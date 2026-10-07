using BatomonHelper.Core;
using BatomonHelper.ScreenReader;
using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Collections.Immutable;

if (args.Length < 2) { Console.Error.WriteLine("Usage: ShoppingBenchmark output.json fixture.png [fixture.png ...]. Input is never enabled."); return 2; }
var root = Environment.CurrentDirectory;
var catalog = (await new SnapshotRepository(Path.Combine(root, "assets/wiki/batomon-wiki.db"), true).GetLatestAsync())!;
var knowledge = JsonSerializer.Deserialize<StrategyKnowledge>(await File.ReadAllTextAsync(Path.Combine(root, "assets/wiki/strategy-knowledge.json")))!;
var fixtures = args.Skip(1).ToList();
var evaluateStrategy = fixtures.Remove("--strategy-eval");
var scenarios = evaluateStrategy ? StrategyScenarios.Create(catalog).ToDictionary(s => "scenario:" + s.Name) : new Dictionary<string, StrategyScenario>();
fixtures.AddRange(scenarios.Keys);
var regionIndex = fixtures.IndexOf("--region");
var region = regionIndex < 0 ? MonsterRegionFilter.All : Enum.Parse<MonsterRegionFilter>(fixtures[regionIndex + 1], true);
if (regionIndex >= 0) fixtures.RemoveRange(regionIndex, 2);
var settingsIndex = fixtures.IndexOf("--settings");
var settings = settingsIndex < 0 ? new ShoppingSettings() : ShoppingSettings.Load(fixtures[settingsIndex + 1]);
if (settingsIndex >= 0) fixtures.RemoveRange(settingsIndex, 2);
using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var planner = new OllamaShoppingPlanner(client, settings, new ShoppingJournal(args[0] + ".jsonl"));
var reader = new WindowsBoardScreenReader();
var measurements = new List<object>();
foreach (var path in fixtures)
{
    var timer = Stopwatch.StartNew();
    BoardSnapshot board;
    ImmutableArray<RecognizedField> fields = [];
    if (scenarios.TryGetValue(path, out var scenario)) board = scenario.Board;
    else if (Path.GetExtension(path).Equals(".json",StringComparison.OrdinalIgnoreCase))
    {
        var history = JsonSerializer.Deserialize<BoardCaptureHistoryEntry>(await File.ReadAllTextAsync(path));
        if (history?.Accepted != true || history.AcceptedSnapshot is null) throw new InvalidDataException("Replay requires accepted V2 capture history.");
        board = history.AcceptedSnapshot; fields = history.Result.Fields;
    }
    else
    {
        using var image = new Bitmap(path);
        var result = await reader.RecognizeImageAsync(image, catalog);
        if (!result.IsShop || result.RequiresReview || result.Snapshot is null)
        { measurements.Add(new { fixture = path, error = "Recognition requires review", result.Fields }); continue; }
        board = result.Snapshot;
    }
    var context = ShoppingAdviceContext.Create(catalog, board.ToShopOddsSnapshot(catalog) with { RegionFilter = region }, board, [], []);
    var o = new ShoppingObservation { Board = board with { RegionFilter = MonsterRegionFilter.All }, Panel = ShoppingPanel.Shop, Resolved = true,
        AdviceContext = context, RunRegion = region,
        ObservedAt = board.CapturedAt, Recognition = fields.Select(f => new RecognitionFact(f.Name,f.Confidence,true,f.Diagnostic)).ToImmutableArray(),
        OfferLevels = board.Shop.Where(s => s.Occupancy == SlotOccupancy.Creature).Select(s => new ObservedOfferLevel(s.Index,s.Level ?? 1,true,s.Level is null ? "Accepted shop capture without LV header: ordinary level 1 offer." : "Captured LV header")).ToImmutableArray() };
    var actions = new LegalActionGenerator().Generate(o, catalog);
    var odds = context.Odds;
    var samples = new List<string>();
    using var sampling = new CancellationTokenSource();
    var sampler = Task.Run(async () =>
    {
        while (!sampling.IsCancellationRequested)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=timestamp,memory.used,memory.total,utilization.gpu --format=csv,noheader,nounits") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
                var sample = await process.StandardOutput.ReadToEndAsync(sampling.Token); await process.WaitForExitAsync(sampling.Token);
                lock (samples) samples.Add(sample.Trim());
                await Task.Delay(500, sampling.Token);
            }
            catch (OperationCanceledException) { break; }
        }
    });
    try
    {
        var decision = await planner.ProposeAsync(new(o, catalog, knowledge, actions, odds, true, null, []) { AdviceContext = context });
        measurements.Add(new { fixture = path, evaluationCriteria = scenario?.Criteria, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds, decision, candidateCount = actions.Length, contextSections = planner.ContextSectionCharacters, telemetry = planner.Telemetry.ToArray(), gpuSamples = samples });
        Console.WriteLine($"{Path.GetFileName(path)}: {decision.Action.Label} ({decision.InferenceMilliseconds:F0} ms)");
    }
    catch (Exception e) { measurements.Add(new { fixture = path, error = e.Message, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds, candidateCount = actions.Length, contextSections = planner.ContextSectionCharacters, telemetry = planner.Telemetry.ToArray(), gpuSamples = samples }); Console.WriteLine(e.Message); }
    finally { sampling.Cancel(); await sampler; }
}
await File.WriteAllTextAsync(args[0], JsonSerializer.Serialize(new { measuredAt = DateTimeOffset.UtcNow, settings, gameRunning = Process.GetProcessesByName("batomon_showdown").Length > 0, measurements }, new JsonSerializerOptions { WriteIndented = true }));
return measurements.Count == 0 ? 1 : 0;

using BatomonHelper.GameReader;
using System.Collections.Immutable;
using System.Text.Json;

namespace BatomonHelper.Capture;

public static class Program
{
    private static readonly string[] ScalarNames =
    [
        "Day", "ShopRank", "Gold", "Wins", "Lives", "ItemUses", "HasEligibleItemOffer",
        "IsShopLocked", "FreeRerolls", "IsOverflow", "TeamCount", "BenchCount", "ShopCount"
    ];

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { PrintUsage(); return 0; }
        var command = args[0].ToLowerInvariant();
        var options = ParseOptions(args.Skip(1).ToArray());
        try
        {
            return command switch
            {
                "baseline" => await BaselineAsync(options),
                "filter" => await FilterAsync(options),
                "observe" => await ObserveAsync(options),
                "pointers" => await PointersAsync(options),
                _ => Fail($"Unknown command '{command}'.")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Capture failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> BaselineAsync(IReadOnlyDictionary<string, string> options)
    {
        var output = Required(options, "out");
        var expected = ReadScalars(options);
        if (expected.Count == 0) throw new ArgumentException("Baseline needs at least one visible scalar, e.g. --gold 10 --shop-rank 1.");
        var install = await RequireInstallAsync();
        using var memory = RequireGameMemory();
        var scanner = new DifferentialScanner();
        Console.WriteLine("Scanning read/write private pages. This can take several seconds and reads no game data to disk.");
        var matches = scanner.ScanBaseline(memory, expected);
        var fields = matches.ToImmutableDictionary(pair => pair.Key, pair => new CapturedField(expected[pair.Key], pair.Value), StringComparer.OrdinalIgnoreCase);
        var session = new CaptureSession(install.BuildId, install.ExecutableSha256, DateTimeOffset.UtcNow, ["baseline"], fields);
        await CaptureSessionStore.SaveAsync(output, session);
        PrintCounts(session);
        return 0;
    }

    private static async Task<int> FilterAsync(IReadOnlyDictionary<string, string> options)
    {
        var path = Required(options, "session");
        var session = await CaptureSessionStore.LoadAsync(path);
        await VerifySessionBuildAsync(session);
        var expected = ReadScalars(options);
        if (expected.Count == 0) throw new ArgumentException("Filter needs the current visible value for at least one field.");
        using var memory = RequireGameMemory();
        var action = options.GetValueOrDefault("action", "controlled-action");
        var next = new DifferentialScanner().Filter(memory, session, expected, action);
        await CaptureSessionStore.SaveAsync(path, next);
        PrintCounts(next);
        return 0;
    }

    private static async Task<int> ObserveAsync(IReadOnlyDictionary<string, string> options)
    {
        var session = await CaptureSessionStore.LoadAsync(Required(options, "session"));
        await VerifySessionBuildAsync(session);
        using var memory = RequireGameMemory();
        var values = new DifferentialScanner().Observe(memory, session);
        Console.WriteLine(JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static async Task<int> PointersAsync(IReadOnlyDictionary<string, string> options)
    {
        var session = await CaptureSessionStore.LoadAsync(Required(options, "session"));
        await VerifySessionBuildAsync(session);
        var fieldName = Required(options, "field");
        var field = session.Fields.FirstOrDefault(pair => pair.Key.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
        if (field.Value is null) throw new ArgumentException($"No captured field named '{fieldName}'.");
        var candidates = field.Value.Candidates;
        var index = options.TryGetValue("candidate-index", out var rawIndex) ? int.Parse(rawIndex) : 0;
        if (candidates.Length != 1 && !options.ContainsKey("candidate-index")) throw new ArgumentException($"{field.Key} has {candidates.Length} candidates. Filter it further or specify --candidate-index.");
        if (index < 0 || index >= candidates.Length) throw new ArgumentOutOfRangeException(nameof(index));
        using var memory = RequireGameMemory();
        Console.WriteLine("Scanning pointers for a module-rooted path. This is read-only and can take minutes for a deep scan.");
        var paths = new PointerPathScanner().FindModuleRootPaths(memory, candidates[index].Address, options.TryGetValue("max-depth", out var rawDepth) ? int.Parse(rawDepth) : 3);
        var output = options.GetValueOrDefault("out", Path.ChangeExtension(Required(options, "session"), $"{field.Key}.pointers.json"));
        await using var stream = File.Create(output);
        await JsonSerializer.SerializeAsync(stream, paths, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"Wrote {paths.Length} module-rooted candidate path(s) to {output}.");
        return 0;
    }

    private static IReadOnlyDictionary<string, int> ReadScalars(IReadOnlyDictionary<string, string> options)
    {
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ScalarNames)
        {
            var option = ToKebab(name);
            if (options.TryGetValue(option, out var raw)) values[name] = ParseInt(raw);
        }
        return values;
    }

    private static async Task<SteamInstall> RequireInstallAsync() => await SteamBuildDetector.DiscoverAsync() ?? throw new InvalidOperationException("Could not locate the installed Batomon Steam build.");
    private static async Task VerifySessionBuildAsync(CaptureSession session)
    {
        var install = await RequireInstallAsync();
        if (session.SteamBuildId != install.BuildId || !session.ExecutableSha256.Equals(install.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Session is {session.SteamBuildId}/{session.ExecutableSha256}, but installed game is {install.BuildId}/{install.ExecutableSha256}. Start a new session.");
    }
    private static WindowsProcessMemory RequireGameMemory() => WindowsProcessMemory.TryOpenGame() ?? throw new InvalidOperationException("batomon_showdown.exe is not running.");
    private static string Required(IReadOnlyDictionary<string, string> options, string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing --{key}.");
    private static int ParseInt(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(value[2..], 16) : int.Parse(value);
    private static int Fail(string message) { Console.Error.WriteLine(message); PrintUsage(); return 2; }
    private static void PrintCounts(CaptureSession session) => Console.WriteLine(string.Join(Environment.NewLine, session.Fields.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}: {pair.Value.Candidates.Length} candidate(s)")));
    private static string ToKebab(string value) => string.Concat(value.Select((character, index) => index > 0 && char.IsUpper(character) ? "-" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Expected an option, got '{args[index]}'.");
            var key = args[index][2..];
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Option --{key} requires a value.");
            options[key] = args[index];
        }
        return options;
    }

    private static void PrintUsage() => Console.WriteLine("""
        BatomonHelper.Capture — external, read-only scanner

        baseline --out captures/shop-baseline.json --gold 10 --shop-rank 1 --day 1 [other visible scalar options]
        filter --session captures/shop-baseline.json --action reroll-1 --gold 7 [current visible values]
        observe --session captures/shop-baseline.json
        pointers --session captures/shop-baseline.json --field Gold [--candidate-index 0] [--max-depth 3] [--out paths.json]

        Scalar options: day, shop-rank, gold, wins, lives, item-uses, has-eligible-item-offer,
        is-shop-locked, free-rerolls, is-overflow, team-count, bench-count, shop-count.
        """);
}

using BatomonHelper.Core;
using BatomonHelper.ScreenReader;
using System.Drawing;
using System.Text.Json;
using System.Diagnostics;
using Xunit.Abstractions;

namespace BatomonHelper.Tests;

public sealed class BoardScreenReaderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1, 10, 0, 300, 30)]
    [InlineData(2, 9, 0, 500, 0)]
    [InlineData(3, 9, 1, 800, 50)]
    public async Task Reads_supplied_board(int day, int lives, int wins, int hp, int gold)
    {
        var catalog = (await new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync())!;
        using var image = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"board-day{day}.png"));
        var reader = new WindowsBoardScreenReader();
        var result = await reader.RecognizeImageAsync(image, catalog);
        Assert.True(result.IsShop);
        var board = result.Snapshot!;
        output.WriteLine(string.Join("\n", result.Fields.Where(field => !field.IsResolved).Select(field => $"{field.Name}: {field.Value} ({field.Confidence:F3})")));
        output.WriteLine("OCR proposals: " + string.Join(", ", result.Fields.Where(field => field.Confidence == .85).Select(field => field.Name)));
        Assert.Equal(day, board.Day); Assert.Equal(lives, board.Lives); Assert.Equal(wins, board.Wins); Assert.Equal(10, board.WinTarget);
        Assert.Equal(hp, board.TeamHp); Assert.Equal(gold, board.Gold);
        Assert.Equal("youngster-m", board.TrainerId);
        Assert.Equal(day, board.ShopRank);
        Assert.Equal(6, board.Team.Length); Assert.Equal(4, board.Bench.Length); Assert.Equal(5, board.Shop.Length);
        Assert.Equal(ShopRankOdds.ForRank(day).OrderBy(o => o.Rarity), board.RarityOdds.OrderBy(o => o.Rarity));
        Assert.Equal(day == 1 ? 3 : day == 2 ? 5 : 8, board.FreeRerolls);
        Assert.Null(board.RerollCost); Assert.False(board.ShopLocked);
        Assert.Equal(day == 2 ? (int?)null : 1, board.ItemUsesRemaining);
        Assert.Equal(day != 2, board.ItemUsesCounterVisible);
        if (day == 2)
        {
            var evidence = JsonSerializer.Deserialize<CaptureHistoryEntry>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "board-day2.v1.json")))!;
            Assert.Equal(1m, evidence.Result.Snapshot!.RarityOdds.Single(odd => odd.Rarity == Rarity.Common).Probability);
            Assert.Equal(.90m, board.RarityOdds.Single(odd => odd.Rarity == Rarity.Common).Probability);
        }
        Assert.Equal(day == 2 ? (int?)null : 1, board.ItemUsesMaximum);
        Assert.Equal(day == 2, result.RequiresReview);
        if (day != 2) Assert.True(BoardSnapshotValidator.Validate(board, catalog).IsValid);
        AssertAnnotatedSlots(board, day);
        var warm = await reader.RecognizeImageAsync(image, catalog);
        output.WriteLine($"Cold {result.Timings}; unchanged {warm.Timings}");
        Assert.Equal(0, warm.Timings.OcrCalls);
        Assert.True(warm.Timings.CacheHits >= 20);
    }

    private static void AssertAnnotatedSlots(BoardSnapshot board, int day)
        => AssertAnnotatedSlots(board, $"board-day{day}");

    private static void AssertAnnotatedSlots(BoardSnapshot board, string stem)
    {
        using var annotation = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{stem}.expected.json")));
        foreach (var (name, slots) in new[] { ("Team", board.Team), ("Bench", board.Bench), ("Shop", board.Shop) })
        {
            var expected = annotation.RootElement.GetProperty(name).EnumerateArray().ToArray();
            Assert.Equal(expected.Length, slots.Length);
            for (var i = 0; i < slots.Length; i++)
            {
                Assert.Equal(i, slots[i].Index);
                var row = expected[i]; var slot = slots[i];
                Assert.Equal(row.GetProperty("Id").GetString(), slot.DefinitionId);
                Assert.Equal(row.GetProperty("Occupancy").GetString(), slot.Occupancy.ToString());
                Assert.Equal(row.GetProperty("Variant").GetString(), slot.Variant.ToString());
                Assert.Equal(row.GetProperty("Level").ValueKind == JsonValueKind.Null ? (int?)null : row.GetProperty("Level").GetInt32(), slot.Level);
                Assert.Equal(row.GetProperty("Price").ValueKind == JsonValueKind.Null ? (int?)null : row.GetProperty("Price").GetInt32(), slot.Price);
                Assert.Equal(row.GetProperty("Stats").EnumerateObject().Select(stat => (stat.Name, (int?)stat.Value.GetInt32())), slot.Stats.Select(stat => (stat.Kind.ToString(), stat.Value)));
                if (slot.Price is not null) Assert.Equal(slot.Price <= board.Gold, slot.Affordable);
            }
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task Reads_treasure_hunter_feedback_without_numeric_corrections(int day)
    {
        var catalog = (await new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync())!;
        var stem = $"treasure-day{day}";
        using var image = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", stem + ".png"));
        var reader = new WindowsBoardScreenReader();
        var result = await reader.RecognizeImageAsync(image, catalog);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, stem + ".actual.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(result.IsShop);
        var unresolved = string.Join("\n", result.Fields.Where(field => !field.IsResolved).Select(field => $"{field.Name}: {field.Value} ({field.Confidence:F3})"));
        output.WriteLine(unresolved);
        Assert.False(result.RequiresReview, unresolved);
        var board = result.Snapshot!;
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", stem + ".expected.json")));
        foreach (var property in new[] { "Day", "Lives", "Wins", "WinTarget", "TeamHp", "Gold", "ShopRank", "RerollCost", "FreeRerolls", "ItemUsesRemaining", "ItemUsesMaximum", "TrainerId", "ShopLocked" })
        {
            var actual = JsonSerializer.SerializeToElement(board).GetProperty(property);
            Assert.Equal(expected.RootElement.GetProperty(property).ToString(), actual.ToString());
        }
        Assert.Equal(expected.RootElement.GetProperty("Odds").EnumerateArray().Select(value => value.GetDecimal() / 100), board.RarityOdds.Select(odd => odd.Probability));
        AssertAnnotatedSlots(board, stem);
        Assert.True(BoardSnapshotValidator.Validate(board, catalog).IsValid);
        var warm = await reader.RecognizeImageAsync(image, catalog);
        Assert.Equal(0, warm.Timings.OcrCalls);
    }

    [Fact]
    public async Task Benchmark_cold_changed_and_unchanged_frames()
    {
        var catalog = (await new SnapshotRepository(Path.Combine(AppContext.BaseDirectory, "Fixtures", "batomon-wiki.db"), true).GetLatestAsync())!;
        using var first = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", "board-day1.png"));
        using var third = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", "board-day3.png"));
        var coldTimer = Stopwatch.StartNew();
        var reader = new WindowsBoardScreenReader();
        var cold = await reader.RecognizeImageAsync(first, catalog);
        coldTimer.Stop();
        var changed = new List<double>(); var unchanged = new List<double>(); var ocrTimes = new List<double>();
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var image = iteration % 2 == 0 ? third : first;
            var refresh = await reader.RecognizeImageAsync(image, catalog);
            changed.Add(refresh.Timings.RecognitionMilliseconds);
            ocrTimes.Add(refresh.Timings.OcrMilliseconds);
            var repeat = await reader.RecognizeImageAsync(image, catalog);
            unchanged.Add(repeat.Timings.RecognitionMilliseconds);
            Assert.False(refresh.RequiresReview, string.Join("\n", refresh.Fields.Where(field => !field.IsResolved).Select(field => $"{field.Name}: {field.Value} ({field.Confidence:F3})"))); Assert.Equal(0, repeat.Timings.OcrCalls);
        }
        static double P95(List<double> samples) => samples.Order().ElementAt((int)Math.Ceiling(samples.Count * .95) - 1);
        using var ninth = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", "treasure-day9.png"));
        using var tenth = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", "treasure-day10.png"));
        var crowded = new List<double>(); var crowdedUnchanged = new List<double>();
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var image = iteration % 2 == 0 ? ninth : tenth;
            var refresh = await reader.RecognizeImageAsync(image, catalog);
            Assert.False(refresh.RequiresReview);
            crowded.Add(refresh.Timings.RecognitionMilliseconds);
            var repeat = await reader.RecognizeImageAsync(image, catalog);
            crowdedUnchanged.Add(repeat.Timings.RecognitionMilliseconds);
            Assert.Equal(0, repeat.Timings.OcrCalls);
        }
        using var fallbackImage = new Bitmap(first);
        using (var graphics = Graphics.FromImage(fallbackImage))
        using (var font = new Font("Arial", 38, FontStyle.Italic, GraphicsUnit.Pixel))
        {
            var region = BoardLayout.Gold.ToPixels(fallbackImage.Width, fallbackImage.Height);
            graphics.FillRectangle(Brushes.Black, region);
            graphics.DrawString("42", font, Brushes.White, region.Location);
        }
        var fallback = await reader.RecognizeImageAsync(fallbackImage, catalog);
        Assert.Contains(fallback.Fields, field => field.Name == "Gold" && !field.IsResolved);
        var report = new { ColdMilliseconds = coldTimer.Elapsed.TotalMilliseconds, ColdRecognitionMilliseconds = cold.Timings.RecognitionMilliseconds, ColdOcrCalls = cold.Timings.OcrCalls, ColdOcrMilliseconds = cold.Timings.OcrMilliseconds,
            ChangedP95Milliseconds = P95(changed), UnchangedP95Milliseconds = P95(unchanged), WarmOcrP95Milliseconds = P95(ocrTimes),
            CrowdedChangedP95Milliseconds = P95(crowded), CrowdedUnchangedP95Milliseconds = P95(crowdedUnchanged),
            NumericFallbackRecognitionMilliseconds = fallback.Timings.RecognitionMilliseconds, NumericFallbackOcrCalls = fallback.Timings.OcrCalls,
            NumericFallbackOcrMilliseconds = fallback.Timings.OcrMilliseconds, NumericFallbackRequiresReview = fallback.RequiresReview,
            ChangedSamples = changed, UnchangedSamples = unchanged, CrowdedChangedSamples = crowded, CrowdedUnchangedSamples = crowdedUnchanged, OcrMilliseconds = ocrTimes };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(json);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "capture-benchmark.json"), json);
    }
}

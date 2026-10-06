using BatomonHelper.Core;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace BatomonHelper.ScreenReader;

public sealed class WindowsBoardScreenReader : IBoardScreenReader
{
    private readonly string _assetRoot;
    private readonly PixelDigits _digits;
    private readonly FastOcr _ocr = new();
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, (ulong Hash, RecognizedField Field)> _cache = [];
    private readonly Dictionary<string, (ulong Hash, BoardSlot Slot, ImmutableArray<RecognizedField> Fields)> _slotCache = [];
    private readonly Dictionary<string, (ulong Hash, SpriteMatch Match)> _spriteCache = [];
    private readonly Dictionary<string, (ulong Hash, ImmutableArray<VisibleStat> Stats, ImmutableArray<RecognizedField> Fields)> _badgeCache = [];
    private SpriteMatcher? _sprites;
    private string? _catalogKey;
    private Size _geometry;
    private int _ocrCalls, _cacheHits, _spriteCacheHits;
    private double _ocrMilliseconds;
    public event Action? FrameCaptured;

    public WindowsBoardScreenReader(string? assetRoot = null)
    {
        _assetRoot = assetRoot ?? AppContext.BaseDirectory;
        _digits = new(Path.Combine(_assetRoot, "assets", "recognition", "digits.json"));
    }
    public async Task<BoardCaptureResult> CaptureAsync(CatalogSnapshot catalog, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var acquisition = Stopwatch.StartNew();
            using var original = await Task.Run(() => CaptureClient(), cancellationToken);
            FrameCaptured?.Invoke();
            acquisition.Stop();
            var result = await Task.Run(() => RecognizeAsync(original, catalog, cancellationToken), cancellationToken);
            return result with { Timings = result.Timings with { AcquisitionMilliseconds = acquisition.Elapsed.TotalMilliseconds } };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { FrameCaptured?.Invoke(); return Failure("capture-failed", exception.Message); }
        finally { _gate.Release(); }
    }
    public async Task<BoardCaptureResult> RecognizeImageAsync(Bitmap image, CatalogSnapshot catalog, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await Task.Run(() => RecognizeAsync(image, catalog, cancellationToken), cancellationToken); }
        finally { _gate.Release(); }
    }
    private async Task<BoardCaptureResult> RecognizeAsync(Bitmap image, CatalogSnapshot catalog, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew(); _ocrCalls = 0; _cacheHits = 0; _spriteCacheHits = 0; _ocrMilliseconds = 0;
        if (!ShopLayout.TryDetectContentBounds(image, out var content, out var failure)) return Failure("unsupported-capture", failure!);
        using var bitmap = image.Clone(content, PixelFormat.Format32bppArgb);
        var source = PixelImage.FromBitmap(bitmap);
        var key = catalog.SnapshotId + catalog.ContentHash;
        if (_catalogKey != key || _geometry != bitmap.Size)
        { _sprites = new(catalog, _assetRoot); _catalogKey = key; _geometry = bitmap.Size; _cache.Clear(); _slotCache.Clear(); _spriteCache.Clear(); _badgeCache.Clear(); }
        var bounds = ShopLayout.DescribeContentBounds(image.Size, content);
        Func<CaptureFrame> encoder = () =>
        {
            using var frame = source.ToBitmap(); using var stream = new MemoryStream(); frame.Save(stream, ImageFormat.Png);
            return new(stream.ToArray(), source.Width, source.Height, bounds);
        };
        if (!LooksLikeShop(source)) return Failure("not-shop", "The frame does not match the shopping screen.") with { FrameEncoder = encoder };
        var fields = ImmutableArray.CreateBuilder<RecognizedField>();
        await PrepareButtonText();
        var trainer = await Trainer();
        var day = await Number("Day", BoardLayout.Day, 1, 999);
        var lives = await Number("Lives", BoardLayout.Lives, 0, 99);
        var winsText = await TextNumber("Wins", BoardLayout.Wins);
        var wins = Pair(winsText, "Wins", BoardLayout.Wins, out var target);
        var hp = await Number("TeamHp", BoardLayout.Hp, 0, 9_999_999);
        var gold = await Number("Gold", BoardLayout.Gold, 0, 9_999);
        var rank = await Number("ShopRank", BoardLayout.Rank, 1, 14, "gray");
        var odds = ImmutableArray.CreateBuilder<CapturedRarityOdds>();
        var palettes = new[] { "gray", "green", "blue", "purple", "orange" };
        for (var i = 0; i < 5; i++)
        {
            var percentage = await Number($"{(Rarity)i}Odds", BoardLayout.Odds[i], 0, 100, palettes[i]);
            if (percentage is not null) odds.Add(new((Rarity)i, percentage.Value / 100m));
        }
        if (odds.Count != 5 || odds.Sum(odd => odd.Probability) != 1)
            for (var i = 0; i < fields.Count; i++) if (fields[i].Name.EndsWith("Odds", StringComparison.Ordinal)) fields[i] = fields[i] with { IsResolved = false, Diagnostic = "Displayed percentages must total 100%; correct this capture." };
        var reroll = await CachedText("Reroll", BoardLayout.Reroll);
        var free = Regex.Replace(reroll.Value, @"\s+", "").Contains("free", StringComparison.OrdinalIgnoreCase);
        var modeRead = free || reroll.Value.Contains('$');
        var rerollNumber = Regex.Match(reroll.Value, @"(?:\$|\()\s*(\d+)");
        var rollCount = rerollNumber.Success && int.TryParse(rerollNumber.Groups[1].Value, out var count) ? (int?)count : null;
        var countRegion = free ? new NormalizedRect(210d / 2048, 1055d / 1152, 74d / 2048, 49d / 1152) : new NormalizedRect(90d / 2048, 1055d / 1152, 145d / 2048, 55d / 1152);
        var counter = _digits.Read(source.Crop(countRegion), family: "hud");
        var counterDigits = Regex.Match(counter.Text, @"^\D*(\d+)\D*$");
        var countResolved = modeRead && counter.Confidence >= .80 && counterDigits.Success && int.TryParse(counterDigits.Groups[1].Value, out _);
        if (countResolved) rollCount = int.Parse(counterDigits.Groups[1].Value);
        fields.Add(new("RerollMode", modeRead ? free ? "Free" : "Paid" : "", .90, modeRead, [new("Free", .5), new("Paid", .5)]) { Region = BoardLayout.Reroll });
        fields.Add(new("RerollCount", rollCount?.ToString() ?? "", countResolved ? counter.Confidence : rollCount is null ? 0 : .70, countResolved, [], countResolved ? null : "Read the free-reroll count or price; numeric OCR requires review.") { Region = countRegion });
        var lockText = await CachedText("ShopLockText", BoardLayout.Lock);
        bool? locked = NormalizeName(lockText.Value) switch { "unlock" => true, "lock" => false, _ => null };
        fields.Add(new("ShopLocked", locked?.ToString() ?? "", locked is null ? 0 : .95, locked is not null, [], locked is null ? "Confirm whether the shop is locked." : null) { Region = BoardLayout.Lock });
        var team = await Slots("Team", BoardLayout.Team, false, gold);
        var bench = await Slots("Bench", BoardLayout.Bench, false, gold);
        var shop = await Slots("Shop", BoardLayout.Shop, true, gold);
        int? uses, maximum;
        var counterVisible = shop.Any(slot => slot.Occupancy == SlotOccupancy.Item);
        fields.Add(new("ItemUsesCounterVisible", counterVisible.ToString(), .95, shop[4].Occupancy != SlotOccupancy.Unknown, [new("True", .5), new("False", .5)]) { Region = BoardLayout.ItemUses });
        if (counterVisible)
        {
            var usesText = await TextNumber("ItemUses", BoardLayout.ItemUses);
            uses = Pair(usesText, "ItemUses", BoardLayout.ItemUses, out maximum);
        }
        else
        {
            // Absence is distinct from a displayed zero. No hidden counter is reconstructed.
            uses = null; maximum = null;
        }
        var board = new BoardSnapshot
        {
            TrainerId = trainer, Day = day, Lives = lives, Wins = wins, WinTarget = target, TeamHp = hp, Gold = gold, ShopRank = rank,
            RarityOdds = odds.ToImmutable(), RerollCost = free ? null : rollCount, FreeRerolls = free ? rollCount : null,
            ItemUsesRemaining = uses, ItemUsesMaximum = maximum, ItemUsesCounterVisible = counterVisible, ShopLocked = locked, Team = team, Bench = bench, Shop = shop, CapturedAt = DateTimeOffset.UtcNow
        };
        return new(board, null, fields.ToImmutable(), _sprites!.MissingAssets.Select(path => new CaptureDiagnostic("missing-asset", $"Missing recognition asset: {path}", false)).ToImmutableArray(),
            new(timer.Elapsed.TotalMilliseconds, _ocrCalls, _cacheHits, _ocrMilliseconds, SpriteCacheHits: _spriteCacheHits)) { FrameEncoder = encoder };

        async Task<string?> Trainer()
        {
            var region = ShopLayout.TrainerHeader; var crop = source.Crop(region); var hash = crop.Fingerprint() ^ source.Crop(BoardLayout.TrainerPortrait).Fingerprint();
            if (_cache.TryGetValue("TrainerId", out var cached) && cached.Hash == hash) { _cacheHits++; fields.Add(cached.Field); return cached.Field.IsResolved ? cached.Field.Value : null; }
            var portrait = _sprites!.MatchTrainer(source.Crop(BoardLayout.TrainerPortrait));
            var raw = portrait.Score >= .86 && portrait.Candidates.Length > 1 && portrait.Score - portrait.Candidates[1].Confidence >= .10 ? "" : await Ocr(source.Crop(new NormalizedRect(.015, .126, .185, .03)));
            var normalized = NormalizeName(raw);
            var matches = catalog.Trainers.Where(entry => NormalizeName(entry.Name) == normalized).ToArray();
            var id = matches.Length == 1 ? matches[0].Id : portrait.Score >= .86 && portrait.Candidates.Length > 1 && portrait.Score - portrait.Candidates[1].Confidence >= .10 ? portrait.Id : null;
            var recognized = new RecognizedField("TrainerId", id ?? "", id is null ? portrait.Score : .95, id is not null,
                portrait.Candidates.Append(new RecognitionCandidate(raw, .85)).ToImmutableArray(), id is null ? "Choose the trainer by title or portrait." : null) { Region = BoardLayout.TrainerPortrait };
            _cache["TrainerId"] = (hash, recognized); fields.Add(recognized); return id;
        }
        async Task<int?> Number(string name, NormalizedRect region, int minimum, int maximumValue, string palette = "light")
        {
            var entry = await TextNumber(name, region, palette);
            var match = Regex.Match(entry.Value, @"^\D*(\d+)\D*$");
            var value = match.Success && int.TryParse(match.Groups[1].Value, out var parsed) && parsed >= minimum && parsed <= maximumValue ? (int?)parsed : null;
            var final = entry with { Value = value?.ToString() ?? "", IsResolved = value is not null && entry.IsResolved,
                Candidates = entry.Candidates.Select(candidate => (Candidate: candidate, Match: Regex.Match(candidate.Value, @"^\D*(\d+)\D*$")))
                    .Where(candidate => candidate.Match.Success).Select(candidate => candidate.Candidate with { Value = candidate.Match.Groups[1].Value }).DistinctBy(candidate => candidate.Value).ToImmutableArray(),
                Diagnostic = value is null ? $"Read {name}." : entry.Diagnostic };
            fields.Add(final); return value;
        }
        async Task<RecognizedField> TextNumber(string name, NormalizedRect region, string palette = "light")
        {
            var crop = source.Crop(region); var hash = crop.Fingerprint();
            if (_cache.TryGetValue(name, out var cached) && cached.Hash == hash) { _cacheHits++; return cached.Field with { Region = region }; }
            if (name.Contains(".Stats[", StringComparison.Ordinal)) crop = BadgeDetector.IsolateDigits(crop);
            var family = name == "ShopRank" ? "rank" : name == "TeamHp" ? "hp" : name is "Day" or "Lives" or "Wins" or "Gold" or "ItemUses" ? "hud" : null;
            var read = _digits.Read(crop, palette, family);
            if (palette == "level")
            {
                // White sparkle strokes beside a shiny creature are not a level digit.
                // Select the actual level palette by ink coverage, not whichever
                // isolated decoration happens to score best against a glyph.
                var colorInk = crop.Mask(c => PixelDigits.Ink(c, "level")).Count(value => value);
                var silverInk = crop.Mask(c => PixelDigits.Ink(c, "silver")).Count(value => value);
                if (silverInk > colorInk) read = _digits.Read(crop, "silver");
            }
            var raw = read.Text; var confidence = read.Confidence;
            // Windows OCR exposes no confidence score. Its fallback remains a reviewable proposal.
            if (confidence < .80 || raw.Length == 0)
            {
                var proposal = await Ocr(crop, palette);
                raw = string.IsNullOrWhiteSpace(proposal) ? read.Text : proposal;
                confidence = string.IsNullOrWhiteSpace(proposal) ? 0 : .70;
            }
            if (name == "ShopRank" && !Regex.IsMatch(raw, @"\d"))
            {
                raw = await Ocr(source.Crop(new NormalizedRect(465d / 2048, 870d / 1152, 28d / 2048, 31d / 1152)), palette);
                confidence = string.IsNullOrWhiteSpace(raw) ? 0 : .70;
                // Keep a reviewable glyph proposal when OCR has no answer; it cannot publish
                // automatically and must never become a rank-table substitute for the ribbon.
                if (string.IsNullOrWhiteSpace(raw)) raw = read.Text;
            }
            var entry = new RecognizedField(name, raw.Replace('O', '0').Replace('o', '0'), confidence, confidence >= .80,
                read.Candidates.Append(new(raw, confidence)).Where(candidate => !string.IsNullOrWhiteSpace(candidate.Value))
                    .DistinctBy(candidate => candidate.Value).ToImmutableArray(), confidence < .80 ? "Unresolved numeric glyphs." : null) { Region = region };
            _cache[name] = (hash, entry); return entry;
        }
        async Task<RecognizedField> CachedText(string name, NormalizedRect region)
        {
            var crop = source.Crop(region); var hash = crop.Fingerprint();
            if (_cache.TryGetValue(name, out var cached) && cached.Hash == hash) { _cacheHits++; return cached.Field; }
            var raw = await Ocr(crop);
            var entry = new RecognizedField(name, raw, .85, raw.Length > 0, []) { Region = region };
            _cache[name] = (hash, entry); return entry;
        }
        async Task PrepareButtonText()
        {
            var missing = new[] { (Name: "Reroll", Region: BoardLayout.Reroll), (Name: "ShopLockText", Region: BoardLayout.Lock) }
                .Select(entry => (entry.Name, entry.Region, Crop: source.Crop(entry.Region)))
                .Select(entry => (entry.Name, entry.Region, entry.Crop, Hash: entry.Crop.Fingerprint()))
                .Where(entry => !_cache.TryGetValue(entry.Name, out var existing) || existing.Hash != entry.Hash).ToArray();
            if (missing.Length == 0) return;
            _ocrCalls++; var elapsed = Stopwatch.StartNew();
            var text = await _ocr.ReadBatchAsync(missing.Select(entry => (entry.Crop, "light")).ToArray(), cancellationToken);
            _ocrMilliseconds += elapsed.Elapsed.TotalMilliseconds;
            for (var i = 0; i < missing.Length; i++)
            {
                var compact = Regex.Replace(text[i], @"\s+", "");
                var understood = missing[i].Name == "Reroll" ? compact.Contains("free", StringComparison.OrdinalIgnoreCase) || compact.Contains('$') : compact.Contains("lock", StringComparison.OrdinalIgnoreCase);
                if (!understood) text[i] = await Ocr(missing[i].Crop);
                _cache[missing[i].Name] = (missing[i].Hash, new(missing[i].Name, text[i], .85, text[i].Length > 0, []) { Region = missing[i].Region });
            }
        }
        int? Pair(RecognizedField text, string prefix, NormalizedRect region, out int? right)
        {
            var match = Regex.Match(text.Value, @"^(\d+)\s*/\s*(\d+)$");
            int? left = match.Success && int.TryParse(match.Groups[1].Value, out var a) ? a : null;
            right = match.Success && int.TryParse(match.Groups[2].Value, out var b) ? b : null;
            var leftName = prefix == "Wins" ? "Wins" : "ItemUsesRemaining";
            var rightName = prefix == "Wins" ? "WinTarget" : "ItemUsesMaximum";
            fields.Add(text with { Name = leftName, Value = left?.ToString() ?? "", IsResolved = left is not null && text.IsResolved, Region = region });
            fields.Add(text with { Name = rightName, Value = right?.ToString() ?? "", IsResolved = right is not null && text.IsResolved, Region = region });
            return left;
        }
        async Task<ImmutableArray<BoardSlot>> Slots(string area, NormalizedRect[] regions, bool isShop, int? currentGold)
        {
            var slots = ImmutableArray.CreateBuilder<BoardSlot>();
            for (var index = 0; index < regions.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var region = regions[index]; var prefix = $"{area}[{index}]";
                var crop = source.Crop(region); var hash = crop.Fingerprint();
                if (isShop) hash ^= source.Crop(BoardLayout.Price(region)).Fingerprint() ^ source.Crop(BoardLayout.Name(region)).Fingerprint();
                if (_slotCache.TryGetValue(prefix, out var cached) && cached.Hash == hash)
                { _cacheHits++; slots.Add(cached.Slot with { Affordable = isShop && cached.Slot.Price is not null && currentGold is not null ? cached.Slot.Price <= currentGold : null }); fields.AddRange(cached.Fields); continue; }
                var start = fields.Count;
                var spriteRegion = BoardLayout.Sprite(region, isShop);
                var sprite = source.Crop(spriteRegion);
                if (!isShop)
                    for (var sy = 0; sy < Math.Min(sprite.Height, source.Height * .023); sy++) for (var sx = 0; sx < Math.Min(sprite.Width, source.Width * .035); sx++)
                    { var p = (sy * sprite.Width + sx) * 4; sprite.Bytes[p] = sprite.Bytes[p + 1] = sprite.Bytes[p + 2] = 255; }
                var spriteHash = sprite.Fingerprint();
                SpriteMatch match;
                if (_spriteCache.TryGetValue(prefix, out var previousSprite) && previousSprite.Hash == spriteHash) { match = previousSprite.Match; _cacheHits++; _spriteCacheHits++; }
                else { match = _sprites!.Match(sprite, source.Width / 640d); _spriteCache[prefix] = (spriteHash, match); }
                var darkCount = sprite.Mask(c => c.R < 65 && c.G < 65 && c.B < 65).Count(pixel => pixel);
                var empty = darkCount < sprite.Width * sprite.Height * .002;
                var kind = empty ? SlotOccupancy.Empty : match.Score >= .80 && match.Margin >= .015 ? match.Kind : SlotOccupancy.Unknown;
                var id = kind is SlotOccupancy.Creature or SlotOccupancy.Item ? match.Id : null;
                if (isShop && kind != SlotOccupancy.Empty && (kind == SlotOccupancy.Unknown || match.Score < .90))
                {
                    var name = await CachedText(prefix + ".Name", BoardLayout.Name(region));
                    var monsters = catalog.Monsters.Where(entry => CreatureNames.Matches(entry, name.Value)).ToArray();
                    var items = catalog.Items.Where(entry => NormalizeName(entry.Name) == NormalizeName(name.Value)).ToArray();
                    // Text alone cannot resolve a mismatching sprite or its shiny status.
                    if (monsters.Length == 1 && monsters[0].Id == match.Id && match.Score >= .76) { kind = SlotOccupancy.Creature; id = match.Id; }
                    if (items.Length == 1 && items[0].Id == match.Id && match.Score >= .76) { kind = SlotOccupancy.Item; id = match.Id; }
                }
                fields.Add(new(prefix + ".Occupant", kind == SlotOccupancy.Empty ? "empty" : id is null ? "" : (kind == SlotOccupancy.Item ? "item:" : "monster:") + id,
                    empty ? .98 : match.Score, kind != SlotOccupancy.Unknown, match.Candidates.Select(candidate => candidate with { Value = (catalog.Items.Any(item => item.Id == candidate.Value) ? "item:" : "monster:") + candidate.Value }).ToImmutableArray(), kind == SlotOccupancy.Unknown ? "Identify this occupant or mark empty." : null) { Region = spriteRegion });
                int? level = null, price = null; var stats = ImmutableArray<VisibleStat>.Empty;
                var variant = kind == SlotOccupancy.Creature ? match.Variant : ShinyState.Unknown;
                if (kind is SlotOccupancy.Creature or SlotOccupancy.Unknown)
                {
                    fields.Add(new(prefix + ".Variant", variant.ToString(), match.Score, variant != ShinyState.Unknown, [new("Normal", .5), new("Shiny", .5)], variant == ShinyState.Unknown ? "Confirm normal or shiny; grayscale may hide its palette." : null) { Region = spriteRegion });
                    if (!isShop)
                    {
                        var levelRegion = BoardLayout.Level(region);
                        level = await Number(prefix + ".Level", levelRegion, 1, 4, "level");
                    }
                    else
                    {
                        var header = BoardLayout.ShopLevelHeader(region);
                        // An absent level label has no implied level. Its outlined LV prefix
                        // establishes visibility independently of the level digit's palette.
                        var label = source.Crop(header);
                        var ink = label.Mask(c => c.R < 65 && c.G < 65 && c.B < 65).Count(value => value);
                        if (ink > label.Width * label.Height * .15)
                            level = await Number(prefix + ".Level", BoardLayout.ShopLevel(region), 1, 4, "level");
                    }
                    stats = await Badges(prefix, BoardLayout.Badges(region, isShop));
                }
                if (isShop && kind != SlotOccupancy.Empty) price = await Number(prefix + ".Price", BoardLayout.Price(region), 0, 9999, "price");
                var slot = new BoardSlot(index, kind, id, variant, level, stats, price, isShop && price is not null && currentGold is not null ? price <= currentGold : null);
                slots.Add(slot); _slotCache[prefix] = (hash, slot, fields.Skip(start).ToImmutableArray());
            }
            return slots.ToImmutable();
        }
        async Task<ImmutableArray<VisibleStat>> Badges(string prefix, NormalizedRect region)
        {
            var crop = source.Crop(region); var hash = crop.Fingerprint();
            if (_badgeCache.TryGetValue(prefix, out var cached) && cached.Hash == hash) { fields.AddRange(cached.Fields); _cacheHits++; return cached.Stats; }
            var firstField = fields.Count; var stats = ImmutableArray.CreateBuilder<VisibleStat>();
            foreach (var badge in BadgeDetector.Find(crop, source.Width / 640d))
            {
                var absolute = new NormalizedRect(region.X + badge.Bounds.X / (double)source.Width,
                    region.Y + badge.Bounds.Y / (double)source.Height, badge.Bounds.Width / (double)source.Width,
                    badge.Bounds.Height / (double)source.Height);
                var value = await Number($"{prefix}.Stats[{stats.Count}].Value", absolute, 0, 9_999_999);
                fields.Add(new($"{prefix}.Stats[{stats.Count}].Kind", badge.Kind.ToString(), .95, true, []) { Region = absolute });
                stats.Add(new(badge.Kind, value));
            }
            // Distinguish a visibly absent badge from an unread or occluded badge, never substitute zero.
            if (stats.Count == 0)
            {
                var background = crop.Mask(c => c.R > 120 && Math.Abs(c.R - c.G) < 15 && Math.Abs(c.G - c.B) < 15).Count(value => value);
                var faces = crop.Mask(c => BadgeKind(c) != VisibleStatKind.Unknown).Count(value => value);
                // Some offers visibly have no badges. A clear neutral band is absence;
                // colored faces or an occluded band still require review.
                if (background < crop.Width * crop.Height * .25 || faces > crop.Width * crop.Height * .01)
                {
                    stats.Add(new(VisibleStatKind.Unknown, null));
                    fields.Add(new(prefix + ".Stats[0].Kind", "Unknown", 0, false, [], "Read the badge type or mark absent.") { Region = region });
                    fields.Add(new(prefix + ".Stats[0].Value", "", 0, false, [], "Read the badge value.") { Region = region });
                }
            }
            var recognized = stats.ToImmutable(); _badgeCache[prefix] = (hash, recognized, fields.Skip(firstField).ToImmutableArray()); return recognized;
        }
        async Task<string> Ocr(PixelImage crop, string palette = "light")
        { _ocrCalls++; var elapsed = Stopwatch.StartNew(); var text = await _ocr.ReadAsync(crop, cancellationToken, palette); _ocrMilliseconds += elapsed.Elapsed.TotalMilliseconds; return text; }
    }
    private static string NormalizeName(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9?]", "");
    internal static VisibleStatKind BadgeKind(Color c)
    {
        if (Distance(c, 239, 66, 107) < 45 || (c.R > 220 && c.G is > 45 and < 135 && c.B is > 80 and < 180)) return VisibleStatKind.Damage;
        if (Distance(c, 248, 144, 40) < 45) return VisibleStatKind.Burn;
        if (c.R > 220 && c.G > 190 && c.B < 110) return VisibleStatKind.Shock;
        if (Distance(c, 33, 140, 165) < 35 || Distance(c, 63, 172, 191) < 35) return VisibleStatKind.Multicast;
        if (c.B > 130 && c.G > 105 && c.R < 115) return VisibleStatKind.Heal;
        if (Distance(c, 152, 160, 216) < 55) return VisibleStatKind.Shield;
        if (c.R > 115 && c.B > 135 && c.G < 130) return VisibleStatKind.Poison;
        return VisibleStatKind.Unknown;
        static int Distance(Color value, int r, int g, int b) => Math.Abs(value.R - r) + Math.Abs(value.G - g) + Math.Abs(value.B - b);
    }
    private static bool LooksLikeShop(PixelImage source)
    {
        var anchors = new[] { new NormalizedRect(.018, .128, .18, .031), new(.26, .128, .42, .031), new(.32, .356, .32, .031) };
        var good = 0;
        for (var i = 0; i < anchors.Length; i++)
        {
            var pixels = source.Crop(anchors[i]);
            var mask = pixels.Mask(c => i == 2 ? c.R > 190 && c.G < 140 && c.B < 160 : c.R > 200 && c.G > 130 && c.B < 90);
            if (mask.Count(value => value) > mask.Length * .50) good++;
        }
        return good == 3;
    }
    private static BoardCaptureResult Failure(string code, string message) => new(null, null, [], [new(code, message, true)], new(0, 0, 0));

    private static Bitmap CaptureClient()
    {
        using var game = Process.GetProcessesByName("batomon_showdown").FirstOrDefault();
        if (game is null) throw new InvalidOperationException("Start Batomon and open its shop before capturing.");
        var window = game.MainWindowHandle;
        if (window == IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window)) throw new InvalidOperationException("Batomon must be visible and not minimized.");
        GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
        if (foreground != (uint)game.Id)
        {
            SetForegroundWindow(window); Thread.Sleep(25);
            GetWindowThreadProcessId(GetForegroundWindow(), out foreground);
            if (foreground != (uint)game.Id) throw new InvalidOperationException("Focus the Batomon shop and capture again.");
        }
        if (!GetClientRect(window, out var rect)) throw new InvalidOperationException("Cannot read the game client.");
        var point = new Point(0, 0); if (!ClientToScreen(window, ref point)) throw new InvalidOperationException("Cannot locate the game client.");
        var bitmap = new Bitmap(rect.Right, rect.Bottom, PixelFormat.Format32bppArgb);
        try { using var graphics = Graphics.FromImage(bitmap); graphics.CopyFromScreen(point.X, point.Y, 0, 0, bitmap.Size); return bitmap; }
        catch { bitmap.Dispose(); throw; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}

using BatomonHelper.Capture;
using BatomonHelper.GameReader;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;

namespace BatomonHelper.Capture.Ui;

public sealed class CaptureViewModel : INotifyPropertyChanged
{
    private static readonly (string Name, string Property)[] FieldMap =
    [
        ("Day", nameof(Day)), ("ShopRank", nameof(ShopRank)), ("Gold", nameof(Gold)), ("Wins", nameof(Wins)), ("Lives", nameof(Lives)),
        ("FreeRerolls", nameof(FreeRerolls)), ("IsShopLocked", nameof(IsShopLocked)), ("TeamCount", nameof(TeamCount)),
        ("BenchCount", nameof(BenchCount)), ("ShopCount", nameof(ShopCount)), ("ItemUses", nameof(ItemUses)), ("IsOverflow", nameof(IsOverflow))
    ];

    private CaptureSession? _session;
    private readonly IScreenValueReader _screenReader = new WindowsScreenValueReader();
    private CaptureStep _step = CaptureStep.Baseline;
    private string _sessionPath = CreateDefaultSessionPath();
    private string _status = "Enter baseline values from the visible shop, then choose Baseline scan.";
    private string _actionName = "reroll-1";
    private string _day = ""; private string _shopRank = ""; private string _gold = ""; private string _wins = ""; private string _lives = "";
    private string _freeRerolls = ""; private string _isShopLocked = ""; private string _teamCount = ""; private string _benchCount = ""; private string _shopCount = "5";
    private string _itemUses = ""; private string _isOverflow = "";
    private FieldRow? _selectedField;

    public CaptureViewModel()
    {
        BaselineCommand = new AsyncCommand(BaselineAsync);
        FilterCommand = new AsyncCommand(FilterAsync);
        LoadSessionCommand = new AsyncCommand(LoadSessionAsync);
        ObserveCommand = new AsyncCommand(ObserveAsync);
        FindPointersCommand = new AsyncCommand(FindPointersAsync);
        ReadScreenCommand = new AsyncCommand(ReadScreenAsync);
        PrimaryCommand = new AsyncCommand(PrimaryAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<FieldRow> CapturedFields { get; } = [];
    public ICommand BaselineCommand { get; }
    public ICommand FilterCommand { get; }
    public ICommand LoadSessionCommand { get; }
    public ICommand ObserveCommand { get; }
    public ICommand FindPointersCommand { get; }
    public ICommand ReadScreenCommand { get; }
    public ICommand PrimaryCommand { get; }
    public string SessionPath { get => _sessionPath; set => Set(ref _sessionPath, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string ActionName { get => _actionName; set => Set(ref _actionName, value); }
    public string Day { get => _day; set => Set(ref _day, value); }
    public string ShopRank { get => _shopRank; set => Set(ref _shopRank, value); }
    public string Gold { get => _gold; set => Set(ref _gold, value); }
    public string Wins { get => _wins; set => Set(ref _wins, value); }
    public string Lives { get => _lives; set => Set(ref _lives, value); }
    public string FreeRerolls { get => _freeRerolls; set => Set(ref _freeRerolls, value); }
    public string IsShopLocked { get => _isShopLocked; set => Set(ref _isShopLocked, value); }
    public string TeamCount { get => _teamCount; set => Set(ref _teamCount, value); }
    public string BenchCount { get => _benchCount; set => Set(ref _benchCount, value); }
    public string ShopCount { get => _shopCount; set => Set(ref _shopCount, value); }
    public string ItemUses { get => _itemUses; set => Set(ref _itemUses, value); }
    public string IsOverflow { get => _isOverflow; set => Set(ref _isOverflow, value); }
    public FieldRow? SelectedField { get => _selectedField; set => Set(ref _selectedField, value); }
    public CaptureStep Step
    {
        get => _step;
        private set
        {
            if (_step == value) return;
            _step = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Step)));
            RaiseGuide();
        }
    }
    public string Progress => CaptureGuide.For(Step).Progress;
    public string NextGameInstruction => CaptureGuide.For(Step).GameInstruction;
    public string PrimaryActionLabel => CaptureGuide.For(Step).PrimaryAction;
    public ObservableCollection<RecognizedFieldRow> RecognizedFields { get; } = [];

    private async Task ReadScreenAsync()
    {
        var game = Process.GetProcessesByName("batomon_showdown").FirstOrDefault()
            ?? throw new InvalidOperationException("Batomon is not running. Start the game and open its shop first.");
        using (game)
        {
            Status = "Reading the visible Batomon client area locally…";
            var result = await _screenReader.ReadAsync(game.Id);
            Application.Current.MainWindow?.Activate();
            RecognizedFields.Clear();
            foreach (var value in result.Values.Values.OrderBy(value => value.Field))
            {
                RecognizedFields.Add(new RecognizedFieldRow(value.Field, value.Value?.ToString() ?? "—", value.Confidence, value.Status));
                if (value.IsValid && value.Value is int number) SetVisibleValue(FieldMap.FirstOrDefault(map => map.Name.Equals(value.Field, StringComparison.OrdinalIgnoreCase)).Property, number.ToString());
            }
            Status = result.Succeeded
                ? $"Read {result.Values.Count} field(s). Review every value marked Needs confirmation, then click {PrimaryActionLabel}."
                : result.Diagnostic!;
        }
    }

    private async Task PrimaryAsync()
    {
        if (Step == CaptureStep.Baseline) await BaselineAsync();
        else if (Step is CaptureStep.Reroll or CaptureStep.Buy or CaptureStep.Lock or CaptureStep.NextDay) await FilterAsync();
        else if (Step == CaptureStep.Resolve) await FindPointersAsync();
        else Status = "Restart Batomon and load a new run. Then create a fresh baseline and repeat the reroll check before accepting a pointer path.";
    }

    private async Task BaselineAsync()
    {
        var expected = ReadValues();
        foreach (var required in new[] { "Day", "ShopRank", "Gold" })
            if (!expected.ContainsKey(required)) throw new InvalidOperationException("Confirm Day, Shop rank, and Gold before saving a baseline.");
        Status = "Scanning the running process read-only…";
        var install = await RequireInstallAsync();
        var matches = await Task.Run(() =>
        {
            using var memory = RequireGameMemory();
            return new DifferentialScanner().ScanBaseline(memory, expected);
        });
        _session = new CaptureSession(install.BuildId, install.ExecutableSha256, DateTimeOffset.UtcNow, ["baseline"],
            matches.ToImmutableDictionary(pair => pair.Key, pair => new CapturedField(expected[pair.Key], pair.Value), StringComparer.OrdinalIgnoreCase));
        SessionPath = CaptureSessionStore.NormalizePath(SessionPath);
        await CaptureSessionStore.SaveAsync(SessionPath, _session);
        RenderSession();
        Step = CaptureStep.Reroll;
        Status = "Baseline saved. " + NextGameInstruction;
    }

    private async Task FilterAsync()
    {
        if (_session is null) await LoadSessionAsync();
        if (_session is null) return;
        var expected = ReadValues();
        if (expected.Count == 0) throw new InvalidOperationException("Enter at least one current visible value before filtering.");
        Status = "Filtering the existing candidates read-only…";
        await VerifyBuildAsync(_session);
        var action = Step switch { CaptureStep.Reroll => "reroll-1", CaptureStep.Buy => "buy-1", CaptureStep.Lock => "lock-1", CaptureStep.NextDay => "next-day-1", _ => string.IsNullOrWhiteSpace(ActionName) ? "controlled-action" : ActionName.Trim() };
        _session = await Task.Run(() =>
        {
            using var memory = RequireGameMemory();
            return new DifferentialScanner().Filter(memory, _session!, expected, action);
        });
        SessionPath = CaptureSessionStore.NormalizePath(SessionPath);
        await CaptureSessionStore.SaveAsync(SessionPath, _session);
        RenderSession();
        Step = CaptureGuide.Next(Step);
        Status = "Filter saved. " + NextGameInstruction;
    }

    private async Task LoadSessionAsync()
    {
        SessionPath = CaptureSessionStore.NormalizePath(SessionPath);
        _session = await CaptureSessionStore.LoadAsync(SessionPath);
        foreach (var (name, property) in FieldMap)
        {
            if (_session.Fields.TryGetValue(name, out var field)) SetVisibleValue(property, field.ExpectedValue.ToString());
        }
        RenderSession();
        Step = InferStep(_session);
        Status = $"Resumed build {_session.SteamBuildId}. {NextGameInstruction}";
    }

    private async Task ObserveAsync()
    {
        if (_session is null) await LoadSessionAsync();
        if (_session is null) return;
        await VerifyBuildAsync(_session);
        var observed = await Task.Run(() =>
        {
            using var memory = RequireGameMemory();
            return new DifferentialScanner().Observe(memory, _session!);
        });
        var readable = observed.Sum(pair => pair.Value.Count(value => value.Value != int.MinValue));
        var total = observed.Sum(pair => pair.Value.Count);
        Status = $"Observed {readable}/{total} saved candidate addresses. No raw process data was stored.";
    }

    private async Task FindPointersAsync()
    {
        if (_session is null) await LoadSessionAsync();
        if (_session is null || SelectedField is null) throw new InvalidOperationException("Select a captured field first.");
        await VerifyBuildAsync(_session);
        var field = _session.Fields[SelectedField.Name];
        if (field.Candidates.Length != 1) throw new InvalidOperationException($"{SelectedField.Name} has {field.Candidates.Length} candidates. Filter it until exactly one remains.");
        Status = "Searching module-rooted pointer paths. This can take several minutes and remains read-only…";
        var paths = await Task.Run(() =>
        {
            using var memory = RequireGameMemory();
            return new PointerPathScanner().FindModuleRootPaths(memory, field.Candidates[0].Address);
        });
        SessionPath = CaptureSessionStore.NormalizePath(SessionPath);
        var output = Path.Combine(Path.GetDirectoryName(SessionPath)!, $"{SelectedField.Name}.paths.json");
        await using var stream = File.Create(output);
        await JsonSerializer.SerializeAsync(stream, paths, new JsonSerializerOptions { WriteIndented = true });
        Status = $"Wrote {paths.Length} module-rooted candidate path(s) to {output}. Test a candidate after a game restart before using it in a profile.";
    }

    private Dictionary<string, int> ReadValues()
    {
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, property) in FieldMap)
        {
            var raw = GetVisibleValue(property);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!int.TryParse(raw, out var value)) throw new InvalidOperationException($"{name} must be an integer.");
            if (!ScreenValueParser.IsValid(name, value)) throw new InvalidOperationException($"{name} is outside the supported visible range. Correct it before continuing.");
            values[name] = value;
        }
        if (values.TryGetValue("TeamCount", out var team) && values.TryGetValue("BenchCount", out var bench) && team + bench > 20)
            throw new InvalidOperationException("Team and bench counts cannot total more than 20.");
        return values;
    }

    private void RenderSession()
    {
        CapturedFields.Clear();
        if (_session is null) return;
        foreach (var pair in _session.Fields.OrderBy(pair => pair.Key)) CapturedFields.Add(new(pair.Key, pair.Value.ExpectedValue, pair.Value.Candidates.Length));
        SelectedField ??= CapturedFields.FirstOrDefault();
    }

    private static CaptureStep InferStep(CaptureSession session)
    {
        if (session.Actions.Any(action => action.StartsWith("next-day", StringComparison.OrdinalIgnoreCase))) return CaptureStep.Resolve;
        if (session.Actions.Any(action => action.StartsWith("lock", StringComparison.OrdinalIgnoreCase))) return CaptureStep.NextDay;
        if (session.Actions.Any(action => action.StartsWith("buy", StringComparison.OrdinalIgnoreCase))) return CaptureStep.Lock;
        if (session.Actions.Any(action => action.StartsWith("reroll", StringComparison.OrdinalIgnoreCase))) return CaptureStep.Buy;
        return CaptureStep.Reroll;
    }

    private static async Task<SteamInstall> RequireInstallAsync() => await SteamBuildDetector.DiscoverAsync() ?? throw new InvalidOperationException("The installed Batomon Steam build was not found.");
    private static WindowsProcessMemory RequireGameMemory() => WindowsProcessMemory.TryOpenGame() ?? throw new InvalidOperationException("batomon_showdown.exe is not running.");
    private static string CreateDefaultSessionPath()
    {
        var dataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(dataRoot)) dataRoot = Path.GetTempPath();
        return CaptureSessionStore.NormalizePath(Path.Combine(dataRoot, "BatomonHelper", "captures", "shop.json"));
    }
    private static async Task VerifyBuildAsync(CaptureSession session)
    {
        var install = await RequireInstallAsync();
        if (session.SteamBuildId != install.BuildId || !session.ExecutableSha256.Equals(install.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The installed game build/hash differs from this session. Start a new baseline.");
    }

    private string GetVisibleValue(string property) => property switch
    {
        nameof(Day) => Day, nameof(ShopRank) => ShopRank, nameof(Gold) => Gold, nameof(Wins) => Wins, nameof(Lives) => Lives,
        nameof(FreeRerolls) => FreeRerolls, nameof(IsShopLocked) => IsShopLocked, nameof(TeamCount) => TeamCount, nameof(BenchCount) => BenchCount,
        nameof(ShopCount) => ShopCount, nameof(ItemUses) => ItemUses, nameof(IsOverflow) => IsOverflow, _ => string.Empty
    };
    private void SetVisibleValue(string property, string value)
    {
        switch (property)
        {
            case nameof(Day): Day = value; break; case nameof(ShopRank): ShopRank = value; break; case nameof(Gold): Gold = value; break;
            case nameof(Wins): Wins = value; break; case nameof(Lives): Lives = value; break; case nameof(FreeRerolls): FreeRerolls = value; break;
            case nameof(IsShopLocked): IsShopLocked = value; break; case nameof(TeamCount): TeamCount = value; break; case nameof(BenchCount): BenchCount = value; break;
            case nameof(ShopCount): ShopCount = value; break; case nameof(ItemUses): ItemUses = value; break; case nameof(IsOverflow): IsOverflow = value; break;
        }
    }
    private void RaiseGuide()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NextGameInstruction)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrimaryActionLabel)));
    }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (!EqualityComparer<T>.Default.Equals(field, value)) { field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); } }
}

public sealed record FieldRow(string Name, int Expected, int CandidateCount);
public sealed record RecognizedFieldRow(string Field, string Value, double Confidence, string Status);

public sealed class AsyncCommand(Func<Task> action) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running;
    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await action(); }
        catch (Exception exception)
        {
            MessageBox.Show(exception.ToString(), "Capture error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}

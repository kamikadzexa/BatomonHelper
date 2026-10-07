using BatomonHelper.Core;
using BatomonHelper.ScreenReader;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace BatomonHelper.Overlay;

public sealed record AdviceMoveRow(string Label, string Reason);

public sealed partial class OverlayViewModel
{
    private ShoppingAdviceSession? _shopping;
    private ShoppingObservation? _latestShoppingCapture;
    private bool _shoppingCaptureInProgress;
    private readonly HttpClient _shoppingHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private string _shoppingStatus = "Loading local advice…";
    private string _decisionText = "Capture with F9, then press F10 for a short plan.";
    private string _decisionReason = "";
    private string _decisionObjective = "";
    private string _decisionExpected = "";
    private string _decisionNextStep = "";
    private string _adviceCaptureLabel = "No accepted shop capture yet.";
    private string? _hotkeyConflict;
    public ObservableCollection<AdviceMoveRow> AdviceMoves { get; } = [];
    public ObservableCollection<string> AdviceEvidenceLinks { get; } = [];
    private string _decisionEvidence = "";
    private string _decisionAssumptions = "";
    public string DecisionEvidence { get => _decisionEvidence; private set => Set(ref _decisionEvidence, value); }
    public string DecisionAssumptions { get => _decisionAssumptions; private set => Set(ref _decisionAssumptions, value); }
    public ShoppingSettings ShoppingConfiguration { get; private set; } = new();
    public ICommand SuggestShoppingCommand { get; private set; } = null!;
    public ICommand ShoppingSettingsCommand { get; private set; } = null!;
    public string ShoppingStatus { get => _shoppingStatus; private set => Set(ref _shoppingStatus, value); }
    public string DecisionText { get => _decisionText; private set => Set(ref _decisionText, value); }
    public string DecisionReason { get => _decisionReason; private set => Set(ref _decisionReason, value); }
    public string DecisionObjective { get => _decisionObjective; private set => Set(ref _decisionObjective, value); }
    public string DecisionExpected { get => _decisionExpected; private set => Set(ref _decisionExpected, value); }
    public string DecisionNextStep { get => _decisionNextStep; private set => Set(ref _decisionNextStep, value); }
    public string AdviceCaptureLabel { get => _adviceCaptureLabel; private set => Set(ref _adviceCaptureLabel, value); }
    public string ShoppingModelLabel => $"Local: {ShoppingConfiguration.Model} · {ShoppingConfiguration.ContextTokens / 1024}K context";
    private static string ShoppingSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatomonHelper", "shopping-settings.json");
    private void InitializeShoppingCommands()
    {
        SuggestShoppingCommand = new AsyncCommand(SuggestShoppingAsync);
        ShoppingSettingsCommand = new AsyncCommand(EditShoppingSettingsAsync);
    }
    private async Task InitializeShoppingAsync()
    {
        try
        {
            ShoppingConfiguration = ShoppingSettings.Load(ShoppingSettingsPath); ShoppingConfiguration.Validate();
            var knowledgePath = Path.Combine(AppContext.BaseDirectory, "assets", "wiki", "strategy-knowledge.json");
            var knowledge = File.Exists(knowledgePath) ? JsonSerializer.Deserialize<StrategyKnowledge>(await File.ReadAllTextAsync(knowledgePath)) ?? StrategyKnowledge.Empty : StrategyKnowledge.Empty;
            var journal = new ShoppingJournal(Path.Combine(Path.GetDirectoryName(ShoppingSettingsPath)!, "shopping-journal", DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl"));
            _shopping = new(new LegalActionGenerator(), new OllamaShoppingPlanner(_shoppingHttp, ShoppingConfiguration, journal), journal, _catalog, knowledge);
            _shopping.Changed += ShoppingChanged;
            ShoppingStatus = _hotkeyConflict ?? "Ready. F9 captures the shop; F10 suggests a move from that capture.";
            OnPropertyChanged(nameof(ShoppingModelLabel));
        }
        catch (Exception error) { ShoppingStatus = "Local advice unavailable: " + error.Message; }
    }
    public async Task SuggestShoppingAsync()
    {
        if (_shoppingCaptureInProgress) { ShoppingStatus = "Wait for the F9 capture to finish, then press F10."; return; }
        if (_latestShoppingCapture is not { } observation)
        { ShoppingStatus = "Capture and accept the shop with F9 first."; return; }
        if (_shopping is not null) await _shopping.SuggestAsync(observation with
        { AdviceContext = _adviceContext, RunRegion = _adviceContext?.Snapshot.RegionFilter });
    }
    private void AcceptShoppingCapture(BoardSnapshot board, BoardCaptureResult result)
    {
        _shopping?.Cancel("New capture accepted. Press F10 for advice.");
        _latestShoppingCapture = new()
        {
            Board = board with { RegionFilter = MonsterRegionFilter.All }, Panel = ShoppingPanel.Shop, Resolved = true,
            ObservedAt = board.CapturedAt, InventoryResolved = false,
            OfferLevels = board.Shop.Where(s => s.Occupancy == SlotOccupancy.Creature).Select(s => new ObservedOfferLevel(s.Index, s.Level ?? 1, true,
                s.Level is null ? "Accepted shop capture without LV header: ordinary level 1 offer." : "Captured LV header")).ToImmutableArray(),
            Recognition = result.Fields.Select(f => new RecognitionFact(f.Name, f.Confidence, true, f.Diagnostic)).ToImmutableArray()
        };
        AdviceCaptureLabel = $"Latest capture: {board.CapturedAt.ToLocalTime():HH:mm:ss} · day {board.Day} · ${board.Gold}. Backpack, sale values and merge progress are unknown unless captured; affected moves are excluded.";
    }
    private void CancelShoppingAdvice(string message)
    {
        _shopping?.Cancel(message);
        if (_shopping is null) ShoppingStatus = message;
    }
    public void ReportHotkeyConflict(string message) { _hotkeyConflict = message; ShoppingStatus = message; }
    private void StopShopping() => _shopping?.Cancel();
    private void ShoppingChanged(ShoppingSessionState state)
    {
        // Planning resumes on the UI context; cancellation/new capture clears old text immediately.
        ShoppingStatus = state.Message;
        AdviceMoves.Clear();
        AdviceEvidenceLinks.Clear();
        if (state.Decision is { } decision)
        {
            AdviceMoves.Add(new("1. " + DescribeMove(decision.Action), decision.Reason));
            if (decision.SecondMove is { } second) AdviceMoves.Add(new("2. " + DescribeMove(second.Action), second.Reason));
        }
        DecisionText = state.Decision is { } d ? d.Action.Label +
            (d.Action.Source is { Area: SlotArea.Shop } source ? $" · shop slot {source.Index + 1}" : "") +
            (d.Action.Cost > 0 && d.Action.Kind is ActionKind.BuyCreature or ActionKind.BuyItem or ActionKind.ChooseReward or ActionKind.ChooseEvent ? $" · ${d.Action.Cost}" : "")
            : "Capture with F9, then press F10 for a short plan.";
        DecisionReason = state.Decision?.Reason ?? "";
        DecisionObjective = state.Decision is null ? "" : "Strategy goal: " + state.Decision.Objective;
        DecisionExpected = state.Decision is null ? "" : "Expected result: " + state.Decision.Action.ExpectedResult;
        if (state.Decision?.SecondMove is { } next) DecisionExpected += "\nSecond move: " + next.Action.ExpectedResult;
        DecisionNextStep = state.Decision is null ? "" : "Likely next: " + state.Decision.NextStep;
        DecisionAssumptions = state.Decision is null ? "" : string.Join("\n", _adviceContext?.Assumptions ?? []);
        DecisionEvidence = state.Decision is null ? "" : string.Join("\n", state.Decision.References
            .Concat(state.Decision.SecondMove?.References ?? []).Distinct().Select(EvidenceUrl));
        if (state.Decision is not null)
            foreach (var url in state.Decision.References.Concat(state.Decision.SecondMove?.References ?? []).Distinct().Select(EvidenceUrl).Distinct())
                AdviceEvidenceLinks.Add(url);
    }
    private static string DescribeMove(GameAction action) => action.Label +
        (action.Source is { Area: SlotArea.Shop } source ? $" · shop slot {source.Index + 1}" : "") +
        (action.Cost > 0 ? $" · ${action.Cost}" : "");
    private string EvidenceUrl(string id) => "https://batomon.com/" +
        (id.StartsWith("pairs/") ? "meta/pairs" : id.StartsWith("comps/") ? "comps" :
         _catalog.Monsters.Any(m => m.Id == id) ? "batomon/" + id : _catalog.Items.Any(i => i.Id == id) ? "items/" + id :
         _catalog.Trinkets.Any(t => t.Id == id) ? "trinkets/" + id : _catalog.Trainers.Any(t => t.Id == id) ? "trainers/" + id : id);
    private async Task EditShoppingSettingsAsync()
    {
        CancelShoppingAdvice("Model settings opened.");
        var dialog = new ShoppingSettingsWindow(ShoppingConfiguration) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.AcceptedSettings is null) return;
        await File.WriteAllTextAsync(ShoppingSettingsPath, JsonSerializer.Serialize(dialog.AcceptedSettings, new JsonSerializerOptions { WriteIndented = true }));
        await InitializeShoppingAsync();
    }
}

using BatomonHelper.Core;
using BatomonHelper.ScreenReader;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace BatomonHelper.Overlay;

public sealed partial class OverlayViewModel
{
    private ShoppingAdviceSession? _shopping;
    private ShoppingObservation? _latestShoppingCapture;
    private bool _shoppingCaptureInProgress;
    private readonly HttpClient _shoppingHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private string _shoppingStatus = "Loading local advice…";
    private string _decisionText = "Capture with F9, then press F10 for one suggested move.";
    private string _decisionReason = "";
    private string _decisionObjective = "";
    private string _decisionExpected = "";
    private string _decisionNextStep = "";
    private string _adviceCaptureLabel = "No accepted shop capture yet.";
    private string? _hotkeyConflict;
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
        if (_shopping is not null) await _shopping.SuggestAsync(observation);
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
        DecisionText = state.Decision is { } d ? d.Action.Label +
            (d.Action.Source is { Area: SlotArea.Shop } source ? $" · shop slot {source.Index + 1}" : "") +
            (d.Action.Cost > 0 && d.Action.Kind is ActionKind.BuyCreature or ActionKind.BuyItem or ActionKind.ChooseReward or ActionKind.ChooseEvent ? $" · ${d.Action.Cost}" : "")
            : "Capture with F9, then press F10 for one suggested move.";
        DecisionReason = state.Decision?.Reason ?? "";
        DecisionObjective = state.Decision is null ? "" : "Goal: " + state.Decision.Objective;
        DecisionExpected = state.Decision is null ? "" : "Expected result: " + state.Decision.Action.ExpectedResult;
        DecisionNextStep = state.Decision is null ? "" : "Then: " + state.Decision.NextStep;
    }
    private async Task EditShoppingSettingsAsync()
    {
        CancelShoppingAdvice("Model settings opened.");
        var dialog = new ShoppingSettingsWindow(ShoppingConfiguration) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.AcceptedSettings is null) return;
        await File.WriteAllTextAsync(ShoppingSettingsPath, JsonSerializer.Serialize(dialog.AcceptedSettings, new JsonSerializerOptions { WriteIndented = true }));
        await InitializeShoppingAsync();
    }
}

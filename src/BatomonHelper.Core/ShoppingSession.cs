using System.Collections.Immutable;
using System.Text.Json;

namespace BatomonHelper.Core;

public enum ShoppingStatus { Idle, Planning, Suggested, Cancelled }
public sealed record ShoppingSessionState(ShoppingStatus Status, string Message, ShoppingDecision? Decision = null);
public sealed record ShoppingJournalEntry(string Kind, DateTimeOffset At, string? Revision,
    ShoppingObservation? Observation = null, ShoppingDecision? Decision = null, string? Message = null);
public interface IShoppingJournal
{
    Task AppendAsync(ShoppingJournalEntry entry, CancellationToken cancellationToken = default);
}
public sealed class ShoppingJournal(string path) : IShoppingJournal
{
    private readonly SemaphoreSlim _gate = new(1);
    public async Task AppendAsync(ShoppingJournalEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry) + Environment.NewLine, cancellationToken);
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Text advice from an accepted capture. No observer or game input adapter is attached.</summary>
public sealed class ShoppingAdviceSession(ILegalActionGenerator legal, IShoppingPlanner planner,
    IShoppingJournal journal, CatalogSnapshot catalog, StrategyKnowledge knowledge)
{
    private readonly SemaphoreSlim _gate = new(1);
    private CancellationTokenSource _operation = new();
    private string? _objective;
    private BoardSnapshot? _previousBoard;
    public ShoppingSessionState State { get; private set; } = new(ShoppingStatus.Idle, "F9 captures the shop; F10 suggests a move.");
    public event Action<ShoppingSessionState>? Changed;
    public void Cancel(string message = "Advice cancelled.")
    {
        _operation.Cancel();
        Set(new(ShoppingStatus.Cancelled, message));
    }
    public async Task SuggestAsync(ShoppingObservation observation)
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            _operation.Dispose(); _operation = new();
            var token = _operation.Token;
            if (!observation.Resolved || observation.Panel != ShoppingPanel.Shop || observation.Board is not { } board)
                throw new InvalidOperationException("Accept a shopping capture with F9 before asking for advice.");
            var candidates = legal.Generate(observation, catalog);
            if (candidates.IsEmpty) throw new InvalidOperationException("The capture needs more information before a legal move can be suggested.");
            if (_previousBoard is { } previous && (previous.TrainerId != board.TrainerId || board.Day < previous.Day)) _objective = null;
            var projection = (board with { RegionFilter = observation.RunRegion ?? MonsterRegionFilter.All }).ToShopOddsSnapshot(catalog);
            var odds = new OddsCalculator().Calculate(catalog, projection, new OddsCalculationOptions(
                board.Team.Concat(board.Bench).Where(s => s.Level >= 3).Select(s => s.DefinitionId!).ToHashSet(),
                observation.Inventory.Where(i => i.IsTrinket && i.Resolved && i.Quantity > 0).Select(i => i.DefinitionId).ToHashSet()));
            Set(new(ShoppingStatus.Planning, $"Considering the capture from {board.CapturedAt.ToLocalTime():HH:mm:ss}…"));
            var decision = await planner.ProposeAsync(new(observation, catalog, knowledge, candidates, odds,
                observation.RunRegion is null || !observation.InventoryResolved, _objective, []), token);
            token.ThrowIfCancellationRequested();
            if (decision.Revision != observation.Revision || !candidates.Any(a => a.SameMove(decision.Action)))
                throw new InvalidDataException("The model suggested an action outside this capture's legal moves.");
            if (string.IsNullOrWhiteSpace(decision.NextStep)) throw new InvalidDataException("The model did not explain the expected next step.");
            await journal.AppendAsync(new("advice", DateTimeOffset.UtcNow, observation.Revision, observation, decision), token);
            token.ThrowIfCancellationRequested();
            _objective = decision.Objective; _previousBoard = board;
            Set(new(ShoppingStatus.Suggested, $"Advice for capture {board.CapturedAt.ToLocalTime():HH:mm:ss}. After changing the game, use F9 then F10 again.", decision));
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_operation.IsCancellationRequested)
            {
                Set(new(ShoppingStatus.Idle, error.Message));
                try { await journal.AppendAsync(new("advice-error", DateTimeOffset.UtcNow, observation.Revision, Message: error.ToString())); }
                catch (IOException) { }
            }
        }
        finally { _gate.Release(); }
    }
    private void Set(ShoppingSessionState state) { State = state; Changed?.Invoke(state); }
}

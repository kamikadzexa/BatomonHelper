using BatomonHelper.Core;

namespace BatomonHelper.ScreenReader;

public sealed record AcceptedBoardState(BoardSnapshot Board, ShopOddsSnapshot ShopOdds);

/// <summary>The accepted board and its odds projection are published as one immutable value.</summary>
public sealed class BoardCaptureState
{
    private AcceptedBoardState? _accepted;
    public AcceptedBoardState? Accepted => Volatile.Read(ref _accepted);
    public AcceptedBoardState Publish(BoardCaptureResult proposal, CatalogSnapshot catalog, BoardSnapshot? corrected = null)
    {
        if (!proposal.IsShop || proposal.Snapshot is null) throw new InvalidOperationException("The capture is not a supported shop.");
        if (proposal.RequiresReview && corrected is null) throw new InvalidOperationException("Resolve the uncertain fields before accepting this board.");
        var board = corrected ?? proposal.Snapshot;
        var state = new AcceptedBoardState(board, board.ToShopOddsSnapshot(catalog));
        Interlocked.Exchange(ref _accepted, state);
        return state;
    }
}

/// <summary>One active capture and at most one pending refresh, including presses during review.</summary>
public sealed class CaptureRefreshQueue(Func<CancellationToken, Task> refresh, CancellationToken cancellationToken = default)
{
    private readonly object _sync = new();
    private TaskCompletionSource? _active;
    private bool _pending;
    public Task RequestAsync()
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_active is not null) { _pending = true; return _active.Task; }
            completion = _active = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = RunAsync(completion);
        return completion.Task;
    }
    private async Task RunAsync(TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await refresh(cancellationToken);
                lock (_sync)
                {
                    if (_pending && !cancellationToken.IsCancellationRequested) { _pending = false; continue; }
                    _active = null; _pending = false; completion.SetResult(); return;
                }
            }
        }
        catch (OperationCanceledException) { Finish(); completion.TrySetCanceled(cancellationToken); }
        catch (Exception exception) { Finish(); completion.TrySetException(exception); }
        void Finish() { lock (_sync) { _active = null; _pending = false; } }
    }
}

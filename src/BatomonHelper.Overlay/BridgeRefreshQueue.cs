namespace BatomonHelper.Overlay;

/// <summary>Coalesces bridge refresh requests so polling never overlaps an active read.</summary>
public sealed class BridgeRefreshQueue(Func<CancellationToken, Task> refresh, CancellationToken cancellationToken = default)
{
    private readonly object _sync = new();
    private TaskCompletionSource? _active;
    private bool _pending;

    public Task RequestAsync()
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_active is not null)
            {
                _pending = true;
                return _active.Task;
            }

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
                    if (_pending && !cancellationToken.IsCancellationRequested)
                    {
                        _pending = false;
                        continue;
                    }

                    _active = null;
                    _pending = false;
                    completion.SetResult();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Finish();
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            Finish();
            completion.TrySetException(exception);
        }

        void Finish()
        {
            lock (_sync)
            {
                _active = null;
                _pending = false;
            }
        }
    }
}

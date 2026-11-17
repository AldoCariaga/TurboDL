namespace TurboDL.App.Services;

/// <summary>
/// Lets up to <see cref="Limit"/> host pages be solved at once (each in its own browser), in the order they asked. The
/// limit can change while pages wait or run (Settings): a higher one lets waiting pages in straight away, a lower one
/// lets running pages finish and holds the next ones until fewer run.
/// </summary>
internal sealed class SolvingGate(int limit)
{
    private readonly Lock _lock = new();
    private readonly LinkedList<TaskCompletionSource> _waiting = [];
    private int _running;
    private int _limit = Math.Max(1, limit);

    public int Limit
    {
        get { lock (_lock) return _limit; }
        set
        {
            lock (_lock) _limit = Math.Max(1, value);
            Pump();
        }
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        TaskCompletionSource entry;
        LinkedListNode<TaskCompletionSource> node;
        lock (_lock)
        {
            if (_running < _limit && _waiting.Count == 0)
            {
                _running++;
                return;
            }
            entry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiting.AddLast(entry);
        }
        using (ct.Register(() =>
               {
                   bool removed;
                   lock (_lock)
                   {
                       removed = node.List is not null;
                       if (removed) _waiting.Remove(node);
                   }
                   if (removed) entry.TrySetCanceled(ct);
               }))
        {
            await entry.Task;
        }
    }

    public void Release()
    {
        lock (_lock) _running--;
        Pump();
    }

    /// <summary>Lets waiting pages in while there's room.</summary>
    private void Pump()
    {
        while (true)
        {
            TaskCompletionSource next;
            lock (_lock)
            {
                if (_running >= _limit || _waiting.First is not { } first) return;
                _waiting.RemoveFirst();
                _running++;
                next = first.Value;
            }
            next.TrySetResult();
        }
    }
}

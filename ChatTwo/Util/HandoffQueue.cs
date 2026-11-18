using System.Collections.Concurrent;

namespace ChatTwo.Util;

/// <summary>
/// First-in first-out handoff from one producer thread to one consumer thread; the consumer blocks while it is empty
/// instead of polling.
/// </summary>
/// <remarks>
/// Items are dequeued lock-free; the event only puts an idle consumer to sleep. Pure on purpose (no Dalamud) so
/// ordering and wakeups can be checked offline. The event is never disposed: a consumer that outlives a timed-out
/// join must still find it usable.
/// </remarks>
public sealed class HandoffQueue<T>
{
    private readonly ConcurrentQueue<T> Items = new();
    private readonly ManualResetEventSlim Queued = new(false);

    /// <summary>Moves everything out of <paramref name="source"/>, in order, and wakes the consumer once.</summary>
    public int MoveFrom(Queue<T> source)
    {
        var moved = 0;
        while (source.TryDequeue(out var item))
        {
            Items.Enqueue(item);
            moved++;
        }

        if (moved > 0)
            Queued.Set();

        return moved;
    }

    /// <summary>Takes the oldest item, blocking while there is none; false once <paramref name="token"/> is cancelled.</summary>
    public bool TryTake(out T item, CancellationToken token)
    {
        while (true)
        {
            if (Items.TryDequeue(out item!))
                return true;

            // Reset before looking again: an item queued after that look is followed by a Set, so Wait cannot sleep through it.
            Queued.Reset();
            if (Items.TryDequeue(out item!))
                return true;

            try
            {
                Queued.Wait(token);
            }
            catch (OperationCanceledException)
            {
                item = default!;
                return false;
            }
        }
    }
}

using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace ChatTwo.History;

/// <summary>
/// Runs database jobs one at a time on the thread pool, each on a connection of its own. Submitting a job makes every
/// earlier one stale: a queued stale job is skipped, a running one is interrupted. A job checks <see cref="IsCurrent"/>
/// before publishing, so a result never replaces a newer one.
/// </summary>
internal sealed class HistoryLane
{
    private readonly Func<SqliteConnection> Open;
    private readonly Action<Exception> OnError;
    private readonly SemaphoreSlim Gate = new(1, 1);
    private readonly object ActiveLock = new();
    private SqliteConnection? Active;
    private long Generation;
    private volatile bool Disposed;
    private Task Last = Task.CompletedTask;

    public HistoryLane(Func<SqliteConnection> open, Action<Exception> onError)
    {
        Open = open;
        OnError = onError;
    }

    public bool IsCurrent(long generation) => !Disposed && Interlocked.Read(ref Generation) == generation;

    public void Submit(Action<SqliteConnection, long> job)
    {
        if (Disposed)
            return;

        var generation = Interlocked.Increment(ref Generation);
        Interrupt();
        Last = Task.Run(() => Run(job, generation));
    }

    /// <summary>Stops the running job and waits for it a short while, so no job outlives the plugin by much.</summary>
    public void Dispose(int waitMs) => DisposeAll(waitMs, this);

    /// <summary>
    /// Stops every lane before waiting for any, then waits for all of them together: the waits overlap instead of
    /// adding up, and each lane still gets at least <paramref name="waitMs"/> after its own interrupt.
    /// </summary>
    public static void DisposeAll(int waitMs, params HistoryLane[] lanes)
    {
        foreach (var lane in lanes)
        {
            lane.Disposed = true;
            Interlocked.Increment(ref lane.Generation);
            lane.Interrupt();
        }

        try
        {
            Task.WaitAll(lanes.Select(lane => lane.Last).ToArray(), waitMs);
        }
        catch (Exception)
        {
            // Run catches everything itself; nothing a wait could add.
        }
    }

    private async Task Run(Action<SqliteConnection, long> job, long generation)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsCurrent(generation))
                return;

            using var conn = Open();
            lock (ActiveLock)
                Active = conn;

            try
            {
                // Submitted between the check above and Active being set: that Submit found nothing to interrupt.
                if (IsCurrent(generation))
                    job(conn, generation);
            }
            finally
            {
                lock (ActiveLock)
                    Active = null;
            }
        }
        catch (Exception ex)
        {
            if (IsCurrent(generation) && !IsInterrupt(ex))
                OnError(ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    // Held under ActiveLock so the handle cannot be closed while it is interrupted.
    private void Interrupt()
    {
        lock (ActiveLock)
        {
            if (Active?.Handle is { } handle)
                raw.sqlite3_interrupt(handle);
        }
    }

    private static bool IsInterrupt(Exception ex)
        => ex is SqliteException { SqliteErrorCode: raw.SQLITE_INTERRUPT } || ex is OperationCanceledException;
}

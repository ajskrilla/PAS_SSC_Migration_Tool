using System.Collections.Concurrent;

namespace PasMigration.Connectors;

/// <summary>
/// Tracks in-flight migration jobs and their cancellation sources so a running job can be
/// aborted from the UI. Singleton; entries are removed when the job completes.
/// </summary>
public sealed class JobRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobs = new();

    /// <summary>Register a job and get a CancellationToken linked to the caller's token.</summary>
    public (Guid JobId, CancellationToken Token) Register(Guid jobId, CancellationToken linked)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linked);
        _jobs[jobId] = cts;
        return (jobId, cts.Token);
    }

    /// <summary>Request cancellation of a running job. Returns false if not found.</summary>
    public bool Cancel(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    /// <summary>Remove a job once finished (and dispose its token source).</summary>
    public void Remove(Guid jobId)
    {
        if (_jobs.TryRemove(jobId, out var cts)) cts.Dispose();
    }

    public bool IsRunning(Guid jobId) => _jobs.ContainsKey(jobId);
}

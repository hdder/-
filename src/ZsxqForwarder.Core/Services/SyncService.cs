using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

public class SyncService
{
    private readonly TopicService _topicService;
    private readonly DatabaseService _db;

    public event EventHandler<SyncProgressEventArgs>? ProgressChanged;
    public event EventHandler? SyncCompleted;
    public event EventHandler<string>? SyncError;

    public bool IsSyncing { get; private set; }

    public SyncService(TopicService topicService, DatabaseService db)
    {
        _topicService = topicService;
        _db = db;
    }

    public async Task FullSyncAsync(CancellationToken ct = default)
    {
        IsSyncing = true;
        try
        {
            var dynamics = await _topicService.FetchAllDynamicsAsync(
                progress: new Progress<(int Loaded, bool IsComplete)>(p =>
                {
                    ProgressChanged?.Invoke(this, new SyncProgressEventArgs
                    {
                        Phase = "fetching",
                        Loaded = p.Loaded,
                        IsComplete = p.IsComplete
                    });
                }),
                cancellationToken: ct);

            _db.SaveDynamicsBatch(dynamics);
            _db.SetSyncState("initial_sync_done", "true");

            ProgressChanged?.Invoke(this, new SyncProgressEventArgs
            {
                Phase = "stored",
                Loaded = dynamics.Count,
                IsComplete = true
            });

            SyncCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            SyncError?.Invoke(this, ex.Message);
            throw;
        }
        finally
        {
            IsSyncing = false;
        }
    }

    public async Task IncrementalSyncAsync(CancellationToken ct = default)
    {
        IsSyncing = true;
        try
        {
            var (dynamics, _) = await _topicService.FetchDynamicsPageAsync(count: 30);
            var lastSyncedId = _db.GetLastSyncedDynamicId();
            var newDynamics = dynamics.Where(d => d.DynamicId > lastSyncedId).ToList();

            if (newDynamics.Count > 0)
            {
                _db.SaveDynamicsBatch(newDynamics);
            }

            SyncCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            SyncError?.Invoke(this, ex.Message);
        }
        finally
        {
            IsSyncing = false;
        }
    }
}

public class SyncProgressEventArgs : EventArgs
{
    public string Phase { get; set; } = ""; // "fetching", "stored"
    public int Loaded { get; set; }
    public bool IsComplete { get; set; }
}

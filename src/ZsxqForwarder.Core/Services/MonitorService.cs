using Serilog;
using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

public class MonitorService : IDisposable
{
    private readonly TopicService _topicService;
    private readonly ForwardService _forwardService;
    private readonly DatabaseService _db;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    // Track last seen CreateTime per group
    private readonly Dictionary<long, long> _lastSeenCreateTime = [];

    public event EventHandler<NewTopicEventArgs>? NewTopicDetected;
    public event EventHandler<MonitorErrorEventArgs>? ErrorOccurred;
    public event EventHandler? MonitorStarted;
    public event EventHandler? MonitorStopped;

    public int IntervalSeconds { get; set; } = 30;
    public bool IsRunning => _isRunning;
    public List<long> MonitoredGroups { get; set; } = [];

    public MonitorService(TopicService topicService, ForwardService forwardService, DatabaseService? db = null)
    {
        _topicService = topicService;
        _forwardService = forwardService;
        _db = db!;
    }

    public async Task StartAsync(List<long> groupIds)
    {
        if (_isRunning) return;

        MonitoredGroups = groupIds;
        _cts = new CancellationTokenSource();
        _isRunning = true;
        _lastSeenCreateTime.Clear();

        // Initialize last seen time from DB for each group
        if (_db != null)
        {
            foreach (var gid in groupIds)
            {
                var latestTopic = _db.GetTopicsByGroup(gid, 1)
                    .FirstOrDefault();
                if (latestTopic != null && latestTopic.CreateTime > 0)
                    _lastSeenCreateTime[gid] = latestTopic.CreateTime;
            }
        }

        MonitorStarted?.Invoke(this, EventArgs.Empty);
        _ = RunMonitorLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _isRunning = false;
        MonitorStopped?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunMonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(IntervalSeconds * 1000, ct);

                // Fetch dynamics (this goes through DOM scraping)
                var (dynamics, _) = await _topicService.FetchDynamicsPageAsync(count: 20);
                if (dynamics.Count == 0) continue;

                // Group dynamics by groupId, find new ones by CreateTime
                foreach (var d in dynamics)
                {
                    if (ct.IsCancellationRequested) break;
                    if (d.Topic == null) continue;

                    var groupId = d.Group?.GroupId ?? d.Topic.Group?.GroupId ?? 0;
                    if (groupId == 0) continue;
                    if (!MonitoredGroups.Contains(groupId)) continue;

                    var topicCreateTime = d.Topic.CreateTime;

                    // Initialize baseline on first check
                    if (!_lastSeenCreateTime.TryGetValue(groupId, out var lastTime))
                    {
                        _lastSeenCreateTime[groupId] = topicCreateTime;
                        continue;
                    }

                    // New message detected
                    if (topicCreateTime > lastTime)
                    {
                        Log.Information("New topic detected in group {GroupId}: create_time={CreateTime}", groupId, topicCreateTime);

                        _lastSeenCreateTime[groupId] = topicCreateTime;

                        // Save to DB
                        if (_db != null)
                        {
                            try { _db.SaveDynamicsBatch(new List<Dynamic> { d }); }
                            catch (Exception ex) { Log.Error(ex, "Failed to save monitored topic"); }
                        }

                        NewTopicDetected?.Invoke(this, new NewTopicEventArgs
                        {
                            Topic = d.Topic,
                            GroupId = groupId
                        });

                        await _forwardService.ForwardAsync(d.Topic, groupId, d.Group?.Name ?? "");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, new MonitorErrorEventArgs { GroupId = 0, Error = ex.Message });
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}

public class NewTopicEventArgs : EventArgs
{
    public Topic Topic { get; set; } = null!;
    public long GroupId { get; set; }
}

public class MonitorErrorEventArgs : EventArgs
{
    public long GroupId { get; set; }
    public string Error { get; set; } = "";
}

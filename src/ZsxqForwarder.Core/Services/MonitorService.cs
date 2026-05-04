using Serilog;
using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

public class MonitorService : IDisposable
{
    private readonly ForwardService _forwardService;
    private readonly DatabaseService _db;
    private readonly Func<long, string, Task<List<Dynamic>>> _scrapeGroupPage;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    // Track last seen CreateTime per group
    private readonly Dictionary<long, long> _lastSeenCreateTime = [];

    public event EventHandler<NewTopicEventArgs>? NewTopicDetected;
    public event EventHandler<MonitorErrorEventArgs>? ErrorOccurred;
    public event EventHandler? MonitorStarted;
    public event EventHandler? MonitorStopped;

    public int IntervalSeconds { get; set; } = 5;
    public bool IsRunning => _isRunning;
    public List<long> MonitoredGroups { get; set; } = [];

    /// <param name="scrapeGroupPage">Function that navigates to a group page and extracts dynamics. Args: (groupId, groupName)</param>
    public MonitorService(
        ForwardService forwardService,
        DatabaseService db,
        Func<long, string, Task<List<Dynamic>>> scrapeGroupPage)
    {
        _forwardService = forwardService;
        _db = db;
        _scrapeGroupPage = scrapeGroupPage;
    }

    public async Task StartAsync(List<long> groupIds)
    {
        if (_isRunning) return;

        MonitoredGroups = groupIds;
        _cts = new CancellationTokenSource();
        _isRunning = true;
        _lastSeenCreateTime.Clear();

        // Initialize last seen time from DB for each group
        foreach (var gid in groupIds)
        {
            var latestTopic = _db.GetTopicsByGroup(gid, 1).FirstOrDefault();
            if (latestTopic != null && latestTopic.CreateTime > 0)
                _lastSeenCreateTime[gid] = latestTopic.CreateTime;
        }

        Log.Information("Monitor started: {Count} groups, interval {Interval}s", groupIds.Count, IntervalSeconds);
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

                foreach (var groupId in MonitoredGroups)
                {
                    if (ct.IsCancellationRequested) break;

                    // Get group name from DB
                    var groups = _db.GetGroups();
                    var group = groups.FirstOrDefault(g => g.GroupId == groupId);
                    var groupName = group?.Name ?? "";

                    // Scrape the group page directly
                    var dynamics = await _scrapeGroupPage(groupId, groupName);

                    foreach (var d in dynamics)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (d.Topic == null) continue;

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
                            Log.Information("New topic in group {GroupId}: time={Time}", groupId, topicCreateTime);
                            _lastSeenCreateTime[groupId] = topicCreateTime;

                            // Save to DB
                            try { _db.SaveDynamicsBatch(new List<Dynamic> { d }); }
                            catch (Exception ex) { Log.Error(ex, "Failed to save monitored topic"); }

                            NewTopicDetected?.Invoke(this, new NewTopicEventArgs
                            {
                                Topic = d.Topic,
                                GroupId = groupId
                            });

                            await _forwardService.ForwardAsync(d.Topic, groupId, groupName);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Monitor cycle error");
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

using Serilog;
using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

public class MonitorService : IDisposable
{
    private readonly ForwardService _forwardService;
    private readonly DatabaseService _db;
    private readonly Func<long, string, Task<List<Dynamic>>> _scrapeGroupPage;
    private readonly ZsxqApiService? _apiService;
    private ImageHostingService? _imageHosting;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private int _roundRobinIndex;

    // Track last seen CreateTime per group
    private readonly Dictionary<long, long> _lastSeenCreateTime = [];

    public event EventHandler<NewTopicEventArgs>? NewTopicDetected;
    public event EventHandler<MonitorErrorEventArgs>? ErrorOccurred;
    public event EventHandler? MonitorStarted;
    public event EventHandler? MonitorStopped;

    public int IntervalSeconds { get; set; } = 5;
    public bool IsRunning => _isRunning;
    public List<long> MonitoredGroups { get; set; } = [];

    public MonitorService(
        ForwardService forwardService,
        DatabaseService db,
        Func<long, string, Task<List<Dynamic>>> scrapeGroupPage,
        ZsxqApiService? apiService = null)
    {
        _forwardService = forwardService;
        _db = db;
        _scrapeGroupPage = scrapeGroupPage;
        _apiService = apiService;
    }

    public void SetImageHosting(ImageHostingService? service)
    {
        _imageHosting = service;
    }

    public async Task StartAsync(List<long> groupIds)
    {
        if (_isRunning) return;

        MonitoredGroups = groupIds;
        _cts = new CancellationTokenSource();
        _isRunning = true;
        _roundRobinIndex = 0;
        _lastSeenCreateTime.Clear();

        foreach (var gid in groupIds)
        {
            var latestTopic = _db.GetTopicsByGroup(gid, 1).FirstOrDefault();
            if (latestTopic != null && latestTopic.CreateTime > 0)
                _lastSeenCreateTime[gid] = latestTopic.CreateTime;
        }

        Log.Information("Monitor started: {Count} groups, interval {Interval}s, api={HasApi}",
            groupIds.Count, IntervalSeconds, _apiService?.HasCookies ?? false);
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

                if (MonitoredGroups.Count == 0) continue;

                // Round-robin: check one group per cycle for speed
                var groupId = MonitoredGroups[_roundRobinIndex % MonitoredGroups.Count];
                _roundRobinIndex++;

                var groups = _db.GetGroups();
                var groupName = groups.FirstOrDefault(g => g.GroupId == groupId)?.Name ?? "";

                List<Dynamic> dynamics;

                // Try API first (respects circuit breaker)
                if (_apiService != null && _apiService.HasCookies && !_apiService.IsCircuitOpen)
                {
                    dynamics = await FetchGroupViaApiAsync(groupId, groupName);
                }
                else if (_apiService?.IsCircuitOpen == true)
                {
                    Log.Debug("Circuit breaker open for group {GroupId}, using DOM fallback", groupId);
                    dynamics = await _scrapeGroupPage(groupId, groupName);
                }
                else
                {
                    dynamics = await _scrapeGroupPage(groupId, groupName);
                }

                foreach (var d in dynamics)
                {
                    if (ct.IsCancellationRequested) break;
                    if (d.Topic == null) continue;

                    var topicCreateTime = d.Topic.CreateTime;

                    if (!_lastSeenCreateTime.TryGetValue(groupId, out var lastTime))
                    {
                        _lastSeenCreateTime[groupId] = topicCreateTime;
                        continue;
                    }

                    if (topicCreateTime > lastTime)
                    {
                        Log.Information("New topic in group {GroupId}: time={Time}", groupId, topicCreateTime);
                        _lastSeenCreateTime[groupId] = topicCreateTime;

                        // Replace image URLs with CDN URLs before saving
                        if (_imageHosting != null)
                        {
                            try { d.Topic = await _imageHosting.ReplaceImageUrlsAsync(d.Topic); }
                            catch (Exception ex) { Log.Warning(ex, "Image URL replacement failed for topic {TopicId}", d.Topic.TopicId); }
                        }

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
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error(ex, "Monitor cycle error");
                ErrorOccurred?.Invoke(this, new MonitorErrorEventArgs { GroupId = 0, Error = ex.Message });
            }
        }
    }

    private async Task<List<Dynamic>> FetchGroupViaApiAsync(long groupId, string groupName)
    {
        var result = await _apiService!.GetTopicsAsync(groupId, count: 10);
        if (result?.RespData == null) return [];

        var dynamics = new List<Dynamic>();
        foreach (var topic in result.RespData.Topics)
        {
            dynamics.Add(new Dynamic
            {
                DynamicId = topic.TopicId,
                Action = "create_topic",
                CreateTimeStr = topic.CreateTime > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(topic.CreateTime).ToString("yyyy-MM-dd HH:mm")
                    : "",
                Topic = topic,
                Group = new DynamicGroup { GroupId = groupId, Name = groupName }
            });
        }
        return dynamics;
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

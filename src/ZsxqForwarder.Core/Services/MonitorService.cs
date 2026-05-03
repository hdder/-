using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

public class MonitorService : IDisposable
{
    private readonly TopicService _topicService;
    private readonly ForwardService _forwardService;
    private readonly Dictionary<long, long> _lastKnownTopicIds = new();
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public event EventHandler<NewTopicEventArgs>? NewTopicDetected;
    public event EventHandler<MonitorErrorEventArgs>? ErrorOccurred;
    public event EventHandler? MonitorStarted;
    public event EventHandler? MonitorStopped;

    public int IntervalSeconds { get; set; } = 30;
    public bool IsRunning => _isRunning;
    public List<long> MonitoredGroups { get; set; } = [];

    public MonitorService(TopicService topicService, ForwardService forwardService)
    {
        _topicService = topicService;
        _forwardService = forwardService;
    }

    public async Task StartAsync(List<long> groupIds)
    {
        if (_isRunning) return;

        MonitoredGroups = groupIds;
        _cts = new CancellationTokenSource();
        _isRunning = true;
        MonitorStarted?.Invoke(this, EventArgs.Empty);

        // Initialize last known topic IDs
        foreach (var groupId in groupIds)
        {
            try
            {
                var topics = await _topicService.GetLatestTopicsAsync(groupId, 1);
                if (topics.Count > 0)
                {
                    _lastKnownTopicIds[groupId] = topics[0].TopicId;
                }
            }
            catch { /* Continue even if initial fetch fails */ }
        }

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

                    try
                    {
                        var topics = await _topicService.GetLatestTopicsAsync(groupId, 5);
                        if (topics.Count == 0) continue;

                        var latestId = topics.Max(t => t.TopicId);
                        var lastKnownId = _lastKnownTopicIds.GetValueOrDefault(groupId, 0);

                        if (latestId > lastKnownId)
                        {
                            var newTopics = topics.Where(t => t.TopicId > lastKnownId).ToList();
                            _lastKnownTopicIds[groupId] = latestId;

                            foreach (var topic in newTopics)
                            {
                                NewTopicDetected?.Invoke(this, new NewTopicEventArgs { Topic = topic, GroupId = groupId });

                                // Auto-forward
                                await _forwardService.ForwardAsync(topic);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorOccurred?.Invoke(this, new MonitorErrorEventArgs { GroupId = groupId, Error = ex.Message });
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

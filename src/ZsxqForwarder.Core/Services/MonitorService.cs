using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

public class MonitorService : IDisposable
{
    private readonly TopicService _topicService;
    private readonly ForwardService _forwardService;
    private long _lastKnownDynamicId;
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

                var (dynamics, _) = await _topicService.FetchDynamicsPageAsync(count: 10);
                if (dynamics.Count == 0) continue;

                var maxDynamicId = dynamics.Max(d => d.DynamicId);
                if (_lastKnownDynamicId == 0)
                {
                    _lastKnownDynamicId = maxDynamicId;
                    continue;
                }

                var newDynamics = dynamics
                    .Where(d => d.DynamicId > _lastKnownDynamicId && d.Topic != null)
                    .ToList();

                _lastKnownDynamicId = maxDynamicId;

                foreach (var d in newDynamics)
                {
                    if (ct.IsCancellationRequested) break;

                    var groupId = d.Group?.GroupId ?? 0;
                    NewTopicDetected?.Invoke(this, new NewTopicEventArgs
                    {
                        Topic = d.Topic!,
                        GroupId = groupId
                    });

                    await _forwardService.ForwardAsync(d.Topic!, groupId);
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

using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

public interface IForwarder
{
    string Name { get; }
    bool IsEnabled { get; set; }
    Task ForwardAsync(Topic topic);
}

public class ForwardService
{
    private readonly List<IForwarder> _forwarders = [];

    public IReadOnlyList<IForwarder> Forwarders => _forwarders.AsReadOnly();

    public void RegisterForwarder(IForwarder forwarder)
    {
        _forwarders.Add(forwarder);
    }

    public void RemoveForwarder(string name)
    {
        _forwarders.RemoveAll(f => f.Name == name);
    }

    public async Task ForwardAsync(Topic topic)
    {
        var enabledForwarders = _forwarders.Where(f => f.IsEnabled).ToList();
        var tasks = enabledForwarders.Select(f => ForwardWithRetryAsync(f, topic));
        await Task.WhenAll(tasks);
    }

    private static async Task ForwardWithRetryAsync(IForwarder forwarder, Topic topic)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                await forwarder.ForwardAsync(topic);
                return;
            }
            catch when (i < 2)
            {
                await Task.Delay(1000 * (i + 1));
            }
        }
    }
}

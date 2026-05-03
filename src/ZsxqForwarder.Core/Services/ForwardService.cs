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
    private List<ForwardRule> _rules = [];
    private Func<ForwardRule, IForwarder?>? _forwarderFactory;

    public IReadOnlyList<IForwarder> Forwarders => _forwarders.AsReadOnly();

    public void RegisterForwarder(IForwarder forwarder)
    {
        _forwarders.Add(forwarder);
    }

    public void RemoveForwarder(string name)
    {
        _forwarders.RemoveAll(f => f.Name == name);
    }

    public void SetRules(List<ForwardRule> rules)
    {
        _rules = rules;
    }

    public void SetForwarderFactory(Func<ForwardRule, IForwarder?> factory)
    {
        _forwarderFactory = factory;
    }

    public async Task ForwardAsync(Topic topic, long groupId)
    {
        var matchingRules = _rules.Where(r =>
            r.GroupId == groupId && r.Enabled && !string.IsNullOrEmpty(r.WebhookUrl)).ToList();

        if (matchingRules.Count > 0 && _forwarderFactory != null)
        {
            var tasks = new List<Task>();
            foreach (var rule in matchingRules)
            {
                var forwarder = _forwarderFactory(rule);
                if (forwarder != null)
                    tasks.Add(ForwardWithRetryAsync(forwarder, topic));
            }
            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
                return;
            }
        }

        // Fallback: all enabled forwarders
        var enabled = _forwarders.Where(f => f.IsEnabled).ToList();
        await Task.WhenAll(enabled.Select(f => ForwardWithRetryAsync(f, topic)));
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

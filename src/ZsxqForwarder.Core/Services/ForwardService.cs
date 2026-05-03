using Serilog;
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
    private DatabaseService? _db;
    private ImageHostingService? _imageHosting;

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

    public void SetDatabase(DatabaseService db)
    {
        _db = db;
    }

    public void SetImageHosting(ImageHostingService? service)
    {
        _imageHosting = service;
    }

    public async Task ForwardAsync(Topic topic, long groupId, string groupName = "")
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
                    tasks.Add(ForwardAndLogAsync(forwarder, topic, groupId, rule));
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

    private async Task ForwardAndLogAsync(IForwarder forwarder, Topic topic, long groupId, ForwardRule rule)
    {
        // Replace expired image URLs with local URLs before forwarding
        if (_imageHosting != null)
            topic = await _imageHosting.ReplaceImageUrlsAsync(topic);

        var author = topic.Talk?.Owner?.Name ?? topic.Task?.Owner?.Name ?? "Unknown";
        var content = topic.Talk?.Text ?? topic.Task?.Text ?? topic.Question?.Text ?? "";
        var preview = content.Length > 100 ? content[..100] + "..." : content;

        string status = "Success";
        string? errorMsg = null;

        try
        {
            await ForwardWithRetryAsync(forwarder, topic);
        }
        catch (Exception ex)
        {
            status = "Failed";
            errorMsg = ex.Message;
            Log.Error(ex, "Forward failed for topic {TopicId} to {Type}", topic.TopicId, rule.ForwarderType);
        }

        // Save topic and log
        try
        {
            _db?.SaveTopic(topic, groupId);
            _db?.AddForwardLog(new ForwardLogEntry
            {
                TopicId = topic.TopicId,
                GroupId = groupId,
                GroupName = rule.GroupName,
                Author = author,
                ContentPreview = preview,
                ForwarderType = rule.ForwarderType,
                WebhookUrl = rule.WebhookUrl,
                Status = status,
                ErrorMessage = errorMsg,
                ForwardedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save forward log");
        }
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

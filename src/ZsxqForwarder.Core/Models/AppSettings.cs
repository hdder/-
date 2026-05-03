namespace ZsxqForwarder.Core.Models;

public class AppSettings
{
    public List<GroupConfig> Groups { get; set; } = [];
    public List<ForwardRule> ForwardRules { get; set; } = [];
    public MonitorConfig Monitor { get; set; } = new();
}

public class GroupConfig
{
    public long GroupId { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public class ForwardRule
{
    public long GroupId { get; set; }
    public string GroupName { get; set; } = "";
    public string ForwarderType { get; set; } = ""; // "DingTalk", "Feishu"
    public bool Enabled { get; set; } = true;
    public string WebhookUrl { get; set; } = "";
    public string Secret { get; set; } = "";        // DingTalk only
}

public class MonitorConfig
{
    public int IntervalSeconds { get; set; } = 30;
}

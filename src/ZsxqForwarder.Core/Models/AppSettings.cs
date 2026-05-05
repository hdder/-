namespace ZsxqForwarder.Core.Models;

public class AppSettings
{
    public List<GroupConfig> Groups { get; set; } = [];
    public List<ForwardRule> ForwardRules { get; set; } = [];
    public MonitorConfig Monitor { get; set; } = new();
    public ApiConfig Api { get; set; } = new();
    public RemoteLogConfig RemoteLog { get; set; } = new();
    public FeishuConfig Feishu { get; set; } = new();
}

public class GroupConfig
{
    public long GroupId { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public string BackgroundUrl { get; set; } = "";
}

public class ForwardRule
{
    public int Id { get; set; }
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

public class ApiConfig
{
    public string Secret { get; set; } = "zsxqapi2020";
    public string BaseUrl { get; set; } = "https://api.zsxq.com";
    public string AppVersion { get; set; } = "3.11.0";
    public string Platform { get; set; } = "ios";
    public int MinIntervalMs { get; set; } = 1000;
}

public class RemoteLogConfig
{
    public bool Enabled { get; set; } = true;
    public string ServerUrl { get; set; } = "http://38.76.164.188:5006";
    public string ApiToken { get; set; } = "zsxq-log-2024";
}

public class FeishuConfig
{
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
}

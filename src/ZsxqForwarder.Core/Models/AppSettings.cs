namespace ZsxqForwarder.Core.Models;

public class AppSettings
{
    public List<GroupConfig> Groups { get; set; } = [];
    public DingTalkConfig DingTalk { get; set; } = new();
    public FeishuConfig Feishu { get; set; } = new();
    public TelegramConfig Telegram { get; set; } = new();
    public WechatConfig Wechat { get; set; } = new();
    public MonitorConfig Monitor { get; set; } = new();
}

public class GroupConfig
{
    public long GroupId { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public class DingTalkConfig
{
    public bool Enabled { get; set; }
    public string WebhookUrl { get; set; } = "";
    public string Secret { get; set; } = "";
}

public class FeishuConfig
{
    public bool Enabled { get; set; }
    public string WebhookUrl { get; set; } = "";
}

public class TelegramConfig
{
    public bool Enabled { get; set; }
    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";
}

public class WechatConfig
{
    public bool Enabled { get; set; }
    public string WebhookUrl { get; set; } = "";
}

public class MonitorConfig
{
    public int IntervalSeconds { get; set; } = 30;
}

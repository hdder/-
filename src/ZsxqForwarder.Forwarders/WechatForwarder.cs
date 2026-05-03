using Newtonsoft.Json;
using ZsxqForwarder.Core.Models;
using ZsxqForwarder.Core.Services;

namespace ZsxqForwarder.Forwarders;

public class WechatForwarder : IForwarder
{
    public string Name => "WeChat";
    public bool IsEnabled { get; set; }

    private string _webhookUrl = string.Empty;
    private readonly HttpClient _httpClient = new();

    public void Configure(string webhookUrl)
    {
        _webhookUrl = webhookUrl;
    }

    public async Task ForwardAsync(Topic topic)
    {
        if (string.IsNullOrEmpty(_webhookUrl))
            return;

        var text = FormatMessage(topic);

        var payload = new
        {
            msgtype = "text",
            text = new { content = text }
        };

        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(_webhookUrl, content);
        response.EnsureSuccessStatusCode();
    }

    private static string FormatMessage(Topic topic)
    {
        var author = topic.Talk?.Owner?.Name
                     ?? topic.Task?.Owner?.Name
                     ?? "Unknown";

        var text = topic.Talk?.Text ?? topic.Task?.Text ?? topic.Question?.Text ?? "";

        var msg = $"[{topic.Type}] {author}\n" +
                  $"{topic.CreatedAt:yyyy-MM-dd HH:mm}\n\n" +
                  $"{text}";

        if (topic.Talk?.Images?.Count > 0)
            msg += $"\n\n[Contains {topic.Talk.Images.Count} image(s)]";

        return msg;
    }
}

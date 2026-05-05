using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Serilog;
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
        if (string.IsNullOrEmpty(_webhookUrl)) return;

        var text = FormatMessage(topic);

        // WeChat markdown_v2 requires escaping special characters
        text = EscapeMarkdownV2(text);

        // Replace multiple spaces with newline
        text = Regex.Replace(text, @" {2,}", "\n");

        var payload = new
        {
            msgtype = "markdown",
            markdown = new { content = text }
        };

        await PostAsync(payload);

        // Send images separately as markdown with image tag
        if (topic.Talk?.Images?.Count > 0)
        {
            foreach (var img in topic.Talk.Images)
            {
                var imgUrl = img.Original?.Url ?? img.Large?.Url ?? img.Url;
                if (!string.IsNullOrEmpty(imgUrl))
                {
                    await PostAsync(new
                    {
                        msgtype = "markdown",
                        markdown = new { content = $"![图片]({imgUrl})" }
                    });
                }
            }
        }
    }

    private static string FormatMessage(Topic topic)
    {
        var author = topic.Talk?.Owner?.Name
                     ?? topic.Task?.Owner?.Name
                     ?? topic.Question?.Owner?.Name
                     ?? "Unknown";
        var text = topic.Talk?.Text ?? topic.Task?.Text ?? topic.Question?.Text ?? "";

        var sb = new StringBuilder();
        sb.AppendLine($"**[{topic.Type}] {author}**");
        sb.AppendLine($"{topic.CreatedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine(text);

        if (topic.LikesCount > 0 || topic.CommentsCount > 0)
        {
            sb.AppendLine();
            sb.Append($"点赞: {topic.LikesCount} | 评论: {topic.CommentsCount}");
        }

        return sb.ToString();
    }

    private static string EscapeMarkdownV2(string text)
    {
        // WeChat markdown requires escaping these characters
        var chars = new[] { '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!', '\\' };
        foreach (var c in chars)
            text = text.Replace(c.ToString(), $"\\{c}");
        return text;
    }

    private async Task PostAsync(object payload)
    {
        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(_webhookUrl, content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("WeChat send failed: {Status} {Body}", response.StatusCode, body);
        }
        response.EnsureSuccessStatusCode();
    }
}

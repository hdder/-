using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Serilog;
using ZsxqForwarder.Core.Models;
using ZsxqForwarder.Core.Services;

namespace ZsxqForwarder.Forwarders;

/// <summary>
/// Forwarder for 团园 (Tuan) platform.
/// WebhookUrl field stores the access_token.
/// Posts markdown to http://trsystem.guhai888.cn/admin/send?access_token={token}
/// </summary>
public class TuanForwarder : IForwarder
{
    public string Name => "Tuan";
    public bool IsEnabled { get; set; }

    private string _accessToken = string.Empty;
    private readonly HttpClient _httpClient = new();

    private const string BaseApiUrl = "http://trsystem.guhai888.cn/admin/send";

    public void Configure(string webhookUrl)
    {
        // WebhookUrl stores the access_token
        _accessToken = webhookUrl.Trim();
    }

    public async Task ForwardAsync(Topic topic)
    {
        if (string.IsNullOrEmpty(_accessToken)) return;

        var (title, text) = FormatContent(topic);

        // Clean markdown image links
        text = Regex.Replace(text, @" {2,}", "  \n");

        var url = $"{BaseApiUrl}?access_token={_accessToken}";
        var payload = new
        {
            msgtype = "markdown",
            markdown = new { title, text }
        };

        await PostAsync(url, payload);

        // Send images separately
        if (topic.Talk?.Images?.Count > 0)
        {
            foreach (var img in topic.Talk.Images)
            {
                var imgUrl = img.Original?.Url ?? img.Large?.Url ?? img.Url;
                if (!string.IsNullOrEmpty(imgUrl))
                {
                    await PostAsync(url, new
                    {
                        msgtype = "markdown",
                        markdown = new { title = "图片", text = $"![图片]({imgUrl})" }
                    });
                }
            }
        }
    }

    private static (string title, string text) FormatContent(Topic topic)
    {
        var author = topic.Talk?.Owner?.Name
                     ?? topic.Task?.Owner?.Name
                     ?? topic.Question?.Owner?.Name
                     ?? "Unknown";
        var title = $"[{topic.Type}] {author}";
        var text = topic.Talk?.Text ?? topic.Task?.Text ?? topic.Question?.Text ?? "";

        var sb = new StringBuilder();
        sb.AppendLine($"### {title}");
        sb.AppendLine($"> {topic.CreatedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine(text);

        if (topic.LikesCount > 0 || topic.CommentsCount > 0)
        {
            sb.AppendLine();
            sb.Append($"点赞: {topic.LikesCount} | 评论: {topic.CommentsCount}");
        }

        return (title, sb.ToString());
    }

    private async Task PostAsync(string url, object payload)
    {
        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("Tuan send failed: {Status} {Body}", response.StatusCode, body);
            response.EnsureSuccessStatusCode();
            return;
        }

        // Tuan returns {"code": 200} on success
        var result = JsonConvert.DeserializeObject<dynamic>(body);
        if (result?.code != 200)
        {
            Log.Warning("Tuan API returned non-200: {Body}", body);
        }
    }
}

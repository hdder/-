using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Serilog;
using ZsxqForwarder.Core.Models;
using ZsxqForwarder.Core.Services;

namespace ZsxqForwarder.Forwarders;

public class DingTalkForwarder : IForwarder
{
    public string Name => "DingTalk";
    public bool IsEnabled { get; set; }

    private string _webhookUrl = string.Empty;
    private string _secret = string.Empty;
    private readonly HttpClient _httpClient = new();

    public void Configure(string webhookUrl, string secret = "")
    {
        _webhookUrl = webhookUrl;
        _secret = secret;
    }

    public async Task ForwardAsync(Topic topic)
    {
        if (string.IsNullOrEmpty(_webhookUrl)) return;

        var url = BuildUrl();

        // Build full text with image markdown
        var (title, fullText) = FormatContent(topic);

        // Extract markdown image links: ![xxx](url)
        var imagePattern = @"!\[.*?\]\((.*?)\)";
        var imageMatches = Regex.Matches(fullText, imagePattern);

        if (imageMatches.Count > 0)
        {
            // Send each image as separate message first
            foreach (Match match in imageMatches)
            {
                var imgUrl = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(imgUrl))
                {
                    await PostAsync(url, new
                    {
                        msgtype = "markdown",
                        markdown = new { title = "图片", text = $"![图片]({imgUrl})" }
                    });
                }
            }

            // Send remaining text (images removed)
            var textOnly = Regex.Replace(fullText, imagePattern, "").Trim();
            if (!string.IsNullOrWhiteSpace(textOnly))
            {
                await PostAsync(url, new
                {
                    msgtype = "markdown",
                    markdown = new { title, text = textOnly }
                });
            }
        }
        else
        {
            // No images in text, also check topic.Talk.Images
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

            // Send main content
            if (!string.IsNullOrWhiteSpace(fullText))
            {
                await PostAsync(url, new
                {
                    msgtype = "markdown",
                    markdown = new { title, text = fullText }
                });
            }
        }

        // Send files as links
        if (topic.Talk?.Files?.Count > 0)
        {
            var filesText = "**附件:**\n\n" +
                string.Join("\n", topic.Talk.Files.Select(f => $"- [{f.Name}]({f.Url})"));
            await PostAsync(url, new
            {
                msgtype = "markdown",
                markdown = new { title = "附件", text = filesText }
            });
        }
    }

    private string BuildUrl()
    {
        if (string.IsNullOrEmpty(_secret)) return _webhookUrl;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stringToSign = $"{timestamp}\n{_secret}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
        var sign = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        return $"{_webhookUrl}&timestamp={timestamp}&sign={Uri.EscapeDataString(sign)}";
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
            Log.Warning("DingTalk send failed: {Status} {Body}", response.StatusCode, body);
        }
        response.EnsureSuccessStatusCode();
    }
}

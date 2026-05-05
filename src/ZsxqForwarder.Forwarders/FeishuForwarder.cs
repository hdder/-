using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Serilog;
using ZsxqForwarder.Core.Models;
using ZsxqForwarder.Core.Services;

namespace ZsxqForwarder.Forwarders;

public class FeishuForwarder : IForwarder
{
    public string Name => "Feishu";
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

        var (title, fullText) = FormatContent(topic);

        // Extract markdown image links
        var imagePattern = @"!\[.*?\]\((.*?)\)";
        var imageMatches = Regex.Matches(fullText, imagePattern);

        // Send images first (as markdown cards)
        foreach (Match match in imageMatches)
        {
            var imgUrl = match.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(imgUrl))
            {
                await PostAsync(BuildPayload(new List<string>
                {
                    $"![图片]({imgUrl})"
                }));
            }
        }

        // Also check topic.Talk.Images if not already in text
        if (imageMatches.Count == 0 && topic.Talk?.Images?.Count > 0)
        {
            foreach (var img in topic.Talk.Images)
            {
                var imgUrl = img.Original?.Url ?? img.Large?.Url ?? img.Url;
                if (!string.IsNullOrEmpty(imgUrl))
                {
                    await PostAsync(BuildPayload(new List<string>
                    {
                        $"![图片]({imgUrl})"
                    }));
                }
            }
        }

        // Send remaining text
        var textOnly = Regex.Replace(fullText, imagePattern, "").Trim();
        if (!string.IsNullOrWhiteSpace(textOnly))
        {
            // Split into sections by newlines for better readability
            var lines = textOnly.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList();
            await PostAsync(BuildPayload(lines));
        }

        // Send files as links
        if (topic.Talk?.Files?.Count > 0)
        {
            var fileLines = topic.Talk.Files.Select(f => $"[{f.Name}]({f.Url})").ToList();
            await PostAsync(BuildPayload(fileLines, "附件"));
        }
    }

    private object BuildPayload(List<string> contentLines, string? headerTitle = null)
    {
        var elements = contentLines.Select(line => new Dictionary<string, object>
        {
            ["tag"] = "markdown",
            ["content"] = line,
            ["text_align"] = "left",
            ["text_size"] = "normal_v2",
            ["margin"] = "0px 0px 8px 0px"
        }).ToList<object>();

        var body = new
        {
            direction = "vertical",
            padding = "12px 12px 12px 12px",
            elements
        };

        object card = new
        {
            schema = "2.0",
            config = new
            {
                update_multi = true,
                style = new
                {
                    text_size = new
                    {
                        normal_v2 = new { @default = "normal", pc = "normal", mobile = "heading" }
                    }
                }
            },
            header = headerTitle != null ? new
            {
                title = new { tag = "plain_text", content = headerTitle },
                template = "blue"
            } : null,
            body
        };

        var msgData = new Dictionary<string, object>
        {
            ["msg_type"] = "interactive",
            ["card"] = card
        };

        // Add signature if secret is configured
        if (!string.IsNullOrEmpty(_secret))
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var stringToSign = $"{timestamp}\n{_secret}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
            var sign = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
            msgData["timestamp"] = timestamp;
            msgData["sign"] = sign;
        }

        return msgData;
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
        sb.AppendLine($"**{title}**");
        sb.AppendLine($"{topic.CreatedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine(text);

        if (topic.LikesCount > 0 || topic.CommentsCount > 0)
        {
            sb.AppendLine();
            sb.Append($"点赞: {topic.LikesCount} | 评论: {topic.CommentsCount}");
        }

        return (title, sb.ToString());
    }

    private async Task PostAsync(object payload)
    {
        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(_webhookUrl, content);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("Feishu send failed: {Status} {Body}", response.StatusCode, body);
        }
        response.EnsureSuccessStatusCode();
    }
}

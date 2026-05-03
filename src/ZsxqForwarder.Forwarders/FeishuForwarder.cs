using Newtonsoft.Json;
using ZsxqForwarder.Core.Models;
using ZsxqForwarder.Core.Services;

namespace ZsxqForwarder.Forwarders;

public class FeishuForwarder : IForwarder
{
    public string Name => "Feishu";
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

        // Send main content as rich post
        var (title, contentLines) = FormatPost(topic);
        await PostAsync(new
        {
            msg_type = "post",
            content = new
            {
                post = new
                {
                    zh_cn = new
                    {
                        title,
                        content = contentLines
                    }
                }
            }
        });

        // Send images as text links (webhook can't upload images without app_id)
        if (topic.Talk?.Images?.Count > 0)
        {
            var imgText = string.Join("\n", topic.Talk.Images.Select((img, i) =>
            {
                var url = img.Original?.Url ?? img.Large?.Url ?? img.Url;
                return $"图片{i + 1}: {url}";
            }));
            await PostAsync(new
            {
                msg_type = "text",
                content = new { text = imgText }
            });
        }

        // Send files as text links
        if (topic.Talk?.Files?.Count > 0)
        {
            var fileText = "附件:\n" + string.Join("\n",
                topic.Talk.Files.Select(f => $"{f.Name}: {f.Url}"));
            await PostAsync(new
            {
                msg_type = "text",
                content = new { text = fileText }
            });
        }
    }

    private static (string title, List<List<Dictionary<string, object>>> contentLines) FormatPost(Topic topic)
    {
        var author = topic.Talk?.Owner?.Name
                     ?? topic.Task?.Owner?.Name
                     ?? topic.Question?.Owner?.Name
                     ?? "Unknown";
        var title = $"[{topic.Type}] {author}";
        var text = topic.Talk?.Text ?? topic.Task?.Text ?? topic.Question?.Text ?? "";

        var contentLines = new List<List<Dictionary<string, object>>>();

        // Time line
        contentLines.Add(new List<Dictionary<string, object>>
        {
            new() { ["tag"] = "text", ["text"] = $"{topic.CreatedAt:yyyy-MM-dd HH:mm}" }
        });

        // Content
        contentLines.Add(new List<Dictionary<string, object>>
        {
            new() { ["tag"] = "text", ["text"] = text }
        });

        // Stats
        contentLines.Add(new List<Dictionary<string, object>>
        {
            new() { ["tag"] = "text", ["text"] = $"点赞: {topic.LikesCount} | 评论: {topic.CommentsCount}" }
        });

        return (title, contentLines);
    }

    private async Task PostAsync(object payload)
    {
        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(_webhookUrl, content);
        response.EnsureSuccessStatusCode();
    }
}

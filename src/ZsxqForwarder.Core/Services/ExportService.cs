using System.Text;
using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

public class ExportService
{
    private readonly TopicService _topicService;
    private readonly HttpClient _httpClient;

    public ExportService(TopicService topicService)
    {
        _topicService = topicService;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public async Task ExportToMarkdownAsync(
        int groupId,
        string groupName,
        string outputDir,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var topics = await _topicService.GetAllTopicsAsync(groupId, null, cancellationToken);

        // Group by month
        var monthlyGroups = topics
            .GroupBy(t => t.CreatedAt.ToString("yyyy-MM"))
            .OrderByDescending(g => g.Key);

        var totalTopics = topics.Count;
        var processed = 0;

        foreach (var monthGroup in monthlyGroups)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var monthDir = Path.Combine(outputDir, groupName, monthGroup.Key);
            Directory.CreateDirectory(monthDir);

            var imagesDir = Path.Combine(monthDir, "images");
            var hasImages = monthGroup.Any(t => t.Talk?.Images?.Count > 0);
            if (hasImages) Directory.CreateDirectory(imagesDir);

            var sb = new StringBuilder();
            sb.AppendLine($"# {groupName} - {monthGroup.Key}");
            sb.AppendLine();

            foreach (var topic in monthGroup.OrderByDescending(t => t.CreateTime))
            {
                if (cancellationToken.IsCancellationRequested) break;

                WriteTopicMarkdown(sb, topic, groupName, imagesDir);
                sb.AppendLine("---");
                sb.AppendLine();

                // Download images
                if (topic.Talk?.Images?.Count > 0)
                {
                    foreach (var img in topic.Talk.Images)
                    {
                        var imgUrl = img.Original?.Url ?? img.Large?.Url ?? img.Url;
                        if (!string.IsNullOrEmpty(imgUrl))
                        {
                            try
                            {
                                var fileName = $"topic_{topic.TopicId}_{Guid.NewGuid():N[..8]}.jpg";
                                var filePath = Path.Combine(imagesDir, fileName);
                                await DownloadFileAsync(imgUrl, filePath, cancellationToken);
                            }
                            catch { /* Skip failed downloads */ }
                        }
                    }
                }

                processed++;
                progress?.Report(new ExportProgress
                {
                    Processed = processed,
                    Total = totalTopics,
                    CurrentFile = $"Exporting {monthGroup.Key}"
                });
            }

            var filePath = Path.Combine(monthDir, $"{monthGroup.Key}.md");
            await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
        }

        progress?.Report(new ExportProgress
        {
            Processed = totalTopics,
            Total = totalTopics,
            CurrentFile = "Export completed"
        });
    }

    private void WriteTopicMarkdown(StringBuilder sb, Topic topic, string groupName, string imagesDir)
    {
        var author = topic.Talk?.Owner?.Name
                     ?? topic.Task?.Owner?.Name
                     ?? topic.Question?.Owner?.Name
                     ?? "Unknown";

        sb.AppendLine($"## [{topic.Type}] {author} - {topic.CreatedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        var text = topic.Talk?.Text ?? topic.Task?.Text ?? topic.Question?.Text ?? "";
        if (!string.IsNullOrEmpty(text))
        {
            sb.AppendLine(text);
            sb.AppendLine();
        }

        if (topic.Talk?.Images?.Count > 0)
        {
            foreach (var img in topic.Talk.Images)
            {
                var imgUrl = img.Original?.Url ?? img.Large?.Url ?? img.Url;
                if (!string.IsNullOrEmpty(imgUrl))
                {
                    var fileName = $"topic_{topic.TopicId}_{Guid.NewGuid():N[..8]}.jpg";
                    sb.AppendLine($"![image](images/{fileName})");
                    sb.AppendLine();
                }
            }
        }

        if (topic.Talk?.Files?.Count > 0)
        {
            sb.AppendLine("**Files:**");
            foreach (var file in topic.Talk.Files)
            {
                sb.AppendLine($"- [{file.Name}]({file.Url})");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"Likes: {topic.LikesCount} | Comments: {topic.CommentsCount}");

        if (topic.Digested) sb.Append(" | **Digest**");
        if (topic.Sticky) sb.Append(" | **Sticky**");

        sb.AppendLine();
    }

    private async Task DownloadFileAsync(string url, string filePath, CancellationToken ct)
    {
        var response = await _httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        await File.WriteAllBytesAsync(filePath, bytes, ct);
    }
}

public class ExportProgress
{
    public int Processed { get; set; }
    public int Total { get; set; }
    public string CurrentFile { get; set; } = "";
    public double Percent => Total > 0 ? (double)Processed / Total * 100 : 0;
}

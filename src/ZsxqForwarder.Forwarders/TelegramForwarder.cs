using Newtonsoft.Json;
using ZsxqForwarder.Core.Models;
using ZsxqForwarder.Core.Services;

namespace ZsxqForwarder.Forwarders;

public class TelegramForwarder : IForwarder
{
    public string Name => "Telegram";
    public bool IsEnabled { get; set; }

    private string _botToken = string.Empty;
    private string _chatId = string.Empty;
    private readonly HttpClient _httpClient = new();

    public void Configure(string botToken, string chatId)
    {
        _botToken = botToken;
        _chatId = chatId;
    }

    public async Task ForwardAsync(Topic topic)
    {
        if (string.IsNullOrEmpty(_botToken) || string.IsNullOrEmpty(_chatId))
            return;

        var text = FormatMessage(topic);
        var payload = new
        {
            chat_id = _chatId,
            text = text,
            parse_mode = "Markdown"
        };

        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            $"https://api.telegram.org/bot{_botToken}/sendMessage", content);

        response.EnsureSuccessStatusCode();

        // Send images separately
        if (topic.Talk?.Images?.Count > 0)
        {
            foreach (var img in topic.Talk.Images)
            {
                var imgUrl = img.Original?.Url ?? img.Large?.Url ?? img.Url;
                if (!string.IsNullOrEmpty(imgUrl))
                {
                    var photoPayload = new
                    {
                        chat_id = _chatId,
                        photo = imgUrl,
                        caption = ""
                    };
                    var photoJson = JsonConvert.SerializeObject(photoPayload);
                    var photoContent = new StringContent(photoJson, System.Text.Encoding.UTF8, "application/json");
                    await _httpClient.PostAsync(
                        $"https://api.telegram.org/bot{_botToken}/sendPhoto", photoContent);
                }
            }
        }
    }

    private static string FormatMessage(Topic topic)
    {
        var author = topic.Talk?.Owner?.Name
                     ?? topic.Task?.Owner?.Name
                     ?? "Unknown";

        var text = topic.Talk?.Text ?? topic.Task?.Text ?? topic.Question?.Text ?? "";

        var msg = $"*[{topic.Type}] {author}*\n" +
                  $"_{topic.CreatedAt:yyyy-MM-dd HH:mm}_\n\n" +
                  $"{EscapeMarkdown(text)}";

        if (topic.CommentsCount > 0)
            msg += $"\n\nComments: {topic.CommentsCount}";

        return msg;
    }

    private static string EscapeMarkdown(string text)
    {
        var chars = new[] { '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };
        foreach (var c in chars)
            text = text.Replace(c.ToString(), $"\\{c}");
        return text;
    }
}

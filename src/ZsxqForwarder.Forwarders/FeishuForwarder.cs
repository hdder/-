using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
    private string _appId = string.Empty;
    private string _appSecret = string.Empty;
    private DatabaseService? _db;

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Token cache
    private string? _cachedToken;
    private DateTime _tokenExpiresAt = DateTime.MinValue;

    public void Configure(string webhookUrl, string secret = "")
    {
        _webhookUrl = webhookUrl;
        _secret = secret;
    }

    public void SetFeishuCredentials(string appId, string appSecret, DatabaseService? db = null)
    {
        _appId = appId;
        _appSecret = appSecret;
        _db = db;
    }

    public async Task ForwardAsync(Topic topic)
    {
        if (string.IsNullOrEmpty(_webhookUrl)) return;

        var (title, fullText) = FormatContent(topic);

        // Collect all image URLs
        var imageUrls = new List<string>();
        var imagePattern = @"!\[.*?\]\((.*?)\)";
        var imageMatches = Regex.Matches(fullText, imagePattern);

        foreach (Match match in imageMatches)
        {
            var url = match.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(url))
                imageUrls.Add(url);
        }

        // Also check topic.Talk.Images
        if (imageMatches.Count == 0 && topic.Talk?.Images?.Count > 0)
        {
            foreach (var img in topic.Talk.Images)
            {
                var url = img.Original?.Url ?? img.Large?.Url ?? img.Url;
                if (!string.IsNullOrEmpty(url))
                    imageUrls.Add(url);
            }
        }

        // Upload and send images to Feishu
        if (imageUrls.Count > 0 && !string.IsNullOrEmpty(_appId))
        {
            foreach (var imgUrl in imageUrls)
            {
                var imageKey = await GetFeishuImageKeyAsync(imgUrl);
                if (imageKey != null)
                {
                    await PostAsync(BuildImagePayload(imageKey));
                }
                else
                {
                    // Fallback: send as markdown card with image link
                    Log.Warning("Failed to upload image to Feishu, sending as card fallback");
                    await PostAsync(BuildPayload([$"![图片]({imgUrl})"]));
                }
            }
        }
        else if (imageUrls.Count > 0)
        {
            // No Feishu app credentials, send images as markdown cards
            foreach (var imgUrl in imageUrls)
            {
                await PostAsync(BuildPayload([$"![图片]({imgUrl})"]));
            }
        }

        // Send text (without image markdown)
        var textOnly = Regex.Replace(fullText, imagePattern, "").Trim();
        if (!string.IsNullOrWhiteSpace(textOnly))
        {
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

    /// <summary>
    /// Get Feishu image_key for a CDN image URL.
    /// Downloads the image, uploads to Feishu API, returns image_key.
    /// Uses DB cache to avoid re-uploading.
    /// </summary>
    private async Task<string?> GetFeishuImageKeyAsync(string imageUrl)
    {
        // Check DB cache by CDN URL
        if (_db != null)
        {
            var cached = _db.GetFeishuImageKey(imageUrl);
            if (!string.IsNullOrEmpty(cached))
                return cached;
        }

        try
        {
            // Download image
            var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl);

            // Upload to Feishu
            var imageKey = await UploadImageToFeishuAsync(imageBytes);
            if (imageKey != null && _db != null)
            {
                _db.SaveFeishuImageKey(imageUrl, imageKey);
            }
            return imageKey;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to upload image to Feishu: {Url}", imageUrl);
            return null;
        }
    }

    /// <summary>
    /// Upload image bytes to Feishu, returns image_key.
    /// </summary>
    private async Task<string?> UploadImageToFeishuAsync(byte[] imageBytes)
    {
        var token = await GetTenantTokenAsync();
        if (token == null) return null;

        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent("message"), "image_type");
            var fileContent = new ByteArrayContent(imageBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            content.Add(fileContent, "image", "image.jpg");

            var request = new HttpRequestMessage(HttpMethod.Post, "https://open.feishu.cn/open-apis/im/v1/images")
            {
                Content = content
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Feishu image upload HTTP error {Status}: {Body}", response.StatusCode, body);
                return null;
            }

            var json = JObject.Parse(body);
            var code = json["code"]?.Value<int>();
            if (code == 0)
            {
                var imageKey = json["data"]?["image_key"]?.Value<string>();
                Log.Debug("Feishu image uploaded: {ImageKey}", imageKey);
                return imageKey;
            }

            Log.Warning("Feishu image upload API error: {Body}", body);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Feishu image upload exception");
            return null;
        }
    }

    /// <summary>
    /// Get Feishu tenant_access_token, cached until expiry.
    /// </summary>
    private async Task<string?> GetTenantTokenAsync()
    {
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiresAt)
            return _cachedToken;

        try
        {
            var payload = new { app_id = _appId, app_secret = _appSecret };
            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(
                "https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal", content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Feishu token HTTP error {Status}: {Body}", response.StatusCode, body);
                return null;
            }

            var result = JObject.Parse(body);
            var code = result["code"]?.Value<int>();
            if (code == 0)
            {
                _cachedToken = result["tenant_access_token"]?.Value<string>();
                var expire = result["expire"]?.Value<int>() ?? 7200;
                _tokenExpiresAt = DateTime.UtcNow.AddSeconds(expire - 300); // Refresh 5 min early
                Log.Debug("Feishu token obtained, expires in {Expire}s", expire);
                return _cachedToken;
            }

            Log.Warning("Feishu token API error: {Body}", body);
            _cachedToken = null;
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Feishu token exception");
            return null;
        }
    }

    private object BuildImagePayload(string imageKey)
    {
        var msgData = new Dictionary<string, object>
        {
            ["msg_type"] = "image",
            ["content"] = new { image_key = imageKey }
        };

        if (!string.IsNullOrEmpty(_secret))
        {
            var (timestamp, sign) = SignPayload();
            msgData["timestamp"] = timestamp;
            msgData["sign"] = sign;
        }

        return msgData;
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

        if (!string.IsNullOrEmpty(_secret))
        {
            var (timestamp, sign) = SignPayload();
            msgData["timestamp"] = timestamp;
            msgData["sign"] = sign;
        }

        return msgData;
    }

    private (string timestamp, string sign) SignPayload()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var stringToSign = $"{timestamp}\n{_secret}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
        var sign = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        return (timestamp, sign);
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

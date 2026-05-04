using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

public class ImageHostingService : IDisposable
{
    private readonly DatabaseService _db;
    private readonly string _imagesDir;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _uploadClient;

    private const string UploadUrl = "https://api.gaotu.cn/v1/storage/upload";

    public int Port { get; }
    public string PublicHost { get; }
    public string BaseUrl { get; }

    public ImageHostingService(DatabaseService db, string imagesDir, int port = 8900, string publicHost = "localhost")
    {
        _db = db;
        Port = port;
        PublicHost = publicHost;
        _imagesDir = imagesDir;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _uploadClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        BaseUrl = $"http://{PublicHost}:{Port}";
    }

    public void Start()
    {
        Directory.CreateDirectory(_imagesDir);
    }

    public void Stop() { }

    public void Dispose()
    {
        Stop();
        _httpClient.Dispose();
        _uploadClient.Dispose();
    }

    /// <summary>
    /// Download a remote image, upload to image hosting, cache URL in DB.
    /// Returns the hosted CDN URL or null on failure.
    /// </summary>
    public async Task<string?> DownloadAndMapAsync(string remoteUrl, long topicId)
    {
        var urlHash = HashUrl(remoteUrl);

        // Check cache first
        var existing = _db.GetLocalImageUrl(urlHash);
        if (existing != null && existing.StartsWith("http"))
            return existing;

        try
        {
            // Download image
            var bytes = await _httpClient.GetByteArrayAsync(remoteUrl);

            // Determine extension from content or URL
            var ext = GetExtension(remoteUrl, bytes);

            // Save to local disk as backup
            var localPath = GetLocalPath(urlHash, ext);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllBytesAsync(localPath, bytes);

            // Upload to image hosting
            var hostedUrl = await UploadToImageHostAsync(bytes, $"zsxq_{urlHash}{ext}");
            if (hostedUrl != null)
            {
                Log.Debug("Image uploaded: {Hash} -> {Url}", urlHash, hostedUrl);
                _db.SaveLocalImage(urlHash, remoteUrl, localPath, hostedUrl);
                return hostedUrl;
            }

            // Upload failed, fallback to original URL
            Log.Warning("Image upload failed for {Hash}, using original URL", urlHash);
            return remoteUrl;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to download/upload image {Url}", remoteUrl);
            return remoteUrl;
        }
    }

    /// <summary>
    /// Upload image bytes to gaotu image hosting.
    /// Returns the CDN URL or null on failure.
    /// </summary>
    private async Task<string?> UploadToImageHostAsync(byte[] imageBytes, string fileName)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(imageBytes);

            // Determine content type from extension
            var ext = Path.GetExtension(fileName).ToLower();
            var contentType = ext switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };

            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            content.Add(fileContent, "file", fileName);

            var resp = await _uploadClient.PostAsync(UploadUrl, content);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                Log.Warning("Image upload HTTP error {Status}: {Body}", resp.StatusCode, body);
                return null;
            }

            var json = JObject.Parse(body);
            if (json["status"]?.Value<int>() == 0)
            {
                var url = json["data"]?["url"]?.Value<string>();
                return url;
            }

            Log.Warning("Image upload API error: {Body}", body);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Image upload exception");
            return null;
        }
    }

    /// <summary>
    /// Replace all remote image URLs in a Topic with hosted CDN URLs.
    /// Updates both the Images list and Text content.
    /// </summary>
    public async Task<Topic> ReplaceImageUrlsAsync(Topic topic)
    {
        if (topic.Talk?.Images == null || topic.Talk.Images.Count == 0)
            return topic;

        var imageUrlList = new List<string>();
        foreach (var img in topic.Talk.Images)
        {
            var remoteUrl = img.Original?.Url ?? img.Large?.Url ?? img.Url;
            if (string.IsNullOrEmpty(remoteUrl))
            {
                imageUrlList.Add(remoteUrl ?? "");
                continue;
            }

            var hostedUrl = await DownloadAndMapAsync(remoteUrl, topic.TopicId);
            var finalUrl = hostedUrl ?? remoteUrl;

            // Replace all URL fields so forwarders get CDN URL regardless of which field they read
            img.Url = finalUrl;
            if (img.Original != null) img.Original.Url = finalUrl;
            if (img.Large != null) img.Large.Url = finalUrl;
            if (img.Thumbnail != null) img.Thumbnail.Url = finalUrl;

            imageUrlList.Add(finalUrl);
        }

        topic.Talk.Text = ReplaceImageUrlsInText(topic.Talk.Text, imageUrlList);
        return topic;
    }

    private static string ReplaceImageUrlsInText(string text, List<string> localUrls)
    {
        var idx = 0;
        var result = new StringBuilder();
        var parts = text.Split("[图片]");
        for (var i = 0; i < parts.Length; i++)
        {
            result.Append(parts[i]);
            if (i < parts.Length - 1 && idx < localUrls.Count)
            {
                result.Append($"![图片]({localUrls[idx]})");
                idx++;
            }
            else if (i < parts.Length - 1)
            {
                result.Append("[图片]");
            }
        }
        return result.ToString();
    }

    private string GetLocalPath(string urlHash, string ext) => Path.Combine(_imagesDir, $"{urlHash}{ext}");

    private static string HashUrl(string url)
    {
        var pathPart = url.Split('?')[0];
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(pathPart));
        return Convert.ToHexString(bytes)[..16];
    }

    private static string GetExtension(string url, byte[] bytes)
    {
        // Check URL extension
        var pathPart = url.Split('?')[0];
        var ext = Path.GetExtension(pathPart).ToLower();
        if (ext is ".png" or ".gif" or ".webp" or ".jpg" or ".jpeg")
            return ext;

        // Check magic bytes
        if (bytes.Length >= 8)
        {
            if (bytes[0] == 0x89 && bytes[1] == 0x50) return ".png";  // PNG
            if (bytes[0] == 0x47 && bytes[1] == 0x49) return ".gif";  // GIF
            if (bytes[0] == 0x52 && bytes[1] == 0x49) return ".webp"; // WEBP (RIFF)
        }

        return ".jpg";
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

public class ImageHostingService : IDisposable
{
    private readonly DatabaseService _db;
    private readonly string _imagesDir;
    private readonly HttpListener _listener;
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _cts;

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
        _listener = new HttpListener();
        BaseUrl = $"http://{PublicHost}:{Port}";
    }

    public void Start()
    {
        Directory.CreateDirectory(_imagesDir);

        _listener.Prefixes.Add($"http://+:{Port}/images/");
        _listener.Start();

        _cts = new CancellationTokenSource();
        _ = ServeAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _listener.Close();
        _httpClient.Dispose();
    }

    /// <summary>
    /// Download a remote image, save to disk, store URL mapping in DB.
    /// Returns the local URL or null on failure.
    /// </summary>
    public async Task<string?> DownloadAndMapAsync(string remoteUrl, long topicId)
    {
        var urlHash = HashUrl(remoteUrl);

        // Check if already downloaded
        var existing = _db.GetLocalImageUrl(urlHash);
        if (existing != null)
        {
            // Verify file still exists on disk
            var existingPath = GetLocalPath(urlHash);
            if (File.Exists(existingPath))
                return existing;
        }

        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(remoteUrl);
            var localPath = GetLocalPath(urlHash);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllBytesAsync(localPath, bytes);

            var localUrl = $"{BaseUrl}/images/{urlHash}.jpg";
            _db.SaveLocalImage(urlHash, remoteUrl, localPath, localUrl);
            return localUrl;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Replace all remote image URLs in a Topic with local URLs.
    /// Downloads images that haven't been cached yet.
    /// </summary>
    public async Task<Topic> ReplaceImageUrlsAsync(Topic topic)
    {
        if (topic.Talk?.Images == null || topic.Talk.Images.Count == 0)
            return topic;

        var imageUrlList = new List<string>();
        foreach (var img in topic.Talk.Images)
        {
            var remoteUrl = img.Original?.Url ?? img.Large?.Url ?? img.Url;
            if (string.IsNullOrEmpty(remoteUrl)) continue;

            var localUrl = await DownloadAndMapAsync(remoteUrl, topic.TopicId);
            if (localUrl != null)
            {
                imageUrlList.Add(localUrl);
            }
            else
            {
                imageUrlList.Add(remoteUrl);
            }
        }

        // Also replace URLs in text content ([图片] placeholders already reference these)
        topic.Talk.Text = ReplaceImageUrlsInText(topic.Talk.Text, imageUrlList);

        return topic;
    }

    private static string ReplaceImageUrlsInText(string text, List<string> localUrls)
    {
        // Replace [图片] placeholders with local image URLs
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

    private string GetLocalPath(string urlHash) => Path.Combine(_imagesDir, $"{urlHash}.jpg");

    private static string HashUrl(string url)
    {
        // Strip query params for dedup (signed URLs have different signatures)
        var pathPart = url.Split('?')[0];
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(pathPart));
        return Convert.ToHexString(bytes)[..16]; // Use first 16 hex chars
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();

                var path = ctx.Request.Url?.LocalPath ?? "";
                if (path.StartsWith("/images/"))
                {
                    var fileName = path["/images/".Length..];
                    var filePath = Path.Combine(_imagesDir, fileName);

                    if (File.Exists(filePath))
                    {
                        ctx.Response.ContentType = "image/jpeg";
                        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                        var bytes = await File.ReadAllBytesAsync(filePath, ct);
                        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                    }
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                }

                ctx.Response.Close();
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { break; }
            catch { }
        }
    }
}

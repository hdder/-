using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Serilog;
using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

/// <summary>
/// Calls zsxq API directly with signature authentication.
/// Uses cookies extracted from WebView2 for auth.
/// </summary>
public class ZsxqApiService
{
    private const string Secret = "zsxqapi2020";
    private const string BaseUrl = "https://api.zsxq.com";
    private const string AppVersion = "3.11.0";
    private const string Platform = "ios";

    private readonly HttpClient _httpClient;
    private string _cookies = "";

    public ZsxqApiService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        _httpClient.DefaultRequestHeaders.Add("Origin", "https://wx.zsxq.com");
        _httpClient.DefaultRequestHeaders.Add("Referer", "https://wx.zsxq.com/");
    }

    public void SetCookies(string cookies)
    {
        _cookies = cookies;
        _httpClient.DefaultRequestHeaders.Remove("Cookie");
        if (!string.IsNullOrEmpty(cookies))
            _httpClient.DefaultRequestHeaders.Add("Cookie", cookies);
    }

    /// <summary>
    /// Set cookies from a raw cookie string (extracted externally).
    /// </summary>
    public void InitCookies(string cookies)
    {
        SetCookies(cookies);
    }

    public bool HasCookies => !string.IsNullOrEmpty(_cookies);

    // --- API Methods ---

    /// <summary>Get groups list (planets I've joined)</summary>
    public async Task<ApiResponse<GroupsRespData>?> GetGroupsAsync(int count = 50)
    {
        return await GetAsync<GroupsRespData>("/v1/groups", new Dictionary<string, string> { ["count"] = count.ToString() });
    }

    /// <summary>Get topics for a group</summary>
    public async Task<ApiResponse<TopicsRespData>?> GetTopicsAsync(long groupId, int count = 20, long? endTime = null)
    {
        var path = $"/v1/groups/{groupId}/topics";
        var parms = new Dictionary<string, string> { ["count"] = count.ToString() };
        if (endTime.HasValue)
            parms["end_time"] = endTime.Value.ToString();
        return await GetAsync<TopicsRespData>(path, parms);
    }

    // --- Core Request Logic ---

    private async Task<ApiResponse<T>?> GetAsync<T>(string path, Dictionary<string, string>? parms = null) where T : class
    {
        var (signature, timestamp) = GenerateSignature(path, parms);

        var url = $"{BaseUrl}{path}";
        if (parms != null && parms.Count > 0)
        {
            var qs = string.Join("&", parms.OrderBy(p => p.Key).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
            url += $"?{qs}";
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Signature", signature);
        req.Headers.Add("X-Timestamp", timestamp);

        try
        {
            var resp = await _httpClient.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                Log.Warning("API {Path} returned {Status}: {Body}", path, resp.StatusCode, body.Length > 200 ? body.Substring(0, 200) : body);
                return null;
            }

            return JsonConvert.DeserializeObject<ApiResponse<T>>(body);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "API request failed for {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Generate zsxq API signature.
    /// Sign string = path & sorted(key=value pairs) & secret
    /// Signature = MD5(sign string)
    /// </summary>
    private static (string signature, string timestamp) GenerateSignature(string path, Dictionary<string, string>? parms)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        var allParms = new Dictionary<string, string>
        {
            ["app_version"] = AppVersion,
            ["platform"] = Platform,
            ["timestamp"] = ts
        };

        if (parms != null)
        {
            foreach (var p in parms)
                allParms[p.Key] = p.Value;
        }

        // Sort by key name, join as key=value&
        var sorted = allParms.OrderBy(p => p.Key)
            .Select(p => $"{p.Key}={p.Value}");
        var paramsStr = string.Join("&", sorted);

        // sign_str = path & params & secret
        var signStr = $"{path}&{paramsStr}&{Secret}";

        // MD5
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(signStr));
        var sig = Convert.ToHexString(hash).ToLowerInvariant();

        return (sig, ts);
    }
}

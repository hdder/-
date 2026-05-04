using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Serilog;
using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

/// <summary>
/// Calls zsxq API directly with signature authentication.
/// Uses cookies extracted from WebView2 for auth.
/// Includes rate limiting, circuit breaker, and remote logging.
/// </summary>
public class ZsxqApiService
{
    // Configurable values (loaded from config)
    public string Secret { get; set; } = "zsxqapi2020";
    public string BaseUrl { get; set; } = "https://api.zsxq.com";
    public string AppVersion { get; set; } = "3.11.0";
    public string Platform { get; set; } = "ios";

    private readonly HttpClient _httpClient;
    private string _cookies = "";

    // Rate limiting
    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly int _minIntervalMs;

    // Circuit breaker
    private int _consecutiveFailures;
    private DateTime _circuitOpenUntil;
    private const int MaxFailuresBeforeBreak = 3;
    private const int CircuitBreakDurationSeconds = 60;

    // Remote logger
    private RemoteLogService? _remoteLogger;

    public ZsxqApiService(int minIntervalMs = 1000)
    {
        _minIntervalMs = minIntervalMs;
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

    public void SetRemoteLogger(RemoteLogService remoteLogger)
    {
        _remoteLogger = remoteLogger;
    }

    public void SetCookies(string cookies)
    {
        _cookies = cookies;
        _httpClient.DefaultRequestHeaders.Remove("Cookie");
        if (!string.IsNullOrEmpty(cookies))
            _httpClient.DefaultRequestHeaders.Add("Cookie", cookies);
    }

    public void InitCookies(string cookies)
    {
        SetCookies(cookies);
    }

    public bool HasCookies => !string.IsNullOrEmpty(_cookies);

    /// <summary>
    /// True when circuit breaker is open (API calls should be skipped).
    /// </summary>
    public bool IsCircuitOpen => _consecutiveFailures >= MaxFailuresBeforeBreak
        && DateTime.UtcNow < _circuitOpenUntil;

    public int ConsecutiveFailures => _consecutiveFailures;

    // --- API Methods ---

    public async Task<ApiResponse<GroupsRespData>?> GetGroupsAsync(int count = 50)
    {
        return await GetAsync<GroupsRespData>("/v1/groups", new Dictionary<string, string> { ["count"] = count.ToString() });
    }

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
        // Circuit breaker check
        if (IsCircuitOpen)
        {
            Log.Warning("Circuit breaker open, skipping API call to {Path}. Retries available after {Reset}",
                path, _circuitOpenUntil.ToLocalTime().ToString("HH:mm:ss"));
            return null;
        }

        // Rate limiting
        var elapsed = (DateTime.UtcNow - _lastRequestTime).TotalMilliseconds;
        if (elapsed < _minIntervalMs)
        {
            var delay = (int)(_minIntervalMs - elapsed);
            Log.Debug("Rate limit: waiting {Delay}ms before API call to {Path}", delay, path);
            await Task.Delay(delay);
        }
        _lastRequestTime = DateTime.UtcNow;

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
                Log.Warning("API {Path} returned {Status}: {Body}", path, resp.StatusCode,
                    body.Length > 200 ? body.Substring(0, 200) : body);
                RecordFailure(path, $"HTTP {(int)resp.StatusCode}");
                return null;
            }

            var result = JsonConvert.DeserializeObject<ApiResponse<T>>(body);
            if (result?.Succeeded == true)
            {
                RecordSuccess();
                return result;
            }

            Log.Warning("API {Path} returned succeeded=false: {Error}", path, result?.Error ?? "unknown");
            RecordFailure(path, result?.Error ?? "succeeded=false");
            return result;
        }
        catch (TaskCanceledException ex)
        {
            Log.Error(ex, "API timeout for {Path}", path);
            RecordFailure(path, "timeout");
            return null;
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "API network error for {Path}", path);
            RecordFailure(path, $"network: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "API request failed for {Path}", path);
            RecordFailure(path, ex.Message);
            return null;
        }
    }

    private void RecordSuccess()
    {
        if (_consecutiveFailures > 0)
        {
            Log.Information("API recovered after {Failures} failures, circuit breaker reset", _consecutiveFailures);
            _remoteLogger?.LogAsync("info", "API recovered", $"Recovered after {_consecutiveFailures} consecutive failures").Wait();
        }
        _consecutiveFailures = 0;
    }

    private void RecordFailure(string path, string reason)
    {
        _consecutiveFailures++;
        Log.Warning("API failure #{Count} for {Path}: {Reason}", _consecutiveFailures, path, reason);

        if (_consecutiveFailures >= MaxFailuresBeforeBreak)
        {
            _circuitOpenUntil = DateTime.UtcNow.AddSeconds(CircuitBreakDurationSeconds);
            Log.Warning("Circuit breaker OPEN - API calls disabled until {Reset}. Falling back to DOM.",
                _circuitOpenUntil.ToLocalTime().ToString("HH:mm:ss"));
            _remoteLogger?.LogAsync("warning", "Circuit breaker opened",
                $"API failures: {_consecutiveFailures}. Disabled until {_circuitOpenUntil:HH:mm:ss}. Path: {path}. Reason: {reason}").Wait();
        }
        else
        {
            _remoteLogger?.LogAsync("error", "API request failed",
                $"Path: {path}. Reason: {reason}. Consecutive failures: {_consecutiveFailures}").Wait();
        }
    }

    /// <summary>
    /// Generate zsxq API signature.
    /// Sign string = path & sorted(key=value pairs) & secret
    /// Signature = MD5(sign string)
    /// </summary>
    private (string signature, string timestamp) GenerateSignature(string path, Dictionary<string, string>? parms)
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

        var sorted = allParms.OrderBy(p => p.Key)
            .Select(p => $"{p.Key}={p.Value}");
        var paramsStr = string.Join("&", sorted);

        var signStr = $"{path}&{paramsStr}&{Secret}";

        var hash = MD5.HashData(Encoding.UTF8.GetBytes(signStr));
        var sig = Convert.ToHexString(hash).ToLowerInvariant();

        return (sig, ts);
    }
}

using System.Text;
using Newtonsoft.Json;
using Serilog;

namespace ZsxqForwarder.Core.Services;

/// <summary>
/// Sends log entries to the remote log server.
/// Fire-and-forget: never blocks the caller on failure.
/// </summary>
public class RemoteLogService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiToken;
    private bool _enabled = true;

    public RemoteLogService(string serverUrl, string apiToken = "zsxq-log-2024")
    {
        _apiToken = apiToken;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        _httpClient.DefaultRequestHeaders.Add("X-Token", _apiToken);
        _httpClient.BaseAddress = new Uri(serverUrl.TrimEnd('/'));

        // Verify connectivity
        _ = CheckHealthAsync();
    }

    private async Task CheckHealthAsync()
    {
        try
        {
            var resp = await _httpClient.GetAsync("/api/health");
            if (resp.IsSuccessStatusCode)
            {
                Log.Information("Remote log server connected: {Url}", _httpClient.BaseAddress);
            }
            else
            {
                Log.Warning("Remote log server returned {Status}", resp.StatusCode);
                _enabled = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Remote log server unreachable: {Message}", ex.Message);
            _enabled = false;
        }
    }

    public async Task LogAsync(string level, string title, string message = "", string source = "desktop")
    {
        if (!_enabled) return;

        try
        {
            var payload = new
            {
                level,
                title = Truncate(title, 500),
                message = Truncate(message, 5000),
                source
            };

            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _httpClient.PostAsync("/api/logs", content);

            if (!resp.IsSuccessStatusCode)
            {
                // Re-enable on success path, disable on auth failure
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _enabled = false;
                    Log.Warning("Remote log server auth failed, disabling remote logging");
                }
            }
            else
            {
                _enabled = true;
            }
        }
        catch
        {
            // Silently ignore - remote logging must never crash the app
        }
    }

    public async Task LogErrorAsync(string title, string message = "")
    {
        await LogAsync("error", title, message);
    }

    public async Task LogWarningAsync(string title, string message = "")
    {
        await LogAsync("warning", title, message);
    }

    public async Task LogInfoAsync(string title, string message = "")
    {
        await LogAsync("info", title, message);
    }

    private static string Truncate(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= maxLen ? s : s.Substring(0, maxLen);
    }
}

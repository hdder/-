using System.Globalization;
using System.Net;
using Newtonsoft.Json;
using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Api;

public class ZsxqApiClient
{
    private readonly HttpClient _httpClient;
    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly SemaphoreSlim _rateLimitLock = new(1, 1);
    private const int MinRequestIntervalMs = 2000;

    public ZsxqApiClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false
        };

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.zsxq.com")
        };

        _httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        _httpClient.DefaultRequestHeaders.Add("Origin", "https://wx.zsxq.com");
        _httpClient.DefaultRequestHeaders.Add("Referer", "https://wx.zsxq.com/");
    }

    public void SetAccessToken(string token)
    {
        _httpClient.DefaultRequestHeaders.Remove("Cookie");
        _httpClient.DefaultRequestHeaders.Add("Cookie", $"zsxq_access_token={token}");
    }

    public bool IsAuthenticated =>
        _httpClient.DefaultRequestHeaders.Contains("Cookie");

    public async Task<List<Group>> GetGroupsAsync()
    {
        var path = "/v2/groups";
        var (signature, timestamp) = SignatureHelper.GenerateSignature(path);

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{path}?app_version=3.11.0&platform=ios&timestamp={timestamp}&sign={signature}");

        var response = await SendWithRateLimitAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        var result = JsonConvert.DeserializeObject<ApiResponse<GroupsRespData>>(json);
        if (result?.Succeeded == true && result.RespData != null)
        {
            return result.RespData.Groups;
        }

        throw new ApiException($"Failed to get groups: {result?.Error}", result?.Code ?? -1);
    }

    public async Task<(List<Topic> Topics, bool IsEnd)> GetTopicsAsync(
        int groupId, int count = 20, bool backward = true, long? endTime = null)
    {
        var path = $"/v2/groups/{groupId}/topics";
        var businessParams = new Dictionary<string, string>
        {
            ["count"] = count.ToString(),
            ["scope"] = "all"
        };

        if (endTime.HasValue)
        {
            businessParams["end_time"] = endTime.Value.ToString();
        }

        var (signature, timestamp) = SignatureHelper.GenerateSignature(path, businessParams);

        var queryParams = new List<string>
        {
            $"app_version=3.11.0",
            $"platform=ios",
            $"timestamp={timestamp}",
            $"sign={signature}",
            $"count={count}",
            $"scope=all"
        };

        if (endTime.HasValue)
        {
            queryParams.Add($"end_time={endTime.Value}");
        }

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{path}?{string.Join("&", queryParams)}");

        var response = await SendWithRateLimitAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        var result = JsonConvert.DeserializeObject<ApiResponse<TopicsRespData>>(json);
        if (result?.Succeeded == true && result.RespData != null)
        {
            return (result.RespData.Topics, result.RespData.IsEnd);
        }

        throw new ApiException($"Failed to get topics: {result?.Error}", result?.Code ?? -1);
    }

    public async Task<List<Comment>> GetCommentsAsync(long topicId)
    {
        var path = $"/v2/topics/{topicId}/comments";
        var (signature, timestamp) = SignatureHelper.GenerateSignature(path);

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{path}?app_version=3.11.0&platform=ios&timestamp={timestamp}&sign={signature}");

        var response = await SendWithRateLimitAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        var result = JsonConvert.DeserializeObject<CommentResponse>(json);
        if (result?.Succeeded == true && result.RespData != null)
        {
            return result.RespData.Comments;
        }

        throw new ApiException($"Failed to get comments: {json}", -1);
    }

    private async Task<HttpResponseMessage> SendWithRateLimitAsync(HttpRequestMessage request)
    {
        await _rateLimitLock.WaitAsync();
        try
        {
            var elapsed = (DateTime.UtcNow - _lastRequestTime).TotalMilliseconds;
            if (elapsed < MinRequestIntervalMs)
            {
                await Task.Delay(MinRequestIntervalMs - (int)elapsed);
            }

            var response = await _httpClient.SendAsync(request);
            _lastRequestTime = DateTime.UtcNow;

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new ApiException("Rate limited - too many requests", 429);
            }

            response.EnsureSuccessStatusCode();
            return response;
        }
        finally
        {
            _rateLimitLock.Release();
        }
    }
}

public class ApiException : Exception
{
    public int Code { get; }

    public ApiException(string message, int code) : base(message)
    {
        Code = code;
    }
}

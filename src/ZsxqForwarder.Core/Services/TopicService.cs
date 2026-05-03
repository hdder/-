using Newtonsoft.Json;
using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

public class TopicService
{
    private readonly Func<string, Task<string>> _fetchJson;

    public TopicService(Func<string, Task<string>> fetchJson)
    {
        _fetchJson = fetchJson;
    }

    public async Task<List<Group>> GetGroupsAsync()
    {
        var json = await _fetchJson("https://api.zsxq.com/v2/groups");
        var result = JsonConvert.DeserializeObject<ApiResponse<GroupsRespData>>(json);
        if (result?.Succeeded == true && result.RespData != null)
            return result.RespData.Groups;
        throw new Exception($"Failed to get groups: {result?.Error}");
    }

    public async Task<List<Topic>> GetAllTopicsAsync(long groupId, IProgress<(int Loaded, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var allTopics = new List<Topic>();
        long? endTime = null;
        var isEnd = false;

        while (!isEnd && !cancellationToken.IsCancellationRequested)
        {
            var url = $"https://api.zsxq.com/v2/groups/{groupId}/topics?count=20&scope=all";
            if (endTime.HasValue)
                url += $"&end_time={endTime.Value}";

            var json = await _fetchJson(url);
            var result = JsonConvert.DeserializeObject<ApiResponse<TopicsRespData>>(json);
            if (result?.Succeeded != true || result.RespData == null)
                break;

            isEnd = result.RespData.IsEnd;
            if (result.RespData.Topics.Count == 0) break;

            allTopics.AddRange(result.RespData.Topics);
            progress?.Report((allTopics.Count, allTopics.Count));
            endTime = result.RespData.Topics.Min(t => t.CreateTime);
        }

        return allTopics;
    }

    public async Task<List<Topic>> GetLatestTopicsAsync(long groupId, int count = 5)
    {
        var url = $"https://api.zsxq.com/v2/groups/{groupId}/topics?count={count}&scope=all";
        var json = await _fetchJson(url);
        var result = JsonConvert.DeserializeObject<ApiResponse<TopicsRespData>>(json);
        if (result?.Succeeded == true && result.RespData != null)
            return result.RespData.Topics;
        return [];
    }

    public async Task<List<Comment>> GetCommentsAsync(long topicId)
    {
        var url = $"https://api.zsxq.com/v2/topics/{topicId}/comments";
        var json = await _fetchJson(url);
        var result = JsonConvert.DeserializeObject<CommentResponse>(json);
        if (result?.Succeeded == true && result.RespData != null)
            return result.RespData.Comments;
        throw new Exception($"Failed to get comments");
    }

    public async Task<(List<Dynamic> Dynamics, bool IsEnd)> FetchDynamicsPageAsync(string? endTime = null, int count = 30)
    {
        var url = $"https://api.zsxq.com/v2/dynamics?scope=general&count={count}";
        if (!string.IsNullOrEmpty(endTime))
            url += $"&end_time={Uri.EscapeDataString(endTime)}";

        var json = await _fetchJson(url);
        var result = JsonConvert.DeserializeObject<ApiResponse<DynamicsRespData>>(json);
        if (result?.Succeeded == true && result.RespData != null)
            return (result.RespData.Dynamics, result.RespData.IsEnd);
        throw new Exception($"Failed to fetch dynamics: code={result?.Code}, error={result?.Error}, raw={json?.Substring(0, Math.Min(200, json?.Length ?? 0))}");
    }

    public async Task<List<Dynamic>> FetchAllDynamicsAsync(
        IProgress<(int Loaded, bool IsComplete)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var allDynamics = new List<Dynamic>();
        string? endTime = null;
        var isEnd = false;

        while (!isEnd && !cancellationToken.IsCancellationRequested)
        {
            var (dynamics, pageEnd) = await FetchDynamicsPageAsync(endTime, 30);
            isEnd = pageEnd;

            if (dynamics.Count == 0) break;

            allDynamics.AddRange(dynamics);
            progress?.Report((allDynamics.Count, isEnd));

            // Use last dynamic's create_time for pagination
            endTime = dynamics.Last().CreateTimeStr;
        }

        return allDynamics;
    }
}

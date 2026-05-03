using ZsxqForwarder.Core.Api;
using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

public class TopicService
{
    private readonly ZsxqApiClient _apiClient;

    public TopicService(ZsxqApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public async Task<List<Group>> GetGroupsAsync()
    {
        return await _apiClient.GetGroupsAsync();
    }

    public async Task<List<Topic>> GetAllTopicsAsync(int groupId, IProgress<(int Loaded, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var allTopics = new List<Topic>();
        long? endTime = null;
        var isEnd = false;
        var loaded = 0;

        while (!isEnd && !cancellationToken.IsCancellationRequested)
        {
            var (topics, ended) = await _apiClient.GetTopicsAsync(groupId, count: 20, endTime: endTime);
            isEnd = ended;

            if (topics.Count == 0)
                break;

            allTopics.AddRange(topics);
            loaded += topics.Count;
            progress?.Report((loaded, allTopics.Count));

            // Use the earliest topic's create_time as endTime for next page
            endTime = topics.Min(t => t.CreateTime);
        }

        return allTopics;
    }

    public async Task<List<Topic>> GetLatestTopicsAsync(int groupId, int count = 5)
    {
        var (topics, _) = await _apiClient.GetTopicsAsync(groupId, count: count);
        return topics;
    }

    public async Task<List<Comment>> GetCommentsAsync(long topicId)
    {
        return await _apiClient.GetCommentsAsync(topicId);
    }
}

using Newtonsoft.Json;

namespace ZsxqForwarder.Core.Models;

public class ApiResponse<T>
{
    [JsonProperty("succeeded")]
    public bool Succeeded { get; set; }

    [JsonProperty("code")]
    public int Code { get; set; }

    [JsonProperty("error")]
    public string Error { get; set; } = string.Empty;

    [JsonProperty("resp_data")]
    public T? RespData { get; set; }
}

public class GroupsRespData
{
    [JsonProperty("groups")]
    public List<Group> Groups { get; set; } = [];
}

public class TopicsRespData
{
    [JsonProperty("topics")]
    public List<Topic> Topics { get; set; } = [];

    [JsonProperty("is_end")]
    public bool IsEnd { get; set; }
}

public class DynamicsRespData
{
    [JsonProperty("dynamics")]
    public List<Dynamic> Dynamics { get; set; } = [];

    [JsonProperty("is_end")]
    public bool IsEnd { get; set; }
}

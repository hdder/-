using Newtonsoft.Json;

namespace ZsxqForwarder.Core.Models;

public class Group
{
    [JsonProperty("group_id")]
    public int GroupId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("member_count")]
    public int MemberCount { get; set; }

    [JsonProperty("create_time")]
    public long CreateTime { get; set; }

    [JsonProperty("owner")]
    public User? Owner { get; set; }

    [JsonProperty("avatar_url")]
    public string AvatarUrl { get; set; } = string.Empty;

    [JsonProperty("background_url")]
    public string BackgroundUrl { get; set; } = string.Empty;
}

using Newtonsoft.Json;

namespace ZsxqForwarder.Core.Models;

public class User
{
    [JsonProperty("user_id")]
    public int UserId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("avatar_url")]
    public string AvatarUrl { get; set; } = string.Empty;

    [JsonProperty("location")]
    public string Location { get; set; } = string.Empty;

    [JsonProperty("introduction")]
    public string Introduction { get; set; } = string.Empty;
}

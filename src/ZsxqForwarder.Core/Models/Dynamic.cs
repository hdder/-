using Newtonsoft.Json;

namespace ZsxqForwarder.Core.Models;

public class Dynamic
{
    [JsonProperty("dynamic_id")]
    public long DynamicId { get; set; }

    [JsonProperty("action")]
    public string Action { get; set; } = string.Empty;

    [JsonProperty("create_time")]
    public string CreateTimeStr { get; set; } = string.Empty;

    [JsonProperty("topic")]
    public Topic? Topic { get; set; }

    [JsonProperty("group")]
    public DynamicGroup? Group { get; set; }

    [JsonProperty("collapse_id")]
    public long? CollapseId { get; set; }

    /// <summary>
    /// Parse ISO 8601 create_time to unix milliseconds for Topic.CreateTime compatibility.
    /// </summary>
    public long CreateTimeMs
    {
        get
        {
            var trimmed = CreateTimeStr?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return 0;

            // DOM format: "2026-05-03 17:01"
            if (DateTimeOffset.TryParseExact(trimmed, "yyyy-MM-dd HH:mm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var dto))
                return dto.ToUnixTimeMilliseconds();

            // Fallback for ISO 8601 or other formats
            if (DateTimeOffset.TryParse(trimmed,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out dto))
                return dto.ToUnixTimeMilliseconds();

            return 0;
        }
    }
}

public class DynamicGroup
{
    [JsonProperty("group_id")]
    public long GroupId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("background_url")]
    public string BackgroundUrl { get; set; } = string.Empty;

    [JsonProperty("avatar_url")]
    public string AvatarUrl { get; set; } = string.Empty;
}

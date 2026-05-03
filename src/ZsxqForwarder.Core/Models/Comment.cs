using Newtonsoft.Json;

namespace ZsxqForwarder.Core.Models;

public class Comment
{
    [JsonProperty("comment_id")]
    public long CommentId { get; set; }

    [JsonProperty("owner")]
    public User? Owner { get; set; }

    [JsonProperty("create_time")]
    public long CreateTime { get; set; }

    [JsonProperty("text")]
    public string Text { get; set; } = string.Empty;

    [JsonProperty("images")]
    public List<ImageInfo> Images { get; set; } = [];

    [JsonProperty("likes_count")]
    public int LikesCount { get; set; }

    [JsonProperty("reply_user")]
    public User? ReplyUser { get; set; }

    public DateTime CreatedAt => DateTimeOffset.FromUnixTimeMilliseconds(CreateTime).ToLocalTime().DateTime;
}

public class CommentResponse
{
    [JsonProperty("succeeded")]
    public bool Succeeded { get; set; }

    [JsonProperty("resp_data")]
    public CommentRespData? RespData { get; set; }
}

public class CommentRespData
{
    [JsonProperty("comments")]
    public List<Comment> Comments { get; set; } = [];

    [JsonProperty("is_end")]
    public bool IsEnd { get; set; }
}

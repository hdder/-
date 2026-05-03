using Newtonsoft.Json;

namespace ZsxqForwarder.Core.Models;

public class Topic
{
    [JsonProperty("topic_id")]
    public long TopicId { get; set; }

    [JsonProperty("topic_uid")]
    public string TopicUid { get; set; } = string.Empty;

    [JsonProperty("group")]
    public GroupInfo? Group { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty; // talk, task, q&a, solution

    [JsonProperty("create_time")]
    public long CreateTime { get; set; }

    [JsonProperty("talk")]
    public TalkContent? Talk { get; set; }

    [JsonProperty("task")]
    public TaskContent? Task { get; set; }

    [JsonProperty("question")]
    public QuestionContent? Question { get; set; }

    [JsonProperty("likes_count")]
    public int LikesCount { get; set; }

    [JsonProperty("comments_count")]
    public int CommentsCount { get; set; }

    [JsonProperty("digested")]
    public bool Digested { get; set; }

    [JsonProperty("sticky")]
    public bool Sticky { get; set; }

    public DateTime CreatedAt => DateTimeOffset.FromUnixTimeMilliseconds(CreateTime).ToLocalTime().DateTime;
}

public class GroupInfo
{
    [JsonProperty("group_id")]
    public long GroupId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}

public class TalkContent
{
    [JsonProperty("owner")]
    public User? Owner { get; set; }

    [JsonProperty("text")]
    public string Text { get; set; } = string.Empty;

    [JsonProperty("images")]
    public List<ImageInfo> Images { get; set; } = [];

    [JsonProperty("files")]
    public List<FileInfo> Files { get; set; } = [];
}

public class TaskContent
{
    [JsonProperty("owner")]
    public User? Owner { get; set; }

    [JsonProperty("text")]
    public string Text { get; set; } = string.Empty;

    [JsonProperty("action_text")]
    public string ActionText { get; set; } = string.Empty;
}

public class QuestionContent
{
    [JsonProperty("owner")]
    public User? Owner { get; set; }

    [JsonProperty("text")]
    public string Text { get; set; } = string.Empty;

    [JsonProperty("solution_count")]
    public int SolutionCount { get; set; }
}

public class ImageInfo
{
    [JsonProperty("url")]
    public string Url { get; set; } = string.Empty;

    [JsonProperty("large")]
    public ImageSize? Large { get; set; }

    [JsonProperty("original")]
    public ImageSize? Original { get; set; }

    [JsonProperty("thumbnail")]
    public ImageSize? Thumbnail { get; set; }
}

public class ImageSize
{
    [JsonProperty("url")]
    public string Url { get; set; } = string.Empty;

    [JsonProperty("width")]
    public int Width { get; set; }

    [JsonProperty("height")]
    public int Height { get; set; }
}

public class FileInfo
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("url")]
    public string Url { get; set; } = string.Empty;

    [JsonProperty("size")]
    public long Size { get; set; }
}

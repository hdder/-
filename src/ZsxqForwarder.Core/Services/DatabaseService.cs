using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using ZsxqForwarder.Core.Models;

namespace ZsxqForwarder.Core.Services;

public class DatabaseService
{
    private static readonly string DbDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ZsxqForwarder");

    private static readonly string DbPath = Path.Combine(DbDir, "data.db");

    private string ConnectionString => $"Data Source={DbPath}";

    public static string GetImagesDir() => Path.Combine(DbDir, "images");

    public void Init()
    {
        Directory.CreateDirectory(DbDir);

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS Groups (
                GroupId INTEGER PRIMARY KEY,
                Name TEXT NOT NULL,
                Url TEXT NOT NULL DEFAULT '',
                AvatarUrl TEXT NOT NULL DEFAULT '',
                BackgroundUrl TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS ForwardRules (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GroupId INTEGER NOT NULL,
                GroupName TEXT NOT NULL,
                ForwarderType TEXT NOT NULL,
                WebhookUrl TEXT NOT NULL,
                Secret TEXT DEFAULT '',
                Enabled INTEGER DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS Topics (
                TopicId INTEGER PRIMARY KEY,
                GroupId INTEGER NOT NULL,
                Type TEXT NOT NULL,
                Author TEXT NOT NULL,
                Content TEXT NOT NULL,
                CreateTime INTEGER NOT NULL,
                RawJson TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ForwardLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TopicId INTEGER NOT NULL,
                GroupId INTEGER NOT NULL,
                GroupName TEXT NOT NULL,
                Author TEXT NOT NULL,
                ContentPreview TEXT NOT NULL,
                ForwarderType TEXT NOT NULL,
                WebhookUrl TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Success',
                ErrorMessage TEXT,
                ForwardedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS SyncState (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS LocalImages (
                UrlHash TEXT PRIMARY KEY,
                RemoteUrl TEXT NOT NULL,
                LocalPath TEXT NOT NULL,
                LocalUrl TEXT NOT NULL,
                DownloadedAt TEXT NOT NULL
            );
        ");

        // Migrate existing DB: add new columns if missing
        Migrate(conn);
    }

    private static void Migrate(SqliteConnection conn)
    {
        // Add AvatarUrl column to Groups if missing
        try { conn.Execute("ALTER TABLE Groups ADD COLUMN AvatarUrl TEXT NOT NULL DEFAULT ''"); }
        catch { /* column already exists */ }

        try { conn.Execute("ALTER TABLE Groups ADD COLUMN BackgroundUrl TEXT NOT NULL DEFAULT ''"); }
        catch { /* column already exists */ }
    }

    // Groups
    public List<GroupConfig> GetGroups()
    {
        using var conn = new SqliteConnection(ConnectionString);
        return conn.Query<GroupConfig>("SELECT * FROM Groups").ToList();
    }

    public void SaveGroup(GroupConfig group)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Execute("INSERT OR REPLACE INTO Groups (GroupId, Name, Url, AvatarUrl, BackgroundUrl) VALUES (@GroupId, @Name, @Url, @AvatarUrl, @BackgroundUrl)", group);
    }

    public void RemoveGroup(long groupId)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Execute("DELETE FROM Groups WHERE GroupId = @GroupId", new { GroupId = groupId });
        conn.Execute("DELETE FROM ForwardRules WHERE GroupId = @GroupId", new { GroupId = groupId });
    }

    public void SaveDiscoveredGroup(long groupId, string name, string avatarUrl, string backgroundUrl)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Execute(
            "INSERT OR REPLACE INTO Groups (GroupId, Name, Url, AvatarUrl, BackgroundUrl) VALUES (@GroupId, @Name, @Url, @AvatarUrl, @BackgroundUrl)",
            new { GroupId = groupId, Name = name, Url = $"https://wx.zsxq.com/group/{groupId}", AvatarUrl = avatarUrl, BackgroundUrl = backgroundUrl });
    }

    // Forward Rules
    public List<ForwardRule> GetForwardRules()
    {
        using var conn = new SqliteConnection(ConnectionString);
        return conn.Query<ForwardRule>("SELECT * FROM ForwardRules").ToList();
    }

    public void SaveForwardRule(ForwardRule rule)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Execute(@"INSERT INTO ForwardRules (GroupId, GroupName, ForwarderType, WebhookUrl, Secret, Enabled)
                       VALUES (@GroupId, @GroupName, @ForwarderType, @WebhookUrl, @Secret, @Enabled)", rule);
    }

    public void RemoveForwardRule(int id)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Execute("DELETE FROM ForwardRules WHERE Id = @Id", new { Id = id });
    }

    // Topics
    public void SaveTopic(Topic topic, long groupId)
    {
        using var conn = new SqliteConnection(ConnectionString);
        var author = topic.Talk?.Owner?.Name ?? topic.Task?.Owner?.Name ?? "Unknown";
        var content = topic.Talk?.Text ?? topic.Task?.Text ?? topic.Question?.Text ?? "";
        conn.Execute(@"INSERT OR REPLACE INTO Topics (TopicId, GroupId, Type, Author, Content, CreateTime, RawJson)
                       VALUES (@TopicId, @GroupId, @Type, @Author, @Content, @CreateTime, @RawJson)",
            new
            {
                TopicId = topic.TopicId,
                GroupId = groupId,
                Type = topic.Type,
                Author = author,
                Content = content,
                CreateTime = topic.CreateTime,
                RawJson = JsonConvert.SerializeObject(topic)
            });
    }

    public Topic? GetTopic(long topicId)
    {
        using var conn = new SqliteConnection(ConnectionString);
        var raw = conn.QueryFirstOrDefault<string>("SELECT RawJson FROM Topics WHERE TopicId = @TopicId", new { TopicId = topicId });
        if (raw == null) return null;
        return JsonConvert.DeserializeObject<Topic>(raw);
    }

    public List<Topic> GetTopicsByGroup(long groupId, int limit = 50, int offset = 0)
    {
        using var conn = new SqliteConnection(ConnectionString);
        var raws = conn.Query<string>(
            "SELECT RawJson FROM Topics WHERE GroupId = @GroupId ORDER BY CreateTime DESC LIMIT @Limit OFFSET @Offset",
            new { GroupId = groupId, Limit = limit, Offset = offset }).ToList();
        return raws.Select(r => JsonConvert.DeserializeObject<Topic>(r)!).ToList();
    }

    public List<Topic> GetTopicsByGroupAndDate(long groupId, DateTime date)
    {
        var start = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var end = new DateTimeOffset(date.Year, date.Month, date.Day, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds();

        using var conn = new SqliteConnection(ConnectionString);
        var raws = conn.Query<string>(
            "SELECT RawJson FROM Topics WHERE GroupId = @GroupId AND CreateTime >= @Start AND CreateTime <= @End ORDER BY CreateTime DESC",
            new { GroupId = groupId, Start = start, End = end }).ToList();
        return raws.Select(r => JsonConvert.DeserializeObject<Topic>(r)!).ToList();
    }

    public int GetTopicCountByGroup(long groupId)
    {
        using var conn = new SqliteConnection(ConnectionString);
        return conn.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM Topics WHERE GroupId = @GroupId", new { GroupId = groupId });
    }

    // Batch save dynamics (transaction-wrapped)
    public void SaveDynamicsBatch(List<Dynamic> dynamics)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();

        foreach (var d in dynamics)
        {
            // Upsert group - use name-based ID if group_id is 0 (from DOM extraction)
            if (d.Group != null && !string.IsNullOrEmpty(d.Group.Name))
            {
                var groupId = d.Group.GroupId > 0 ? d.Group.GroupId : GetGroupIdByName(conn, d.Group.Name, tx);
                conn.Execute(
                    "INSERT OR REPLACE INTO Groups (GroupId, Name, Url, AvatarUrl, BackgroundUrl) VALUES (@GroupId, @Name, @Url, @AvatarUrl, @BackgroundUrl)",
                    new
                    {
                        GroupId = groupId,
                        Name = d.Group.Name,
                        Url = $"https://wx.zsxq.com/group/{groupId}",
                        AvatarUrl = d.Group.AvatarUrl ?? "",
                        BackgroundUrl = d.Group.BackgroundUrl ?? ""
                    },
                    tx);
            }

            // Save topic if action is create_topic
            if (d.Topic != null && d.Action == "create_topic")
            {
                var topic = d.Topic;
                var groupId = d.Group?.GroupId ?? topic.Group?.GroupId ?? 0;
                if (groupId == 0 && d.Group?.Name != null)
                    groupId = GetGroupIdByName(conn, d.Group.Name, tx);
                var author = topic.Talk?.Owner?.Name ?? topic.Task?.Owner?.Name ?? topic.Question?.Owner?.Name ?? "Unknown";
                var content = topic.Talk?.Text ?? topic.Task?.Text ?? topic.Question?.Text ?? "";
                var createTime = topic.CreateTime > 0 ? topic.CreateTime : d.CreateTimeMs;

                // Generate topic_id from content hash if missing (DOM extraction has no IDs)
                var topicId = topic.TopicId;
                if (topicId == 0 && !string.IsNullOrEmpty(content))
                    topicId = ContentHash(content);

                // Use TopicId as dedup key: INSERT OR IGNORE
                conn.Execute(
                    @"INSERT OR IGNORE INTO Topics (TopicId, GroupId, Type, Author, Content, CreateTime, RawJson)
                      VALUES (@TopicId, @GroupId, @Type, @Author, @Content, @CreateTime, @RawJson)",
                    new
                    {
                        TopicId = topicId,
                        GroupId = groupId,
                        Type = topic.Type,
                        Author = author,
                        Content = content,
                        CreateTime = createTime,
                        RawJson = JsonConvert.SerializeObject(topic)
                    },
                    tx);
            }
        }

        // Update sync state
        if (dynamics.Count > 0)
        {
            var maxDynamicId = dynamics.Max(d => d.DynamicId);
            conn.Execute("INSERT OR REPLACE INTO SyncState (Key, Value) VALUES ('last_dynamic_id', @Value)",
                new { Value = maxDynamicId.ToString() }, tx);
            conn.Execute("INSERT OR REPLACE INTO SyncState (Key, Value) VALUES ('last_sync_time', @Value)",
                new { Value = DateTime.Now.ToString("O") }, tx);
        }

        tx.Commit();
    }

    private static long GetGroupIdByName(SqliteConnection conn, string name, SqliteTransaction tx)
    {
        var existing = conn.QueryFirstOrDefault<long?>(
            "SELECT GroupId FROM Groups WHERE Name = @Name", new { Name = name }, tx);
        if (existing.HasValue && existing.Value > 0) return existing.Value;

        // Generate a stable negative ID from name hash (to distinguish from real API IDs)
        return -((long)name.GetHashCode(StringComparison.Ordinal) & 0x7FFFFFFF);
    }

    private static long ContentHash(string content)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return BitConverter.ToInt64(hash, 0) & 0x7FFFFFFFFFFFFFFF; // Ensure positive
    }

    public long GetLastSyncedDynamicId()
    {
        var val = GetSyncState("last_dynamic_id");
        return long.TryParse(val, out var id) ? id : 0;
    }

    // Forward Log
    public void AddForwardLog(ForwardLogEntry entry)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Execute(@"INSERT INTO ForwardLog (TopicId, GroupId, GroupName, Author, ContentPreview, ForwarderType, WebhookUrl, Status, ErrorMessage, ForwardedAt)
                       VALUES (@TopicId, @GroupId, @GroupName, @Author, @ContentPreview, @ForwarderType, @WebhookUrl, @Status, @ErrorMessage, @ForwardedAt)", entry);
    }

    public List<ForwardLogEntry> GetForwardLogs(int limit = 100)
    {
        using var conn = new SqliteConnection(ConnectionString);
        return conn.Query<ForwardLogEntry>("SELECT * FROM ForwardLog ORDER BY Id DESC LIMIT @Limit", new { Limit = limit }).ToList();
    }

    public List<ForwardLogEntry> GetForwardLogsByGroup(long groupId, int limit = 100)
    {
        using var conn = new SqliteConnection(ConnectionString);
        return conn.Query<ForwardLogEntry>("SELECT * FROM ForwardLog WHERE GroupId = @GroupId ORDER BY Id DESC LIMIT @Limit", new { GroupId = groupId, Limit = limit }).ToList();
    }

    // Settings
    public string? GetSetting(string key)
    {
        using var conn = new SqliteConnection(ConnectionString);
        return conn.QueryFirstOrDefault<string>("SELECT Value FROM Settings WHERE Key = @Key", new { Key = key });
    }

    public void SetSetting(string key, string value)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Execute("INSERT OR REPLACE INTO Settings (Key, Value) VALUES (@Key, @Value)", new { Key = key, Value = value });
    }

    public int GetMonitorInterval()
    {
        var val = GetSetting("MonitorInterval");
        return int.TryParse(val, out var interval) ? interval : 30;
    }

    public void SetMonitorInterval(int seconds)
    {
        SetSetting("MonitorInterval", seconds.ToString());
    }

    // Local Images
    public void SaveLocalImage(string urlHash, string remoteUrl, string localPath, string localUrl)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Execute(
            "INSERT OR REPLACE INTO LocalImages (UrlHash, RemoteUrl, LocalPath, LocalUrl, DownloadedAt) VALUES (@UrlHash, @RemoteUrl, @LocalPath, @LocalUrl, @DownloadedAt)",
            new { UrlHash = urlHash, RemoteUrl = remoteUrl, LocalPath = localPath, LocalUrl = localUrl, DownloadedAt = DateTime.UtcNow.ToString("O") });
    }

    public string? GetLocalImageUrl(string urlHash)
    {
        using var conn = new SqliteConnection(ConnectionString);
        return conn.QueryFirstOrDefault<string>("SELECT LocalUrl FROM LocalImages WHERE UrlHash = @UrlHash", new { UrlHash = urlHash });
    }

    public int GetImageServerPort()
    {
        var val = GetSetting("ImageServerPort");
        return int.TryParse(val, out var port) ? port : 8900;
    }

    public void SetImageServerPort(int port)
    {
        SetSetting("ImageServerPort", port.ToString());
    }

    public string GetImagePublicHost()
    {
        return GetSetting("ImagePublicHost") ?? "localhost";
    }

    public void SetImagePublicHost(string host)
    {
        SetSetting("ImagePublicHost", host);
    }

    // SyncState
    public string? GetSyncState(string key)
    {
        using var conn = new SqliteConnection(ConnectionString);
        return conn.QueryFirstOrDefault<string>("SELECT Value FROM SyncState WHERE Key = @Key", new { Key = key });
    }

    public void SetSyncState(string key, string value)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Execute("INSERT OR REPLACE INTO SyncState (Key, Value) VALUES (@Key, @Value)", new { Key = key, Value = value });
    }
}

public class ForwardLogEntry
{
    public int Id { get; set; }
    public long TopicId { get; set; }
    public long GroupId { get; set; }
    public string GroupName { get; set; } = "";
    public string Author { get; set; } = "";
    public string ContentPreview { get; set; } = "";
    public string ForwarderType { get; set; } = "";
    public string WebhookUrl { get; set; } = "";
    public string Status { get; set; } = "Success";
    public string? ErrorMessage { get; set; }
    public string ForwardedAt { get; set; } = "";
}

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

    public void Init()
    {
        Directory.CreateDirectory(DbDir);

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS Groups (
                GroupId INTEGER PRIMARY KEY,
                Name TEXT NOT NULL,
                Url TEXT NOT NULL
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
        ");
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
        conn.Execute("INSERT OR REPLACE INTO Groups (GroupId, Name, Url) VALUES (@GroupId, @Name, @Url)", group);
    }

    public void RemoveGroup(long groupId)
    {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Execute("DELETE FROM Groups WHERE GroupId = @GroupId", new { GroupId = groupId });
        conn.Execute("DELETE FROM ForwardRules WHERE GroupId = @GroupId", new { GroupId = groupId });
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

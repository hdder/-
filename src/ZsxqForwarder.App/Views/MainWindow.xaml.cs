using System.IO;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using Serilog;
using ZsxqForwarder.App.Services;
using ZsxqForwarder.Core.Models;
using ZsxqForwarder.Core.Services;
using ZsxqForwarder.Forwarders;

namespace ZsxqForwarder.App.Views;

public partial class MainWindow : Window
{
    private readonly TopicService _topicService;
    private readonly ExportService _exportService;
    private readonly MonitorService _monitorService;
    private readonly ForwardService _forwardService;
    private readonly AuthService _authService;
    private readonly DatabaseService _db;
    private readonly BrowserService _browser;

    private ObservableCollection<GroupConfig> _groups = [];
    private ObservableCollection<TopicDisplay> _topics = [];
    private ObservableCollection<ForwardLogEntry> _forwardLogs = [];
    private List<Topic> _rawTopics = [];
    private GroupConfig? _selectedGroup;
    private long? _lastEndTime;
    private bool _hasMoreTopics = true;

    public MainWindow(string accessToken, DatabaseService db)
    {
        InitializeComponent();

        _db = db;
        _authService = new AuthService();
        _authService.SaveToken(accessToken);

        // Setup browser service (WebView2 for API calls, avoids SSL issues)
        _browser = new BrowserService();
        _topicService = new TopicService(_browser.FetchJsonAsync);
        _exportService = new ExportService(_topicService);

        // Setup forwarders from DB
        _forwardService = new ForwardService();
        _forwardService.SetDatabase(_db);
        ApplyForwarderSettings();

        // Setup monitor
        _monitorService = new MonitorService(_topicService, _forwardService);
        _monitorService.IntervalSeconds = _db.GetMonitorInterval();
        _monitorService.NewTopicDetected += OnNewTopic;
        _monitorService.ErrorOccurred += OnMonitorError;

        // Load groups from DB
        RefreshGroupList();
        RefreshForwardLogs();

        // Init browser and enrich group names
        _ = InitBrowserAndEnrichAsync(accessToken);
    }

    private void ApplyForwarderSettings()
    {
        var rules = _db.GetForwardRules();
        _forwardService.SetRules(rules);
        _forwardService.SetForwarderFactory(rule => rule.ForwarderType switch
        {
            "DingTalk" => CreateDingTalk(rule),
            "Feishu" => CreateFeishu(rule),
            _ => null
        });
    }

    private static IForwarder CreateDingTalk(ForwardRule rule)
    {
        var f = new DingTalkForwarder { IsEnabled = true };
        f.Configure(rule.WebhookUrl, rule.Secret);
        return f;
    }

    private static IForwarder CreateFeishu(ForwardRule rule)
    {
        var f = new FeishuForwarder { IsEnabled = true };
        f.Configure(rule.WebhookUrl);
        return f;
    }

    private async Task InitBrowserAndEnrichAsync(string accessToken)
    {
        try
        {
            await _browser.InitWithTokenAsync(accessToken);
            await EnrichGroupNamesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to init browser service");
        }
    }

    private void RefreshGroupList()
    {
        _groups = new ObservableCollection<GroupConfig>(_db.GetGroups());
        GroupList.ItemsSource = _groups;
    }

    private void RefreshForwardLogs()
    {
        _forwardLogs = new ObservableCollection<ForwardLogEntry>(_db.GetForwardLogs(50));
        ForwardLogList.ItemsSource = _forwardLogs;
    }

    private async Task EnrichGroupNamesAsync()
    {
        try
        {
            var apiGroups = await _topicService.GetGroupsAsync();
            var changed = false;
            foreach (var g in _groups)
            {
                var match = apiGroups.FirstOrDefault(ag => ag.GroupId == g.GroupId);
                if (match != null && g.Name != match.Name)
                {
                    g.Name = match.Name;
                    _db.SaveGroup(g);
                    changed = true;
                }
            }
            if (changed)
            {
                RefreshGroupList();
            }
        }
        catch { }
    }

    // Group management
    private void OnAddGroup(object sender, RoutedEventArgs e)
    {
        var input = GroupUrlInput.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(input))
        {
            MessageBox.Show("请输入星球URL或ID", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var groupId = ParseGroupIdFromUrl(input);
        if (groupId == null)
        {
            MessageBox.Show("无法解析星球ID", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_groups.Any(g => g.GroupId == groupId.Value))
        {
            MessageBox.Show("该星球已添加", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var groupConfig = new GroupConfig
        {
            GroupId = groupId.Value,
            Name = $"星球 {groupId.Value}",
            Url = input.Contains("/") ? input : $"https://wx.zsxq.com/group/{groupId.Value}"
        };

        _db.SaveGroup(groupConfig);
        RefreshGroupList();
        GroupUrlInput.Text = "";

        _ = EnrichGroupNamesAsync();
    }

    private void OnRemoveGroup(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not long groupId) return;

        _db.RemoveGroup(groupId);
        RefreshGroupList();
    }

    private void OnGroupSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupConfig group) return;

        _selectedGroup = group;
        _lastEndTime = null;
        _hasMoreTopics = true;
        _rawTopics.Clear();
        _topics.Clear();

        TopicTitle.Text = group.Name;
        TopicCount.Text = "";
        BtnLoadMore.Visibility = Visibility.Collapsed;
        TopicsLoading.Visibility = Visibility.Visible;

        _ = LoadTopicsAsync();
    }

    private async Task LoadTopicsAsync()
    {
        if (_selectedGroup == null) return;

        try
        {
            var url = $"https://api.zsxq.com/v2/groups/{_selectedGroup.GroupId}/topics?count=20&scope=all";
            if (_lastEndTime.HasValue)
                url += $"&end_time={_lastEndTime.Value}";

            var json = await _browser.FetchJsonAsync(url);
            var result = Newtonsoft.Json.JsonConvert.DeserializeObject<ZsxqForwarder.Core.Models.ApiResponse<ZsxqForwarder.Core.Models.TopicsRespData>>(json);
            if (result?.Succeeded != true || result.RespData == null)
                throw new Exception(result?.Error ?? "Failed to load topics");

            var topics = result.RespData.Topics;
            _hasMoreTopics = !result.RespData.IsEnd;

            foreach (var topic in topics)
            {
                _rawTopics.Add(topic);
                _topics.Add(new TopicDisplay(topic));
            }

            TopicList.ItemsSource = _topics;
            TopicCount.Text = $"({_topics.Count} 条)";

            if (_rawTopics.Count > 0)
            {
                _lastEndTime = _rawTopics.Min(t => t.CreateTime);
            }

            BtnLoadMore.Visibility = _hasMoreTopics ? Visibility.Visible : Visibility.Collapsed;
            TopicsLoading.Visibility = Visibility.Collapsed;

            SyncTime.Text = $"最后同步: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load topics");
            TopicsLoading.Visibility = Visibility.Collapsed;
            MessageBox.Show($"加载帖子失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnLoadMoreClick(object sender, RoutedEventArgs e)
    {
        BtnLoadMore.IsEnabled = false;
        await LoadTopicsAsync();
        BtnLoadMore.IsEnabled = true;
    }

    private async void OnTopicSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TopicList.SelectedItem is not TopicDisplay display) return;

        var topic = _rawTopics.FirstOrDefault(t => t.TopicId == display.TopicId);
        if (topic == null) return;

        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailAuthor.Visibility = Visibility.Visible;
        DetailText.Visibility = Visibility.Visible;
        DetailStats.Visibility = Visibility.Visible;

        var author = topic.Talk?.Owner?.Name
                     ?? topic.Task?.Owner?.Name
                     ?? topic.Question?.Owner?.Name
                     ?? "Unknown";
        DetailAuthorName.Text = author;
        DetailTime.Text = topic.CreatedAt.ToString("yyyy-MM-dd HH:mm");

        var text = topic.Talk?.Text ?? topic.Task?.Text ?? topic.Question?.Text ?? "";
        DetailText.Text = text;

        var images = topic.Talk?.Images?.Select(i => i.Large?.Url ?? i.Url).ToList();
        if (images?.Count > 0)
        {
            DetailImages.ItemsSource = images;
            DetailImages.Visibility = Visibility.Visible;
        }
        else
        {
            DetailImages.Visibility = Visibility.Collapsed;
        }

        DetailLikes.Text = topic.LikesCount.ToString();
        DetailComments.Text = topic.CommentsCount.ToString();

        if (topic.CommentsCount > 0)
        {
            try
            {
                var comments = await _topicService.GetCommentsAsync(topic.TopicId);
                CommentsHeader.Visibility = Visibility.Visible;
                CommentsList.Visibility = Visibility.Visible;
                CommentsList.ItemsSource = comments;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load comments for topic {Id}", topic.TopicId);
            }
        }
        else
        {
            CommentsHeader.Visibility = Visibility.Collapsed;
            CommentsList.Visibility = Visibility.Collapsed;
        }
    }

    // Resend (补发)
    private async void OnResendClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not int logId) return;

        var log = _forwardLogs.FirstOrDefault(l => l.Id == logId);
        if (log == null) return;

        var topic = _db.GetTopic(log.TopicId);
        if (topic == null)
        {
            MessageBox.Show("帖子数据不存在", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var rule = new ForwardRule
        {
            ForwarderType = log.ForwarderType,
            WebhookUrl = log.WebhookUrl
        };

        var forwarder = rule.ForwarderType switch
        {
            "DingTalk" => CreateDingTalk(rule) as IForwarder,
            "Feishu" => CreateFeishu(rule),
            _ => null
        };

        if (forwarder == null) return;

        try
        {
            await forwarder.ForwardAsync(topic);

            _db.AddForwardLog(new ForwardLogEntry
            {
                TopicId = topic.TopicId,
                GroupId = log.GroupId,
                GroupName = log.GroupName,
                Author = log.Author,
                ContentPreview = log.ContentPreview,
                ForwarderType = log.ForwarderType,
                WebhookUrl = log.WebhookUrl,
                Status = "Success",
                ForwardedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });

            RefreshForwardLogs();
            MessageBox.Show("补发成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"补发失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Monitor
    private async void OnMonitorToggle(object sender, RoutedEventArgs e)
    {
        if (_monitorService.IsRunning)
        {
            _monitorService.Stop();
            BtnMonitor.Content = "开始监控";
            MonitorDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(189, 189, 189));
            MonitorStatus.Text = "监控已停止";
        }
        else
        {
            var groupIds = _db.GetGroups().Select(g => g.GroupId).ToList();
            if (groupIds.Count == 0)
            {
                MessageBox.Show("请先添加星球", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await _monitorService.StartAsync(groupIds);

            BtnMonitor.Content = "停止监控";
            MonitorDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Colors.Green);
            MonitorStatus.Text = $"监控中 ({groupIds.Count} 个星球)";
        }
    }

    private void OnNewTopic(object? sender, NewTopicEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var topic = e.Topic;
            var groupName = _groups.FirstOrDefault(g => g.GroupId == e.GroupId)?.Name ?? "Unknown";

            if (WindowState == WindowState.Minimized)
                FlashWindowEx(this);

            if (_selectedGroup?.GroupId == e.GroupId)
            {
                _topics.Insert(0, new TopicDisplay(topic));
                _rawTopics.Insert(0, topic);
                TopicCount.Text = $"({_topics.Count} 条)";
            }

            SyncTime.Text = $"新帖: {topic.Talk?.Owner?.Name} @ {groupName} - {DateTime.Now:HH:mm:ss}";
            RefreshForwardLogs();
        });
    }

    private void OnMonitorError(object? sender, MonitorErrorEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            MonitorStatus.Text = $"监控错误: {e.Error}";
        });
    }

    // Settings
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_db, _authService);
        settingsWindow.Owner = this;
        if (settingsWindow.ShowDialog() == true)
        {
            ApplyForwarderSettings();
            _monitorService.IntervalSeconds = _db.GetMonitorInterval();
            RefreshGroupList();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _monitorService.IsRunning)
            Hide();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool FlashWindow(IntPtr hWnd, bool bInvert);

    private static void FlashWindowEx(Window window)
    {
        FlashWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle, true);
    }

    protected override void OnClosed(EventArgs e)
    {
        _monitorService.Dispose();
        _browser.Dispose();
        base.OnClosed(e);
    }

    private static long? ParseGroupIdFromUrl(string url)
    {
        var match = Regex.Match(url, @"group/(\d+)");
        if (match.Success && long.TryParse(match.Groups[1].Value, out var id))
            return id;
        if (long.TryParse(url.Trim(), out var rawId))
            return rawId;
        return null;
    }
}

public class TopicDisplay
{
    public long TopicId { get; }
    public string Type { get; }
    public string DisplayText { get; }
    public string AuthorName { get; }
    public int LikesCount { get; }
    public int CommentsCount { get; }
    public bool Digested { get; }
    public DateTime CreatedAt { get; }

    public TopicDisplay(Topic topic)
    {
        TopicId = topic.TopicId;
        Type = topic.Type;
        var text = topic.Talk?.Text ?? topic.Task?.Text ?? topic.Question?.Text ?? "";
        DisplayText = text.Length > 150 ? text[..150] + "..." : text;
        AuthorName = topic.Talk?.Owner?.Name ?? topic.Task?.Owner?.Name ?? "Unknown";
        LikesCount = topic.LikesCount;
        CommentsCount = topic.CommentsCount;
        Digested = topic.Digested;
        CreatedAt = topic.CreatedAt;
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Serilog;
using ZsxqForwarder.Core.Api;
using ZsxqForwarder.Core.Models;
using ZsxqForwarder.Core.Services;
using ZsxqForwarder.Forwarders;

namespace ZsxqForwarder.App.Views;

public partial class MainWindow : Window
{
    private readonly ZsxqApiClient _apiClient;
    private readonly TopicService _topicService;
    private readonly ExportService _exportService;
    private readonly MonitorService _monitorService;
    private readonly ForwardService _forwardService;
    private readonly AuthService _authService;

    private ObservableCollection<Group> _groups = [];
    private ObservableCollection<TopicDisplay> _topics = [];
    private List<Topic> _rawTopics = [];
    private Group? _selectedGroup;
    private long? _lastEndTime;
    private bool _hasMoreTopics = true;
    private CancellationTokenSource? _exportCts;
    private CancellationTokenSource? _monitorCts;

    public MainWindow(string accessToken)
    {
        InitializeComponent();

        _apiClient = new ZsxqApiClient();
        _apiClient.SetAccessToken(accessToken);
        _topicService = new TopicService(_apiClient);
        _exportService = new ExportService(_topicService);
        _forwardService = new ForwardService();
        _monitorService = new MonitorService(_topicService, _forwardService);
        _authService = new AuthService();
        _authService.SaveToken(accessToken);

        // Setup forwarders
        _forwardService.RegisterForwarder(new TelegramForwarder());
        _forwardService.RegisterForwarder(new WechatForwarder());
        _forwardService.RegisterForwarder(new FeishuForwarder());

        // Setup monitor events
        _monitorService.NewTopicDetected += OnNewTopic;
        _monitorService.ErrorOccurred += OnMonitorError;

        _ = LoadGroupsAsync();

        // Set default export dir
        ExportDir.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ZsxqExport");
    }

    private async Task LoadGroupsAsync()
    {
        try
        {
            var groups = await _topicService.GetGroupsAsync();
            _groups = new ObservableCollection<Group>(groups);
            GroupList.ItemsSource = _groups;

            // Also populate export combo
            ExportGroupCombo.ItemsSource = _groups;
            if (_groups.Count > 0)
                ExportGroupCombo.SelectedIndex = 0;

            Log.Information("Loaded {Count} groups", groups.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load groups");
            MessageBox.Show($"加载星球列表失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnGroupSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is not Group group) return;

        _selectedGroup = group;
        _lastEndTime = null;
        _hasMoreTopics = true;
        _rawTopics.Clear();
        _topics.Clear();

        TopicTitle.Text = group.Name;
        TopicCount.Text = "";
        BtnLoadMore.Visibility = Visibility.Collapsed;
        TopicsLoading.Visibility = Visibility.Visible;

        await LoadTopicsAsync();
    }

    private async Task LoadTopicsAsync()
    {
        if (_selectedGroup == null) return;

        try
        {
            var (topics, isEnd) = await _apiClient.GetTopicsAsync(
                _selectedGroup.GroupId, count: 20, endTime: _lastEndTime);

            _hasMoreTopics = !isEnd;

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

        // Show detail
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

        // Images
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

        // Stats
        DetailLikes.Text = topic.LikesCount.ToString();
        DetailComments.Text = topic.CommentsCount.ToString();

        // Load comments
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

    // Export
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        ExportOverlay.Visibility = Visibility.Visible;
    }

    private void OnExportCancel(object sender, RoutedEventArgs e)
    {
        _exportCts?.Cancel();
        ExportOverlay.Visibility = Visibility.Collapsed;
        ExportProgress.Visibility = Visibility.Collapsed;
        ExportProgressText.Visibility = Visibility.Collapsed;
    }

    private void OnBrowseExportDir(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择导出目录"
        };

        if (dialog.ShowDialog() == true)
        {
            ExportDir.Text = dialog.FolderName;
        }
    }

    private async void OnStartExport(object sender, RoutedEventArgs e)
    {
        if (ExportGroupCombo.SelectedItem is not Group group)
        {
            MessageBox.Show("请选择要导出的星球", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dir = ExportDir.Text;
        if (string.IsNullOrEmpty(dir))
        {
            MessageBox.Show("请选择导出目录", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(dir);

        ExportProgress.Visibility = Visibility.Visible;
        ExportProgressText.Visibility = Visibility.Visible;
        BtnStartExport.IsEnabled = false;

        _exportCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<ExportProgress>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    ExportProgress.Value = p.Percent;
                    ExportProgressText.Text = $"{p.CurrentFile} ({p.Processed}/{p.Total})";
                });
            });

            await _exportService.ExportToMarkdownAsync(
                group.GroupId, group.Name, dir, progress, _exportCts.Token);

            MessageBox.Show("导出完成！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ExportProgressText.Text = "导出已取消";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Export failed");
            MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnStartExport.IsEnabled = true;
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
            if (_groups.Count == 0)
            {
                MessageBox.Show("请先加载星球列表", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var groupIds = _groups.Select(g => g.GroupId).ToList();
            await _monitorService.StartAsync(groupIds);
            _monitorService.IntervalSeconds = 30;

            BtnMonitor.Content = "停止监控";
            MonitorDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Colors.Green);
            MonitorStatus.Text = $"监控中 ({_groups.Count} 个星球)";
        }
    }

    private void OnNewTopic(object? sender, NewTopicEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var topic = e.Topic;
            var groupName = _groups.FirstOrDefault(g => g.GroupId == e.GroupId)?.Name ?? "Unknown";

            // Show balloon notification
            if (WindowState == WindowState.Minimized)
            {
                FlashWindowEx(this);
            }

            // Add to topic list if viewing the same group
            if (_selectedGroup?.GroupId == e.GroupId)
            {
                _topics.Insert(0, new TopicDisplay(topic));
                _rawTopics.Insert(0, topic);
                TopicCount.Text = $"({_topics.Count} 条)";
            }

            SyncTime.Text = $"新帖: {topic.Talk?.Owner?.Name} @ {groupName} - {DateTime.Now:HH:mm:ss}";
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
        var settingsWindow = new SettingsWindow(
            _forwardService, _monitorService, _authService);
        settingsWindow.Owner = this;
        settingsWindow.ShowDialog();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _monitorService.IsRunning)
        {
            Hide();
            // NotifyIcon would be set up here for system tray
        }
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
        _exportCts?.Cancel();
        base.OnClosed(e);
    }
}

// Display wrapper for Topic
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

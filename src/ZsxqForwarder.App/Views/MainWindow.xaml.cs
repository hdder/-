using System.Collections.ObjectModel;
using System.Windows;
using Newtonsoft.Json;
using Serilog;
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
    private readonly SyncService _syncService;
    private readonly Microsoft.Web.WebView2.Wpf.WebView2 _webView;

    private ObservableCollection<GroupConfig> _groups = [];
    private ObservableCollection<TopicDisplay> _topics = [];
    private ObservableCollection<ForwardLogEntry> _forwardLogs = [];
    private List<Topic> _rawTopics = [];
    private GroupConfig? _selectedGroup;
    private int _topicOffset;

    public MainWindow(string accessToken, DatabaseService db, Microsoft.Web.WebView2.Wpf.WebView2 webView)
    {
        InitializeComponent();

        _db = db;
        _webView = webView;
        _authService = new AuthService();
        _authService.SaveToken(accessToken);

        _topicService = new TopicService(FetchJsonAsync);
        _exportService = new ExportService(_topicService);

        _forwardService = new ForwardService();
        _forwardService.SetDatabase(_db);
        ApplyForwarderSettings();

        _monitorService = new MonitorService(_topicService, _forwardService);
        _monitorService.IntervalSeconds = _db.GetMonitorInterval();
        _monitorService.NewTopicDetected += OnNewTopic;
        _monitorService.ErrorOccurred += OnMonitorError;

        _syncService = new SyncService(_topicService, _db);
        _syncService.ProgressChanged += OnSyncProgress;
        _syncService.SyncCompleted += OnSyncCompleted;
        _syncService.SyncError += OnSyncError;

        RefreshGroupList();
        RefreshForwardLogs();

        _ = RunInitialSyncAsync();
    }

    /// <summary>
    /// Hybrid fetch: JS fetch in WebView2 context (wx.zsxq.com).
    /// For dynamics API, hooks page fetch to capture signature headers.
    /// </summary>
    private async Task<string> FetchJsonAsync(string url)
    {
        if (_webView.CoreWebView2 == null)
            throw new InvalidOperationException("WebView2 not initialized");

        // For dynamics API, use intercepted headers from the page's own API calls
        if (url.Contains("/v2/dynamics"))
            return await FetchDynamicsAsync(url);

        return await FetchSimpleAsync(url);
    }

    /// <summary>
    /// Simple JS fetch for APIs that work without signature headers.
    /// </summary>
    private async Task<string> FetchSimpleAsync(string url)
    {
        var escapedUrl = url.Replace("'", "\\'");
        var js = $@"
            (async () => {{
                try {{
                    const resp = await fetch('{escapedUrl}', {{
                        credentials: 'include',
                        headers: {{ 'Accept': 'application/json' }}
                    }});
                    if (!resp.ok) return JSON.stringify({{ error: 'HTTP ' + resp.status }});
                    return await resp.text();
                }} catch(e) {{
                    return JSON.stringify({{ error: e.message }});
                }}
            }})()";

        var result = await _webView.CoreWebView2.ExecuteScriptAsync(js);
        if (string.IsNullOrEmpty(result))
            throw new Exception("Empty response from WebView2");

        var text = result.StartsWith("\"")
            ? JsonConvert.DeserializeObject<string>(result) ?? result
            : result;

        if (text.Contains("\"error\"") && !text.Contains("\"succeeded\""))
        {
            Log.Warning("JS fetch failed for {Url}: {Text}", url, text);
            throw new Exception("Fetch error: " + text);
        }

        return text;
    }

    /// <summary>
    /// Fetch dynamics API by hooking into the page's own fetch to capture signature headers.
    /// Strategy:
    /// 1. Install fetch hook to capture headers from the page's own api.zsxq.com calls
    /// 2. Navigate to wx.zsxq.com to trigger the page's dynamics load
    /// 3. Use captured headers (with updated timestamp) for our own requests
    /// 4. Fall back to __NEXT_DATA__ DOM extraction if hook fails
    /// </summary>
    private async Task<string> FetchDynamicsAsync(string url)
    {
        // Step 1: Ensure fetch hook is installed to capture signature headers
        await InstallFetchHookAsync();

        // Step 2: If we already have captured headers, use them directly
        var hasHeaders = await EvaluateBoolAsync("window.__zsxqHeaders != null");
        if (hasHeaders)
        {
            var text = await FetchWithCapturedHeadersAsync(url);
            if (text != null) return text;
        }

        // Step 3: Navigate to wx.zsxq.com to trigger the page's own API call
        Log.Information("Navigating to wx.zsxq.com to capture API headers");
        await NavigateAndWaitAsync("https://wx.zsxq.com/");

        // Step 4: Check if hook captured headers
        hasHeaders = await EvaluateBoolAsync("window.__zsxqHeaders != null");
        if (hasHeaders)
        {
            var text = await FetchWithCapturedHeadersAsync(url);
            if (text != null) return text;
        }

        // Step 5: Fallback - extract from __NEXT_DATA__
        Log.Information("Falling back to __NEXT_DATA__ extraction");
        var data = await ExtractNextDataAsync();
        if (data != null) return data;

        throw new Exception("无法获取 dynamics 数据：签名头捕获失败且 __NEXT_DATA__ 无数据");
    }

    private async Task InstallFetchHookAsync()
    {
        await _webView.CoreWebView2.ExecuteScriptAsync(@"
            if (!window.__zsxqHookInstalled) {
                window.__zsxqHookInstalled = true;
                window.__zsxqHeaders = null;

                const origFetch = window.fetch;
                window.fetch = async function(...args) {
                    const url = typeof args[0] === 'string' ? args[0] : args[0]?.url;
                    const opts = args[1] || {};
                    const headers = opts.headers || {};

                    // Capture headers from any api.zsxq.com request
                    if (url && url.includes('api.zsxq.com')) {
                        try {
                            const h = headers instanceof Headers
                                ? Object.fromEntries(headers.entries())
                                : (typeof headers === 'object' ? {...headers} : {});
                            if (Object.keys(h).length > 1) {
                                window.__zsxqHeaders = h;
                            }
                        } catch(e) {}
                    }

                    return origFetch.apply(this, args);
                };
            }
        ");
    }

    private async Task<string?> FetchWithCapturedHeadersAsync(string url)
    {
        var escapedUrl = url.Replace("'", "\\'").Replace("\"", "\\\"");
        var js = $@"
            (async () => {{
                try {{
                    const h = window.__zsxqHeaders || {{}};
                    // Update timestamp for current request
                    const ts = Math.floor(Date.now() / 1000).toString();
                    const headers = {{...h, 'Accept': 'application/json'}};
                    if (h['x-timestamp']) headers['x-timestamp'] = ts;

                    const resp = await fetch('{escapedUrl}', {{
                        credentials: 'include',
                        headers: headers
                    }});
                    if (!resp.ok) return JSON.stringify({{ error: 'HTTP ' + resp.status }});
                    const text = await resp.text();
                    if (text.includes('""succeeded""')) return text;
                    return JSON.stringify({{ error: 'API error', detail: text.substring(0, 200) }});
                }} catch(e) {{
                    return JSON.stringify({{ error: e.message }});
                }}
            }})()";

        var result = await _webView.CoreWebView2.ExecuteScriptAsync(js);
        if (string.IsNullOrEmpty(result)) return null;

        var text = result.StartsWith("\"")
            ? JsonConvert.DeserializeObject<string>(result) ?? result
            : result;

        if (text.Contains("\"succeeded\"")) return text;

        Log.Warning("FetchWithCapturedHeaders failed: {Text}", text);
        return null;
    }

    private async Task<string?> ExtractNextDataAsync()
    {
        var js = @"
            (() => {
                try {
                    // Try __NEXT_DATA__ script tag
                    const el = document.getElementById('__NEXT_DATA__');
                    if (el && el.textContent) {
                        const data = JSON.parse(el.textContent);
                        // Look for dynamics in page props
                        const props = data?.props?.pageProps;
                        if (props) return JSON.stringify({ succeeded: true, resp_data: { dynamics: props.dynamics || props.feedList || [], is_end: true } });
                    }

                    // Try window.__NEXT_DATA__
                    const wnd = window.__NEXT_DATA__;
                    if (wnd?.props?.pageProps) {
                        const props = wnd.props.pageProps;
                        const dynamics = props.dynamics || props.feedList || props.list || [];
                        if (dynamics.length > 0) {
                            return JSON.stringify({ succeeded: true, resp_data: { dynamics: dynamics, is_end: true } });
                        }
                    }
                } catch(e) {}
                return null;
            })()";

        var result = await _webView.CoreWebView2.ExecuteScriptAsync(js);
        if (string.IsNullOrEmpty(result) || result == "null") return null;

        var text = result.StartsWith("\"")
            ? JsonConvert.DeserializeObject<string>(result) ?? result
            : result;

        if (text.Contains("\"succeeded\"")) return text;
        return null;
    }

    private async Task NavigateAndWaitAsync(string url)
    {
        var tcs = new TaskCompletionSource<bool>();
        void handler(object? s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            tcs.TrySetResult(e.IsSuccess);
        }
        _webView.CoreWebView2.NavigationCompleted += handler;
        _webView.CoreWebView2.Navigate(url);

        var success = await tcs.Task;
        _webView.CoreWebView2.NavigationCompleted -= handler;

        if (success)
            await Task.Delay(2000); // Wait for JS to execute and API calls to complete
    }

    private async Task<bool> EvaluateBoolAsync(string expression)
    {
        var result = await _webView.CoreWebView2.ExecuteScriptAsync(expression);
        return result?.Trim().ToLower() == "true";
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

    // Groups
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

    private void OnGroupSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupConfig group) return;

        _selectedGroup = group;
        _topicOffset = 0;
        TopicDatePicker.SelectedDate = null;

        TopicTitle.Text = group.Name;
        LoadTopicsFromDb();
    }

    private void LoadTopicsFromDb()
    {
        if (_selectedGroup == null) return;

        var pageSize = 50;
        var topics = _db.GetTopicsByGroup(_selectedGroup.GroupId, pageSize, _topicOffset);

        _rawTopics.Clear();
        _topics.Clear();

        foreach (var topic in topics)
        {
            _rawTopics.Add(topic);
            _topics.Add(new TopicDisplay(topic));
        }

        TopicList.ItemsSource = _topics;
        var total = _db.GetTopicCountByGroup(_selectedGroup.GroupId);
        TopicCount.Text = $"({_topics.Count} / {total} 条)";

        BtnLoadMore.Visibility = topics.Count >= pageSize ? Visibility.Visible : Visibility.Collapsed;
        TopicsLoading.Visibility = Visibility.Collapsed;
    }

    // Date filter
    private void OnDateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_selectedGroup == null || TopicDatePicker.SelectedDate == null) return;

        var date = TopicDatePicker.SelectedDate.Value;
        var topics = _db.GetTopicsByGroupAndDate(_selectedGroup.GroupId, date);

        _topics.Clear();
        _rawTopics.Clear();
        foreach (var topic in topics)
        {
            _rawTopics.Add(topic);
            _topics.Add(new TopicDisplay(topic));
        }
        TopicList.ItemsSource = _topics;
        TopicCount.Text = $"({_topics.Count} 条)";
        BtnLoadMore.Visibility = Visibility.Collapsed;
    }

    private void OnClearDateFilter(object sender, RoutedEventArgs e)
    {
        TopicDatePicker.SelectedDate = null;
        _topicOffset = 0;
        if (_selectedGroup != null)
            LoadTopicsFromDb();
    }

    private async void OnLoadMoreClick(object sender, RoutedEventArgs e)
    {
        if (_selectedGroup == null) return;
        BtnLoadMore.IsEnabled = false;

        _topicOffset += 50;
        var topics = _db.GetTopicsByGroup(_selectedGroup.GroupId, 50, _topicOffset);

        foreach (var topic in topics)
        {
            _rawTopics.Add(topic);
            _topics.Add(new TopicDisplay(topic));
        }

        var total = _db.GetTopicCountByGroup(_selectedGroup.GroupId);
        TopicCount.Text = $"({_topics.Count} / {total} 条)";

        BtnLoadMore.Visibility = topics.Count >= 50 ? Visibility.Visible : Visibility.Collapsed;
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

    // Sync
    private async Task RunInitialSyncAsync()
    {
        var initialDone = _db.GetSyncState("initial_sync_done");
        if (initialDone == "true")
        {
            await _syncService.IncrementalSyncAsync();
            Dispatcher.Invoke(RefreshGroupList);
        }
        else
        {
            Dispatcher.Invoke(() => SyncPanel.Visibility = Visibility.Visible);
            await _syncService.FullSyncAsync();
        }
    }

    private void OnSyncProgress(object? sender, SyncProgressEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            SyncPanel.Visibility = Visibility.Visible;
            SyncProgress.IsIndeterminate = !e.IsComplete;
            SyncStatusText.Text = e.Phase == "fetching"
                ? $"正在获取数据... ({e.Loaded} 条)"
                : $"已存储 {e.Loaded} 条动态";
        });
    }

    private void OnSyncCompleted(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            SyncPanel.Visibility = Visibility.Collapsed;
            SyncTime.Text = $"最后同步: {DateTime.Now:HH:mm:ss}";
            RefreshGroupList();
        });
    }

    private void OnSyncError(object? sender, string error)
    {
        Dispatcher.Invoke(() =>
        {
            SyncPanel.Visibility = Visibility.Collapsed;
            SyncStatusText.Text = $"同步失败: {error}";
            SyncPanel.Visibility = Visibility.Visible;
        });
    }

    private async void OnSyncClick(object sender, RoutedEventArgs e)
    {
        BtnSync.IsEnabled = false;
        SyncPanel.Visibility = Visibility.Visible;
        try
        {
            await _syncService.FullSyncAsync();
        }
        finally
        {
            BtnSync.IsEnabled = true;
        }
    }

    // Resend
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
                MessageBox.Show("请先同步星球数据", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        _webView.Dispose();
        base.OnClosed(e);
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

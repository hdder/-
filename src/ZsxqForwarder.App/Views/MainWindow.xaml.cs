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
    private readonly ImageHostingService _imageHosting;
    private readonly ZsxqApiService _apiService;
    private readonly RemoteLogService _remoteLog;
    private readonly Microsoft.Web.WebView2.Wpf.WebView2 _webView;

    private List<long> _monitoredGroupIds = [];

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

        // Load config for API service
        var settingsService = new SettingsService();
        var appSettings = settingsService.Load();
        var apiConfig = appSettings.Api;

        _apiService = new ZsxqApiService(apiConfig.MinIntervalMs);
        _apiService.Secret = apiConfig.Secret;
        _apiService.BaseUrl = apiConfig.BaseUrl;
        _apiService.AppVersion = apiConfig.AppVersion;
        _apiService.Platform = apiConfig.Platform;

        // Remote logger
        RemoteLogService? remoteLog = null;
        if (appSettings.RemoteLog.Enabled)
        {
            try
            {
                remoteLog = new RemoteLogService(appSettings.RemoteLog.ServerUrl, appSettings.RemoteLog.ApiToken);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to initialize remote logger");
            }
        }
        _remoteLog = remoteLog ?? new RemoteLogService("http://localhost:1", "disabled");
        _apiService.SetRemoteLogger(_remoteLog);

        _forwardService = new ForwardService();
        _forwardService.SetDatabase(_db);
        _imageHosting = new ImageHostingService(_db, DatabaseService.GetImagesDir(), _db.GetImageServerPort(), _db.GetImagePublicHost());
        _imageHosting.Start();
        _forwardService.SetImageHosting(_imageHosting);
        ApplyForwarderSettings();

        _monitorService = new MonitorService(_forwardService, _db, ScrapeGroupPageAsync, _apiService);
        _monitorService.IntervalSeconds = _db.GetMonitorInterval();
        _monitorService.NewTopicDetected += OnNewTopic;
        _monitorService.ErrorOccurred += OnMonitorError;

        _syncService = new SyncService(_topicService, _db);
        _syncService.ProgressChanged += OnSyncProgress;
        _syncService.SyncCompleted += OnSyncCompleted;
        _syncService.SyncError += OnSyncError;

        RefreshGroupList();
        RefreshForwardLogs();
    }

    /// <summary>
    /// Fetch JSON from zsxq API or DOM.
    /// Tries API first (fast, structured), falls back to DOM scraping.
    /// </summary>
    private async Task<string> FetchJsonAsync(string url)
    {
        if (_webView.CoreWebView2 == null)
            throw new InvalidOperationException("WebView2 not initialized");

        // Ensure API service has fresh cookies
        if (!_apiService.HasCookies)
        {
            try
            {
                var cookieManager = _webView.CoreWebView2.CookieManager;
                var cookies = await cookieManager.GetCookiesAsync("https://api.zsxq.com");
                var cookieStr = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                if (!string.IsNullOrEmpty(cookieStr))
                    _apiService.InitCookies(cookieStr);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to extract cookies from WebView2 for API auth");
            }
        }

        // Try API first for known endpoints
        if (url.Contains("/v2/groups") || url.Contains("/v1/groups"))
        {
            var apiResult = await TryFetchGroupsFromApiAsync();
            if (apiResult != null) return apiResult;
            Log.Warning("API fetch for groups failed, falling back to DOM");
        }

        if (url.Contains("/v2/groups/") && url.Contains("/topics"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(url, @"/groups/(\d+)/topics");
            if (match.Success)
            {
                var groupId = long.Parse(match.Groups[1].Value);
                var apiResult = await TryFetchTopicsFromApiAsync(groupId);
                if (apiResult != null) return apiResult;
                Log.Warning("API fetch for topics (group {GroupId}) failed, falling back to DOM", groupId);
            }
        }

        if (url.Contains("/v2/dynamics"))
        {
            // Try API-based sync: fetch topics per monitored group
            var apiResult = await TryFetchDynamicsFromApiAsync();
            if (apiResult != null) return apiResult;
            Log.Warning("API fetch for dynamics failed, falling back to DOM");
            return await FetchDynamicsAsync(url);
        }

        // Fallback: simple JS fetch for other endpoints
        return await FetchSimpleAsync(url);
    }

    private async Task<string?> TryFetchGroupsFromApiAsync()
    {
        try
        {
            var result = await _apiService.GetGroupsAsync();
            if (result?.Succeeded == true && result.RespData != null)
            {
                return JsonConvert.SerializeObject(new
                {
                    succeeded = true,
                    resp_data = new
                    {
                        groups = result.RespData.Groups,
                        is_end = true
                    }
                });
            }
        }
        catch (Exception ex) { Log.Error(ex, "API get groups failed"); }
        return null;
    }

    private async Task<string?> TryFetchTopicsFromApiAsync(long groupId, int count = 20, long? endTime = null)
    {
        try
        {
            var result = await _apiService.GetTopicsAsync(groupId, count, endTime);
            if (result?.Succeeded == true && result.RespData != null)
            {
                return JsonConvert.SerializeObject(result);
            }
        }
        catch (Exception ex) { Log.Error(ex, "API get topics failed for group {GroupId}", groupId); }
        return null;
    }

    private async Task<string?> TryFetchDynamicsFromApiAsync()
    {
        // For dynamics (all groups), use API to get topics per group
        var groupIds = _monitoredGroupIds.Count > 0
            ? _monitoredGroupIds
            : _db.GetGroups().Select(g => g.GroupId).ToList();

        if (groupIds.Count == 0) return null;

        var allDynamics = new List<Dynamic>();
        var groups = _db.GetGroups();

        foreach (var groupId in groupIds)
        {
            var result = await _apiService.GetTopicsAsync(groupId, count: 20);
            if (result?.RespData == null) continue;

            var group = groups.FirstOrDefault(g => g.GroupId == groupId);
            var groupName = group?.Name ?? "";

            foreach (var topic in result.RespData.Topics)
            {
                allDynamics.Add(new Dynamic
                {
                    DynamicId = topic.TopicId,
                    Action = "create_topic",
                    CreateTimeStr = topic.CreateTime > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(topic.CreateTime).ToString("yyyy-MM-dd HH:mm")
                        : "",
                    Topic = topic,
                    Group = new DynamicGroup
                    {
                        GroupId = groupId,
                        Name = groupName,
                    }
                });
            }
        }

        if (allDynamics.Count == 0) return null;

        return JsonConvert.SerializeObject(new
        {
            succeeded = true,
            resp_data = new
            {
                dynamics = allDynamics,
                groups = groups.Select(g => new { group_id = g.GroupId, name = g.Name, avatar_url = g.AvatarUrl, background_url = g.BackgroundUrl }),
                is_end = true
            }
        });
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
    /// Extract dynamics data from the wx.zsxq.com page DOM.
    /// The page is an Angular app. Two page types:
    /// - /dynamics: has div.dynamic-topic with .from-group > .group-name
    /// - /group/{id}: has app-topic with .topic-container
    /// </summary>
    private async Task<string> FetchDynamicsAsync(string url)
    {
        Log.Information("Fetching dynamics by iterating group pages");

        // Step 1: Ensure we're on a zsxq page with sidebar
        var currentUrl = await _webView.CoreWebView2.ExecuteScriptAsync("location.href");
        var onZsxq = currentUrl != null && currentUrl.Contains("wx.zsxq.com");

        if (!onZsxq)
        {
            // Any zsxq page has the sidebar; use /dynamics as landing
            await NavigateAndWaitAsync("https://wx.zsxq.com/dynamics", 4000);
        }

        // Step 2: Extract group list from sidebar
        var groupListJson = await _webView.CoreWebView2.ExecuteScriptAsync(@"
            (function() {
                const groups = [];
                const links = document.querySelectorAll('.group-list a[href*=""/group/""]');
                for (const a of links) {
                    const name = a.querySelector('.group-name')?.textContent?.trim();
                    const href = a.getAttribute('href') || '';
                    const match = href.match(/\/group\/(\d+)/);
                    if (name && match) {
                        groups.push({name: name, group_id: parseInt(match[1]), href: href});
                    }
                }
                return JSON.stringify(groups);
            })()");

        if (string.IsNullOrEmpty(groupListJson) || groupListJson == "null" || groupListJson == "[]")
            throw new Exception("无法从侧边栏获取星球列表");

        var groupListText = groupListJson.StartsWith("\"")
            ? JsonConvert.DeserializeObject<string>(groupListJson) ?? groupListJson
            : groupListJson;

        var groupList = JsonConvert.DeserializeObject<List<GroupInfo>>(groupListText);
        if (groupList == null || groupList.Count == 0)
            throw new Exception("星球列表为空");

        Log.Information("Found {Count} groups in sidebar", groupList.Count);

        // Filter: only scrape groups with forwarding rules if monitoring
        if (_monitoredGroupIds.Count > 0)
        {
            groupList = groupList.Where(g => _monitoredGroupIds.Contains(g.group_id)).ToList();
            Log.Information("Filtered to {Count} monitored groups", groupList.Count);
        }

        // Step 3: Check if current page is already a group page — extract from it first
        var allDynamics = new List<object>();
        var allGroups = new List<object>();
        var currentPath = await _webView.CoreWebView2.ExecuteScriptAsync("location.pathname");
        var currentPathClean = currentPath?.Trim('"') ?? "";

        foreach (var group in groupList)
        {
            allGroups.Add(new { group_id = group.group_id, name = group.name, avatar_url = "", background_url = "" });

            // If already on this group's page, extract without navigating
            if (currentPathClean == group.href)
            {
                Log.Information("Scraping group (current page): {Name} ({Id})", group.name, group.group_id);
                var pageJson = await ExtractTopicsFromCurrentPageAsync(group.group_id, group.name);
                if (pageJson != null)
                {
                    var pageData = JsonConvert.DeserializeObject<PageExtractResult>(pageJson);
                    if (pageData?.dynamics != null)
                        allDynamics.AddRange(pageData.dynamics);
                    Log.Information("  Extracted {Count} topics from {Name}",
                        pageData?.dynamics?.Count ?? 0, group.name);
                }
                continue;
            }

            // Navigate to group page
            Log.Information("Scraping group: {Name} ({Id})", group.name, group.group_id);
            var groupUrl = $"https://wx.zsxq.com{group.href}";
            var waitMs = _monitoredGroupIds.Count > 0 ? 2000 : 4000;
            await NavigateAndWaitAsync(groupUrl, waitMs);

            var pageJson2 = await ExtractTopicsFromCurrentPageAsync(group.group_id, group.name);
            if (pageJson2 != null)
            {
                var pageData2 = JsonConvert.DeserializeObject<PageExtractResult>(pageJson2);
                if (pageData2?.dynamics != null)
                    allDynamics.AddRange(pageData2.dynamics);
                Log.Information("  Extracted {Count} topics from {Name}",
                    pageData2?.dynamics?.Count ?? 0, group.name);
            }
        }

        if (allDynamics.Count == 0)
            throw new Exception("所有星球页面均未提取到动态数据");

        var result = new
        {
            succeeded = true,
            resp_data = new
            {
                dynamics = allDynamics,
                groups = allGroups,
                is_end = true
            }
        };

        var json = JsonConvert.SerializeObject(result);
        Log.Information("Total extracted {Count} dynamics from {Groups} groups", allDynamics.Count, groupList.Count);
        return json;
    }

    /// <summary>
    /// Monitor callback: navigate to a single group page, extract topics.
    /// </summary>
    private async Task<List<Dynamic>> ScrapeGroupPageAsync(long groupId, string groupName)
    {
        var groupUrl = $"https://wx.zsxq.com/group/{groupId}";
        await NavigateAndWaitAsync(groupUrl, 2000);

        var pageJson = await ExtractTopicsFromCurrentPageAsync(groupId, groupName);
        if (pageJson == null) return [];

        var pageData = JsonConvert.DeserializeObject<PageExtractResult>(pageJson);
        if (pageData?.dynamics == null) return [];

        // Convert raw objects back to Dynamic list
        var dynamics = new List<Dynamic>();
        foreach (var obj in pageData.dynamics)
        {
            var serialized = JsonConvert.SerializeObject(obj);
            var d = JsonConvert.DeserializeObject<Dynamic>(serialized);
            if (d != null) dynamics.Add(d);
        }
        return dynamics;
    }

    private class GroupInfo
    {
        public string name { get; set; } = "";
        public long group_id { get; set; }
        public string href { get; set; } = "";
    }

    private class PageExtractResult
    {
        public List<object>? dynamics { get; set; }
    }

    private async Task NavigateAndWaitAsync(string url, int waitMs = 3000)
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
            await Task.Delay(waitMs);
    }

    private async Task ReloadAndWaitAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        void handler(object? s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            tcs.TrySetResult(e.IsSuccess);
        }
        _webView.CoreWebView2.NavigationCompleted += handler;
        _webView.CoreWebView2.Reload();

        var success = await tcs.Task;
        _webView.CoreWebView2.NavigationCompleted -= handler;

        if (success)
            await Task.Delay(3000);
    }

    private async Task<string?> ExtractTopicsFromCurrentPageAsync(long groupId, string groupName)
    {
        var js = $@"
            (() => {{
                const dynamics = [];
                let topicIndex = 0;

                // Convert date string to unix milliseconds
                function dateToMs(dateStr) {{
                    const trimmed = (dateStr || '').trim();
                    const m = trimmed.match(/(\d{{4}})-(\d{{2}})-(\d{{2}})\s+(\d{{2}}):(\d{{2}})/);
                    if (!m) return 0;
                    const d = new Date(parseInt(m[1]), parseInt(m[2])-1, parseInt(m[3]), parseInt(m[4]), parseInt(m[5]));
                    return d.getTime();
                }}

                // Generate stable topic_id from content hash (same content = same ID)
                function makeTopicId(groupId, dateText, text) {{
                    const ms = dateToMs(dateText);
                    const raw = groupId + '|' + dateText + '|' + (text || '').substring(0, 100);
                    let hash = 0;
                    for (let i = 0; i < raw.length; i++) {{
                        hash = ((hash << 5) - hash + raw.charCodeAt(i)) | 0;
                    }}
                    return Math.abs(hash) + ms;
                }}

                // Extract from <app-topic type=""flow""> elements
                const appTopics = document.querySelectorAll('app-topic[type=""flow""]');
                for (const at of appTopics) {{
                    const item = extractAppTopic(at);
                    if (item) dynamics.push(item);
                }}

                // Fallback - extract from .topic-container (legacy)
                if (dynamics.length === 0) {{
                    const topicContainers = document.querySelectorAll('.topic-container');
                    for (const tc of topicContainers) {{
                        const item = extractTopicContainer(tc);
                        if (item) dynamics.push(item);
                    }}
                }}

                if (dynamics.length === 0) return null;

                return JSON.stringify({{ dynamics: dynamics }});

                function extractAppTopic(el) {{
                    const header = el.querySelector('app-topic-header');
                    if (!header) return null;

                    const avatar = header.querySelector('.avatar')?.src || '';
                    const authorName = header.querySelector('.role')?.textContent?.trim() || '';
                    const dateText = header.querySelector('.date')?.textContent?.trim() || '';
                    const isDigest = !!header.querySelector('.digest');

                    // Content: may be hidden (display:none) for image-only posts
                    const contentEl = el.querySelector('.talk-content-container .content');
                    let content = '';
                    if (contentEl) {{
                        const wasHidden = contentEl.style.display === 'none';
                        if (wasHidden) contentEl.style.display = 'block';
                        content = contentEl.innerText?.trim() || '';
                        if (wasHidden) contentEl.style.display = 'none';
                    }}

                    // Extract images from app-image-gallery
                    const images = [];
                    const imgEls = el.querySelectorAll('app-image-gallery img.item');
                    for (const img of imgEls) {{
                        if (img.src) images.push(img.src);
                    }}

                    // Extract links from content
                    const links = [];
                    const linkEls = el.querySelectorAll('.talk-content-container .content a.link-of-topic');
                    for (const a of linkEls) {{
                        links.push({{text: a.textContent?.trim() || '', href: a.href || ''}});
                    }}

                    if (!content && images.length === 0 && !authorName) return null;

                    let fullText = content;
                    if (images.length > 0) {{
                        fullText += (fullText ? '\n' : '') + images.map(u => '[图片]').join('\n');
                    }}
                    if (links.length > 0) {{
                        fullText += (fullText ? '\n' : '') + links.map(l => l.text + ': ' + l.href).join('\n');
                    }}

                    return {{
                        dynamic_id: 0,
                        action: 'create_topic',
                        create_time: dateText,
                        topic: {{
                            topic_id: makeTopicId({groupId}, dateText, fullText),
                            type: 'talk',
                            create_time: dateToMs(dateText),
                            group: {{group_id: {groupId}, name: '{groupName.Replace("'", "\\'")}'}},
                            talk: {{
                                owner: {{name: authorName, avatar_url: avatar}},
                                text: fullText,
                                images: images.map(u => ({{url: u, large: {{url: u}}, original: {{url: u}}}}))
                            }},
                            likes_count: 0,
                            comments_count: 0,
                            is_digest: isDigest
                        }},
                        group: {{
                            group_id: {groupId},
                            name: '{groupName.Replace("'", "\\'")}',
                            avatar_url: '',
                            background_url: ''
                        }},
                        images: images,
                        links: links
                    }};
                }}

                function extractTopicContainer(el) {{
                    const avatar = el.querySelector('.header-container .avatar')?.src || '';
                    const authorName = el.querySelector('.header-container .role')?.textContent?.trim() || '';
                    const dateText = el.querySelector('.header-container .date')?.textContent?.trim() || '';
                    const content = el.querySelector('.talk-content-container .content')?.innerText?.trim() || '';

                    const images = [];
                    const imgEls = el.querySelectorAll('.image-container img, .topic-image img');
                    for (const img of imgEls) {{
                        if (img.src) images.push(img.src);
                    }}

                    let fullText = content;
                    if (images.length > 0) {{
                        fullText += (fullText ? '\n' : '') + images.map(u => '[图片]').join('\n');
                    }}

                    if (!content && !authorName && images.length === 0) return null;

                    return {{
                        dynamic_id: 0,
                        action: 'create_topic',
                        create_time: dateText,
                        topic: {{
                            topic_id: makeTopicId({groupId}, dateText, fullText),
                            type: 'talk',
                            create_time: dateToMs(dateText),
                            group: {{group_id: {groupId}, name: '{groupName.Replace("'", "\\'")}'}},
                            talk: {{
                                owner: {{name: authorName, avatar_url: avatar}},
                                text: fullText,
                                images: images.map(u => ({{url: u, large: {{url: u}}, original: {{url: u}}}}))
                            }},
                            likes_count: 0,
                            comments_count: 0
                        }},
                        group: {{
                            group_id: {groupId},
                            name: '{groupName.Replace("'", "\\'")}',
                            avatar_url: '',
                            background_url: ''
                        }}
                    }};
                }}
            }})()";

        var result = await _webView.CoreWebView2.ExecuteScriptAsync(js);
        if (string.IsNullOrEmpty(result) || result.Trim() == "null")
            return null;

        var text = result.StartsWith("\"")
            ? JsonConvert.DeserializeObject<string>(result) ?? result
            : result;

        if (text.Contains("\"dynamics\"")) return text;
        return null;
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

    // Resend from topic list
    private async void OnTopicResendClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not long topicId) return;
        if (_selectedGroup == null) return;

        // Check if forwarding rules exist for this group
        var rules = _db.GetForwardRules()
            .Where(r => r.GroupId == _selectedGroup.GroupId && r.Enabled && !string.IsNullOrEmpty(r.WebhookUrl))
            .ToList();

        if (rules.Count == 0)
        {
            MessageBox.Show(
                $"补发失败：没有为「{_selectedGroup.Name}」配置转发规则。\n请在设置中添加 Webhook 地址（钉钉/飞书等）。",
                "补发失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var topic = _db.GetTopic(topicId);
        if (topic == null)
        {
            MessageBox.Show("帖子数据不存在", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Disable button during send
        btn.IsEnabled = false;
        btn.Content = "发送中...";

        try
        {
            await _forwardService.ForwardAsync(topic, _selectedGroup.GroupId, _selectedGroup.Name);
            RefreshForwardLogs();

            // Check latest forward log to determine result
            var latestLogs = _db.GetForwardLogsByGroup(_selectedGroup.GroupId, 5);
            var latestForTopic = latestLogs.FirstOrDefault(l => l.TopicId == topicId);
            if (latestForTopic != null && latestForTopic.Status == "Success")
            {
                MessageBox.Show(
                    $"补发成功！已通过 {latestForTopic.ForwarderType} 发送到 {latestForTopic.WebhookUrl}",
                    "补发成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (latestForTopic != null)
            {
                MessageBox.Show(
                    $"补发失败：{latestForTopic.ErrorMessage}\n转发方式：{latestForTopic.ForwarderType}\n地址：{latestForTopic.WebhookUrl}",
                    "补发失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show("补发已完成，请查看转发日志确认结果。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"补发失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btn.IsEnabled = true;
            btn.Content = "补发";
        }
    }

    // Resend from forward log
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
            _monitoredGroupIds.Clear();
            BtnMonitor.Content = "开始监控";
            MonitorDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(189, 189, 189));
            MonitorStatus.Text = "监控已停止";
        }
        else
        {
            var ruleGroupIds = _db.GetForwardRules()
                .Where(r => r.Enabled && !string.IsNullOrEmpty(r.WebhookUrl))
                .Select(r => r.GroupId)
                .Distinct()
                .ToList();

            if (ruleGroupIds.Count == 0)
            {
                MessageBox.Show("没有配置转发规则，请先在设置中添加 Webhook 地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await _monitorService.StartAsync(ruleGroupIds);
            _monitoredGroupIds = ruleGroupIds;

            BtnMonitor.Content = "停止监控";
            MonitorDot.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Colors.Green);
            MonitorStatus.Text = $"监控中 ({ruleGroupIds.Count} 个星球)";
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
        _imageHosting.Dispose();
        _webView.Dispose();
        _remoteLog.LogInfoAsync("App closed", $"Last API failures: {_apiService.ConsecutiveFailures}").Wait();
        base.OnClosed(e);
    }
}

public class TopicDisplay
{
    public long TopicId { get; }
    public string Type { get; }
    public string DisplayText { get; }
    public string AuthorName { get; }
    public int LikesCount { get; set; }
    public int CommentsCount { get; set; }
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

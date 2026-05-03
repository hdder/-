using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using ZsxqForwarder.Core.Models;
using ZsxqForwarder.Core.Services;

namespace ZsxqForwarder.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly AuthService _authService;

    public SettingsWindow(SettingsService settingsService, AuthService authService)
    {
        InitializeComponent();

        _settingsService = settingsService;
        _authService = authService;

        LoadSettings();
        MonitorInterval.ValueChanged += (s, e) =>
            IntervalDisplay.Text = $"{(int)MonitorInterval.Value} 秒";
    }

    private void LoadSettings()
    {
        var s = _settingsService.Settings;

        // Groups
        GroupConfigList.ItemsSource = s.Groups.ToList();
        RuleGroupCombo.ItemsSource = s.Groups.ToList();

        // Forward rules
        ForwardRuleList.ItemsSource = s.ForwardRules.ToList();

        // Monitor
        MonitorInterval.Value = s.Monitor.IntervalSeconds;

        // Account
        LoginStatus.Text = _authService.IsLoggedIn ? "已登录" : "未登录";
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
            MessageBox.Show("无法解析星球ID，请输入正确的URL或ID", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_settingsService.Settings.Groups.Any(g => g.GroupId == groupId.Value))
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

        _settingsService.Settings.Groups.Add(groupConfig);
        _settingsService.Save();
        RefreshLists();
        GroupUrlInput.Text = "";
    }

    private void OnRemoveGroup(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not long groupId) return;

        var group = _settingsService.Settings.Groups.FirstOrDefault(g => g.GroupId == groupId);
        if (group != null)
        {
            _settingsService.Settings.Groups.Remove(group);
            // Also remove related rules
            _settingsService.Settings.ForwardRules.RemoveAll(r => r.GroupId == groupId);
            _settingsService.Save();
            RefreshLists();
        }
    }

    // Forward rules
    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        if (RuleGroupCombo.SelectedItem is not GroupConfig group)
        {
            MessageBox.Show("请选择星球", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var forwarderItem = RuleForwarderType.SelectedItem as ComboBoxItem;
        var forwarderType = forwarderItem?.Tag?.ToString();
        if (string.IsNullOrEmpty(forwarderType))
        {
            MessageBox.Show("请选择转发类型", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var webhook = RuleWebhookUrl.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(webhook))
        {
            MessageBox.Show("请输入Webhook URL", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var rule = new ForwardRule
        {
            GroupId = group.GroupId,
            GroupName = group.Name,
            ForwarderType = forwarderType,
            WebhookUrl = webhook,
            Secret = RuleSecret.Text?.Trim() ?? "",
            Enabled = true
        };

        _settingsService.Settings.ForwardRules.Add(rule);
        _settingsService.Save();
        RefreshLists();

        RuleWebhookUrl.Text = "";
        RuleSecret.Text = "";
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not long groupId) return;

        // Find the rule - since Tag is GroupId, remove the first matching rule for this group
        var rule = _settingsService.Settings.ForwardRules.FirstOrDefault(r => r.GroupId == groupId);
        if (rule != null)
        {
            _settingsService.Settings.ForwardRules.Remove(rule);
            _settingsService.Save();
            RefreshLists();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = _settingsService.Settings;
        s.Monitor.IntervalSeconds = (int)MonitorInterval.Value;

        _settingsService.Save();
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnLogout(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("确定要退出登录吗？", "确认",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _authService.Logout();
            LoginStatus.Text = "已退出登录";
            MessageBox.Show("已退出登录，请重新启动应用", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Application.Current.Shutdown();
        }
    }

    private void RefreshLists()
    {
        GroupConfigList.ItemsSource = _settingsService.Settings.Groups.ToList();
        RuleGroupCombo.ItemsSource = _settingsService.Settings.Groups.ToList();
        ForwardRuleList.ItemsSource = _settingsService.Settings.ForwardRules.ToList();
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

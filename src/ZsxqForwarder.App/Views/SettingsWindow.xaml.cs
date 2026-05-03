using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using ZsxqForwarder.Core.Models;
using ZsxqForwarder.Core.Services;

namespace ZsxqForwarder.App.Views;

public partial class SettingsWindow : Window
{
    private readonly DatabaseService _db;
    private readonly AuthService _authService;

    public SettingsWindow(DatabaseService db, AuthService authService)
    {
        InitializeComponent();

        _db = db;
        _authService = authService;

        LoadSettings();
        MonitorInterval.ValueChanged += (s, e) =>
            IntervalDisplay.Text = $"{(int)MonitorInterval.Value} 秒";
    }

    private void LoadSettings()
    {
        GroupConfigList.ItemsSource = _db.GetGroups();
        RuleGroupCombo.ItemsSource = _db.GetGroups();
        ForwardRuleList.ItemsSource = _db.GetForwardRules();
        MonitorInterval.Value = _db.GetMonitorInterval();
        LoginStatus.Text = _authService.IsLoggedIn ? "已登录" : "未登录";
    }

    private void RefreshLists()
    {
        GroupConfigList.ItemsSource = _db.GetGroups();
        RuleGroupCombo.ItemsSource = _db.GetGroups();
        ForwardRuleList.ItemsSource = _db.GetForwardRules();
    }

    // Groups
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

        var groups = _db.GetGroups();
        if (groups.Any(g => g.GroupId == groupId.Value))
        {
            MessageBox.Show("该星球已添加", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _db.SaveGroup(new GroupConfig
        {
            GroupId = groupId.Value,
            Name = $"星球 {groupId.Value}",
            Url = input.Contains("/") ? input : $"https://wx.zsxq.com/group/{groupId.Value}"
        });

        RefreshLists();
        GroupUrlInput.Text = "";
    }

    private void OnRemoveGroup(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not long groupId) return;

        _db.RemoveGroup(groupId);
        RefreshLists();
    }

    // Forward Rules
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

        _db.SaveForwardRule(new ForwardRule
        {
            GroupId = group.GroupId,
            GroupName = group.Name,
            ForwarderType = forwarderType,
            WebhookUrl = webhook,
            Secret = RuleSecret.Text?.Trim() ?? "",
            Enabled = true
        });

        RefreshLists();
        RuleWebhookUrl.Text = "";
        RuleSecret.Text = "";
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        // Use a different approach: get the ForwardRule from DataContext
        if (sender is not Button btn) return;
        if (btn.DataContext is not ForwardRule rule) return;

        _db.RemoveForwardRule(rule.Id);
        RefreshLists();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _db.SetMonitorInterval((int)MonitorInterval.Value);
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

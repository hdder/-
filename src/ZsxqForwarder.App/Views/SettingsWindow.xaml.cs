using System.Text.RegularExpressions;
using System.Windows;
using ZsxqForwarder.Core.Models;
using ZsxqForwarder.Core.Services;
using ZsxqForwarder.Forwarders;

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

        // Forwarders
        DingTalkEnabled.IsChecked = s.DingTalk.Enabled;
        DingTalkWebhook.Text = s.DingTalk.WebhookUrl;
        DingTalkSecret.Text = s.DingTalk.Secret;

        FeishuEnabled.IsChecked = s.Feishu.Enabled;
        FeishuWebhook.Text = s.Feishu.WebhookUrl;

        TelegramEnabled.IsChecked = s.Telegram.Enabled;
        TelegramBotToken.Text = s.Telegram.BotToken;
        TelegramChatId.Text = s.Telegram.ChatId;

        WechatEnabled.IsChecked = s.Wechat.Enabled;
        WechatWebhook.Text = s.Wechat.WebhookUrl;

        // Monitor
        MonitorInterval.Value = s.Monitor.IntervalSeconds;

        // Account
        LoginStatus.Text = _authService.IsLoggedIn ? "已登录" : "未登录";
    }

    private void OnAddGroup(object sender, RoutedEventArgs e)
    {
        var input = GroupUrlInput.Text.Trim();
        if (string.IsNullOrEmpty(input)) return;

        var groupId = ParseGroupIdFromUrl(input);
        if (groupId == null)
        {
            MessageBox.Show("无法解析星球ID，请输入正确的URL或ID", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Check duplicate
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
        GroupConfigList.ItemsSource = _settingsService.Settings.Groups.ToList();

        GroupUrlInput.Text = "";
    }

    private void OnRemoveGroup(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not long groupId) return;

        var group = _settingsService.Settings.Groups.FirstOrDefault(g => g.GroupId == groupId);
        if (group != null)
        {
            _settingsService.Settings.Groups.Remove(group);
            _settingsService.Save();
            GroupConfigList.ItemsSource = _settingsService.Settings.Groups.ToList();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = _settingsService.Settings;

        // Forwarders
        s.DingTalk.Enabled = DingTalkEnabled.IsChecked == true;
        s.DingTalk.WebhookUrl = DingTalkWebhook.Text.Trim();
        s.DingTalk.Secret = DingTalkSecret.Text.Trim();

        s.Feishu.Enabled = FeishuEnabled.IsChecked == true;
        s.Feishu.WebhookUrl = FeishuWebhook.Text.Trim();

        s.Telegram.Enabled = TelegramEnabled.IsChecked == true;
        s.Telegram.BotToken = TelegramBotToken.Text.Trim();
        s.Telegram.ChatId = TelegramChatId.Text.Trim();

        s.Wechat.Enabled = WechatEnabled.IsChecked == true;
        s.Wechat.WebhookUrl = WechatWebhook.Text.Trim();

        // Monitor
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

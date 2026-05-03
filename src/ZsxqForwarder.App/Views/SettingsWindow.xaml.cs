using System.Windows;
using ZsxqForwarder.Core.Services;
using ZsxqForwarder.Forwarders;

namespace ZsxqForwarder.App.Views;

public partial class SettingsWindow : Window
{
    private readonly ForwardService _forwardService;
    private readonly MonitorService _monitorService;
    private readonly AuthService _authService;

    public SettingsWindow(ForwardService forwardService, MonitorService monitorService, AuthService authService)
    {
        InitializeComponent();

        _forwardService = forwardService;
        _monitorService = monitorService;
        _authService = authService;

        LoadSettings();
        MonitorInterval.ValueChanged += (s, e) =>
            IntervalDisplay.Text = $"{(int)MonitorInterval.Value} 秒";
    }

    private void LoadSettings()
    {
        // Load forwarder settings
        var telegram = _forwardService.Forwarders.OfType<TelegramForwarder>().FirstOrDefault();
        if (telegram != null)
        {
            TelegramEnabled.IsChecked = telegram.IsEnabled;
        }

        var wechat = _forwardService.Forwarders.OfType<WechatForwarder>().FirstOrDefault();
        if (wechat != null)
        {
            WechatEnabled.IsChecked = wechat.IsEnabled;
        }

        var feishu = _forwardService.Forwarders.OfType<FeishuForwarder>().FirstOrDefault();
        if (feishu != null)
        {
            FeishuEnabled.IsChecked = feishu.IsEnabled;
        }

        MonitorInterval.Value = _monitorService.IntervalSeconds;
        LoginStatus.Text = _authService.IsLoggedIn ? "已登录" : "未登录";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // Apply forwarder settings
        var telegram = _forwardService.Forwarders.OfType<TelegramForwarder>().FirstOrDefault();
        if (telegram != null)
        {
            telegram.IsEnabled = TelegramEnabled.IsChecked == true;
            telegram.Configure(TelegramBotToken.Text, TelegramChatId.Text);
        }

        var wechat = _forwardService.Forwarders.OfType<WechatForwarder>().FirstOrDefault();
        if (wechat != null)
        {
            wechat.IsEnabled = WechatEnabled.IsChecked == true;
            wechat.Configure(WechatWebhook.Text);
        }

        var feishu = _forwardService.Forwarders.OfType<FeishuForwarder>().FirstOrDefault();
        if (feishu != null)
        {
            feishu.IsEnabled = FeishuEnabled.IsChecked == true;
            feishu.Configure(FeishuWebhook.Text);
        }

        _monitorService.IntervalSeconds = (int)MonitorInterval.Value;

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
}

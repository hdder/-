using System.Windows;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace ZsxqForwarder.App.Views;

public partial class LoginWindow : Window
{
    public bool LoginSucceeded { get; private set; }
    public string? AccessToken { get; private set; }
    public Microsoft.Web.WebView2.Wpf.WebView2 WebView => LoginWebView;

    public LoginWindow()
    {
        InitializeComponent();
        LoginWebView.NavigationCompleted += OnNavigationCompleted;
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            await CheckLoginStatusAsync();
        }
    }

    private async Task CheckLoginStatusAsync()
    {
        try
        {
            if (await TryGetTokenAsync())
                return;

            Dispatcher.Invoke(() =>
            {
                StatusDot.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(255, 183, 77));
                StatusText.Text = "请使用微信扫码登录...";
            });

            _ = PollLoginStatusAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking login status");
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = "检测登录状态出错，请刷新重试";
            });
        }
    }

    private async Task<bool> TryGetTokenAsync()
    {
        var cookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://api.zsxq.com");
        var tokenCookie = cookies.FirstOrDefault(c => c.Name == "zsxq_access_token");

        if (tokenCookie == null || string.IsNullOrEmpty(tokenCookie.Value))
        {
            var wxCookies = await LoginWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://wx.zsxq.com");
            tokenCookie = wxCookies.FirstOrDefault(c => c.Name == "zsxq_access_token");
        }

        if (tokenCookie != null && !string.IsNullOrEmpty(tokenCookie.Value))
        {
            AccessToken = tokenCookie.Value;
            LoginSucceeded = true;

            Dispatcher.Invoke(() =>
            {
                StatusDot.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Colors.Green);
                StatusText.Text = "登录成功！";
            });

            await Task.Delay(800);
            Dispatcher.Invoke(Close);
            return true;
        }

        return false;
    }

    private async Task PollLoginStatusAsync()
    {
        while (!LoginSucceeded)
        {
            await Task.Delay(2000);
            try
            {
                await TryGetTokenAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error polling login status");
            }
        }
    }

    // Do NOT dispose WebView2 here — it's reused by MainWindow
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
    }
}

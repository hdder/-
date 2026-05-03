using System.Windows;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Serilog;

namespace ZsxqForwarder.App.Services;

/// <summary>
/// Uses a hidden WebView2 to fetch data from zsxq.com APIs.
/// WebView2 handles SSL/TLS and cookies natively.
/// </summary>
public class BrowserService : IDisposable
{
    private Microsoft.Web.WebView2.Wpf.WebView2? _webView;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private bool _initialized;

    public bool IsReady => _initialized;

    public async Task InitWithTokenAsync(string accessToken)
    {
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            _webView = new Microsoft.Web.WebView2.Wpf.WebView2
            {
                Visibility = Visibility.Hidden,
                Width = 0,
                Height = 0
            };

            var env = await CoreWebView2Environment.CreateAsync();
            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.CookieManager.AddOrUpdateCookie(
                _webView.CoreWebView2.CookieManager.CreateCookie(
                    "zsxq_access_token", accessToken, ".zsxq.com", "/"));

            _initialized = true;
            Log.Information("BrowserService initialized with token");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to init BrowserService");
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<string> FetchJsonAsync(string url)
    {
        if (!_initialized || _webView?.CoreWebView2 == null)
            throw new InvalidOperationException("BrowserService not initialized");

        await _fetchLock.WaitAsync();
        try
        {
            var escapedUrl = url.Replace("'", "\\'");
            var js = $@"
                (async () => {{
                    try {{
                        const resp = await fetch('{escapedUrl}', {{
                            credentials: 'include',
                            headers: {{
                                'Accept': 'application/json'
                            }}
                        }});
                        const text = await resp.text();
                        return text;
                    }} catch(e) {{
                        return JSON.stringify({{ error: e.message }});
                    }}
                }})()";

            var result = await _webView.CoreWebView2.ExecuteScriptAsync(js);

            if (string.IsNullOrEmpty(result))
                throw new Exception("Empty response from WebView2");

            if (result.StartsWith("\""))
            {
                var unescaped = JsonConvert.DeserializeObject<string>(result);
                return unescaped ?? result;
            }

            return result;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    public void Dispose()
    {
        _webView?.Dispose();
    }
}

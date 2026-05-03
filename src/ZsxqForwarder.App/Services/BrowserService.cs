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
    private bool _initialized;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task InitWithTokenAsync(string accessToken)
    {
        _webView = new Microsoft.Web.WebView2.Wpf.WebView2
        {
            Visibility = Visibility.Hidden,
            Width = 0,
            Height = 0
        };

        // Create WebView2 environment
        var env = await CoreWebView2Environment.CreateAsync();
        await _webView.EnsureCoreWebView2Async(env);

        // Set the access token cookie
        _webView.CoreWebView2.CookieManager.AddOrUpdateCookie(
            _webView.CoreWebView2.CookieManager.CreateCookie(
                "zsxq_access_token", accessToken, ".zsxq.com", "/"));

        _initialized = true;
        Log.Information("BrowserService initialized with token");
    }

    public async Task<string> FetchJsonAsync(string url)
    {
        if (_webView?.CoreWebView2 == null)
            throw new InvalidOperationException("BrowserService not initialized");

        await _lock.WaitAsync();
        try
        {
            var js = $@"
                (async () => {{
                    try {{
                        const resp = await fetch('{url}', {{
                            credentials: 'include',
                            headers: {{
                                'Accept': 'application/json',
                                'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36'
                            }}
                        }});
                        const text = await resp.text();
                        return text;
                    }} catch(e) {{
                        return JSON.stringify({{ error: e.message }});
                    }}
                }})()";

            var result = await _webView.CoreWebView2.ExecuteScriptAsync(js);

            // ExecuteScriptAsync returns JSON-wrapped string: "\"...\"" or "{...}"
            if (string.IsNullOrEmpty(result))
                throw new Exception("Empty response from WebView2");

            // The result is already a JSON string (double-encoded)
            // If it starts with ", it's a string result that needs unescaping
            if (result.StartsWith("\""))
            {
                var unescaped = JsonConvert.DeserializeObject<string>(result);
                return unescaped ?? result;
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _webView?.Dispose();
    }
}

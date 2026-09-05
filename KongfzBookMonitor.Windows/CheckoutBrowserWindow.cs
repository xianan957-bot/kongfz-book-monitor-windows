using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KongfzBookMonitor.Windows
{

/// <summary>
/// Hosts only the official confirmation-page popup requested by the website.
/// It does not run any purchase, confirmation, or payment action itself.
/// </summary>
internal sealed class CheckoutBrowserWindow : Window
{
    private readonly WebView2 _webView = new();
    private bool _isClosed;
    private bool _officialCheckoutOpened;

    public CheckoutBrowserWindow(CoreWebView2Environment environment)
    {
        Environment = environment;
        Title = "孔夫子确认页面";
        Width = 1080;
        Height = 780;
        MinWidth = 640;
        MinHeight = 480;
        Content = _webView;
        Closed += CheckoutBrowserWindow_Closed;
    }

    private CoreWebView2Environment Environment { get; }

    public CoreWebView2 CoreWebView2 => _webView.CoreWebView2
        ?? throw new InvalidOperationException("孔夫子确认页面尚未初始化");

    public event Action? OfficialCheckoutOpened;

    public async Task InitializeAsync()
    {
        await _webView.EnsureCoreWebView2Async(Environment);
        _webView.CoreWebView2.WindowCloseRequested += (_, _) => Close();
        _webView.NavigationCompleted += WebView_NavigationCompleted;
    }

    private void WebView_NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _isClosed || _officialCheckoutOpened) return;

        try
        {
            var coreWebView = _webView.CoreWebView2;
            if (coreWebView is null ||
                !Uri.TryCreate(coreWebView.Source, UriKind.Absolute, out var currentUri) ||
                !IsOfficialCheckoutUri(currentUri))
            {
                return;
            }

            _officialCheckoutOpened = true;
            OfficialCheckoutOpened?.Invoke();
        }
        catch (ObjectDisposedException)
        {
            // A site-initiated close is not a successful checkout transition.
        }
    }

    private void CheckoutBrowserWindow_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        try
        {
            _webView.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // The website can request that this popup closes itself.
        }
    }

    private static bool IsOfficialCheckoutUri(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsKongfzUrl(uri)) return false;

        // The exact buy click created this popup. A failed login or a captcha
        // route is not a checkout confirmation transition.
        return uri.Host.IndexOf("passport", StringComparison.OrdinalIgnoreCase) < 0 &&
            uri.AbsolutePath.IndexOf("login", StringComparison.OrdinalIgnoreCase) < 0 &&
            uri.AbsolutePath.IndexOf("captcha", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static bool IsKongfzUrl(Uri uri)
    {
        return string.Equals(uri.Host, "kongfz.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".kongfz.com", StringComparison.OrdinalIgnoreCase);
    }
}
}

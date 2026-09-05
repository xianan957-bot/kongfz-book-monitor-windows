using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KongfzBookMonitor.Windows
{

public sealed class ItemBrowserWindow : Window
{
    private const int AutoCheckoutDelayMilliseconds = 1200;
    private readonly CoreWebView2Environment _environment;
    private readonly Uri _itemUri;
    private readonly bool _autoCheckout;
    private readonly WebView2 _webView = new();
    private bool _checkoutAttempted;

    public ItemBrowserWindow(CoreWebView2Environment environment, string itemUrl, bool autoCheckout)
    {
        if (!Uri.TryCreate(itemUrl, UriKind.Absolute, out var itemUri) || !IsKongfzUrl(itemUri))
        {
            throw new ArgumentException("商品链接无效", nameof(itemUrl));
        }

        _environment = environment;
        _itemUri = itemUri;
        _autoCheckout = autoCheckout;

        Title = "孔夫子商品页面";
        Width = 1080;
        Height = 780;
        MinWidth = 640;
        MinHeight = 480;
        Content = _webView;

        Loaded += ItemBrowserWindow_Loaded;
        Closed += (_, _) => _webView.Dispose();
    }

    private async void ItemBrowserWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _webView.EnsureCoreWebView2Async(_environment);
            _webView.NavigationCompleted += WebView_NavigationCompleted;
            _webView.CoreWebView2.Navigate(_itemUri.AbsoluteUri);
        }
        catch (Exception error)
        {
            MessageBox.Show(
                this,
                $"无法打开孔夫子商品页面：{error.Message}",
                "孔夫子商品监控",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private async void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || !_autoCheckout || _checkoutAttempted || !IsItemDetailUrl(_webView.Source)) return;

        _checkoutAttempted = true;
        try
        {
            await Task.Delay(AutoCheckoutDelayMilliseconds);
            await _webView.ExecuteScriptAsync(@"
                (function() {
                  var button = document.querySelector('.go-buy');
                  if (!button || button.getAttribute('data-kongfz-auto-clicked') === '1') return;
                  button.setAttribute('data-kongfz-auto-clicked', '1');
                  button.click();
                })();");
        }
        catch (Exception)
        {
            // The notification remains available even when the page does not
            // expose a buy button or rejects the navigation.
        }
    }

    private static bool IsItemDetailUrl(Uri? uri)
    {
        if (uri is null || !string.Equals(uri.Host, "book.kongfz.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 &&
            long.TryParse(segments[0], out _) &&
            long.TryParse(segments[1], out _);
    }

    private static bool IsKongfzUrl(Uri uri)
    {
        return string.Equals(uri.Host, "kongfz.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".kongfz.com", StringComparison.OrdinalIgnoreCase);
    }
}
}

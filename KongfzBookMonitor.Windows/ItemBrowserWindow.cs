using System;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KongfzBookMonitor.Windows
{

/// <summary>
/// Opens a product page only when the user clicks a notification. Automatic
/// purchase attempts use the already visible monitor WebView instead.
/// </summary>
public sealed class ItemBrowserWindow : Window
{
    private readonly CoreWebView2Environment _environment;
    private readonly Uri _itemUri;
    private readonly WebView2 _webView = new();

    public ItemBrowserWindow(CoreWebView2Environment environment, string itemUrl)
    {
        if (!Uri.TryCreate(itemUrl, UriKind.Absolute, out var itemUri) || !IsKongfzUrl(itemUri))
        {
            throw new ArgumentException("商品链接无效", nameof(itemUrl));
        }

        _environment = environment;
        _itemUri = itemUri;

        Title = "孔夫子商品页面";
        Width = 1080;
        Height = 780;
        MinWidth = 640;
        MinHeight = 480;
        Content = _webView;

        Loaded += ItemBrowserWindow_Loaded;
        Closed += ItemBrowserWindow_Closed;
    }

    private async void ItemBrowserWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _webView.EnsureCoreWebView2Async(_environment);
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

    private void ItemBrowserWindow_Closed(object? sender, EventArgs e)
    {
        try
        {
            _webView.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // The WebView may have already been closed after an initialization failure.
        }
    }

    private static bool IsKongfzUrl(Uri uri)
    {
        return string.Equals(uri.Host, "kongfz.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".kongfz.com", StringComparison.OrdinalIgnoreCase);
    }
}
}

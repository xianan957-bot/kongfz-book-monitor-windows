using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KongfzBookMonitor.Windows
{

public sealed class ItemBrowserWindow : Window
{
    private const int AutoCheckoutProbeDelayMilliseconds = 250;
    private const int MaxAutoCheckoutProbeAttempts = 32;

    // Verified against the current official search-v3 result bundle: the
    // button rendered on a normal product card is .buy-button.item-button
    // with the exact visible text "立即购买".
    private const string ClickSearchResultBuyScript = @"
        (function() {
          function label(node) {
            return ((node && (node.innerText || node.textContent || node.value)) || '').replace(/\s+/g, ' ').trim();
          }
          function visible(node) {
            return !!node && node.getClientRects().length > 0 && getComputedStyle(node).visibility !== 'hidden';
          }
          function normalizeUrl(raw) {
            try {
              var url = new URL(raw, location.href);
              url.hash = '';
              url.search = '';
              return url.href.replace(/\/+$/, '');
            } catch (_) {
              return '';
            }
          }

          var targetUrl = __TARGET_ITEM_URL__;
          var target = normalizeUrl(targetUrl);
          if (!target) return false;

          var cards = Array.prototype.slice.call(document.querySelectorAll(
            '.product-item-wrap, .product-item-wihout-img-wrap'));
          var card = cards.find(function(node) {
            var link = node.querySelector('.item-name .item-link, .item-name a, .row-name, a.item-link');
            return link && normalizeUrl(link.getAttribute('href')) === target;
          });
          if (!card) return false;

          var button = card.querySelector('.buy-button.item-button');
          if (!visible(button) || label(button) !== '立即购买') {
            button = Array.prototype.slice.call(card.querySelectorAll(
              '.item-button, a, button, input[type=button], input[type=submit], div'))
              .find(function(node) { return visible(node) && label(node) === '立即购买'; });
          }
          if (!button) return false;
          if (button.getAttribute('data-kongfz-auto-clicked') === '1') return true;

          button.setAttribute('data-kongfz-auto-clicked', '1');
          button.click();
          return true;
        })();";

    private const string ClickItemDetailBuyScript = @"
        (function() {
          function label(node) {
            return ((node && (node.innerText || node.textContent || node.value)) || '').replace(/\s+/g, ' ').trim();
          }
          function visible(node) {
            return !!node && node.getClientRects().length > 0 && getComputedStyle(node).visibility !== 'hidden';
          }

          var button = document.querySelector('.go-buy');
          if (!visible(button) || label(button) !== '立即购买') {
            button = Array.prototype.slice.call(document.querySelectorAll(
              '.go-buy, .buy-button, a, button, input[type=button], input[type=submit], [role=button]'))
              .find(function(node) { return visible(node) && label(node) === '立即购买'; });
          }
          if (!button) return false;
          if (button.getAttribute('data-kongfz-auto-clicked') === '1') return true;

          button.setAttribute('data-kongfz-auto-clicked', '1');
          button.click();
          return true;
        })();";

    private readonly CoreWebView2Environment _environment;
    private readonly Uri _itemUri;
    private readonly Uri? _searchUri;
    private readonly bool _autoCheckout;
    private readonly WebView2 _webView = new();
    private bool _searchResultAttempted;
    private bool _checkoutClickIssued;

    public ItemBrowserWindow(CoreWebView2Environment environment, string itemUrl, bool autoCheckout)
        : this(environment, itemUrl, searchUrl: null, autoCheckout)
    {
    }

    public ItemBrowserWindow(
        CoreWebView2Environment environment,
        string itemUrl,
        string? searchUrl,
        bool autoCheckout)
    {
        if (!Uri.TryCreate(itemUrl, UriKind.Absolute, out var itemUri) || !IsKongfzUrl(itemUri))
        {
            throw new ArgumentException("商品链接无效", nameof(itemUrl));
        }

        if (!string.IsNullOrWhiteSpace(searchUrl) &&
            (!Uri.TryCreate(searchUrl, UriKind.Absolute, out var searchUri) || !IsSearchResultUrl(searchUri)))
        {
            throw new ArgumentException("商品搜索链接无效", nameof(searchUrl));
        }

        _environment = environment;
        _itemUri = itemUri;
        _searchUri = string.IsNullOrWhiteSpace(searchUrl) ? null : new Uri(searchUrl);
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

            var initialUri = _autoCheckout && _searchUri is not null ? _searchUri : _itemUri;
            _webView.CoreWebView2.Navigate(initialUri.AbsoluteUri);
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
        if (!e.IsSuccess || !_autoCheckout || _checkoutClickIssued || _webView.CoreWebView2 is null) return;
        if (!Uri.TryCreate(_webView.CoreWebView2.Source, UriKind.Absolute, out var currentUri)) return;

        if (_searchUri is not null && IsSearchResultUrl(currentUri) && !_searchResultAttempted)
        {
            _searchResultAttempted = true;
            _checkoutClickIssued = await TryClickOfficialButtonAsync(
                ClickSearchResultBuyScript.Replace(
                    "__TARGET_ITEM_URL__",
                    JsonSerializer.Serialize(_itemUri.AbsoluteUri)));

            if (!_checkoutClickIssued && _webView.CoreWebView2 is not null)
            {
                // Keep the existing detail-page route only as a fallback when
                // the official result-card button is not present.
                _webView.CoreWebView2.Navigate(_itemUri.AbsoluteUri);
            }
            return;
        }

        if (IsItemDetailUrl(currentUri))
        {
            _checkoutClickIssued = await TryClickOfficialButtonAsync(ClickItemDetailBuyScript);
        }
    }

    private async Task<bool> TryClickOfficialButtonAsync(string script)
    {
        for (var attempt = 0; attempt < MaxAutoCheckoutProbeAttempts; attempt += 1)
        {
            await Task.Delay(AutoCheckoutProbeDelayMilliseconds);
            if (_webView.CoreWebView2 is null) return false;

            try
            {
                var response = await _webView.ExecuteScriptAsync(script);
                if (string.Equals(response, "true", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsSearchResultUrl(Uri uri)
    {
        return string.Equals(uri.Host, "search.kongfz.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/product", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsItemDetailUrl(Uri uri)
    {
        if (!string.Equals(uri.Host, "book.kongfz.com", StringComparison.OrdinalIgnoreCase))
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

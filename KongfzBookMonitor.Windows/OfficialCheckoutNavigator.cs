using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KongfzBookMonitor.Windows
{

/// <summary>
/// Clicks the official “立即购买” button in the monitor's existing search
/// WebView. It only hosts the website-created confirmation popup; it never
/// confirms an order or triggers payment.
/// </summary>
internal sealed class OfficialCheckoutNavigator : IDisposable
{
    private const int ProbeDelayMilliseconds = 250;
    private const int MaxProbeAttempts = 8;
    private const int CheckoutOpenTimeoutMilliseconds = 15_000;

    // Verified against the current official search result bundle: a normal
    // product card uses .product-item-wrap and its buy control uses
    // .buy-button.item-button with the exact label “立即购买”.
    private const string ClickCurrentSearchResultBuyScript = @"
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

          var target = normalizeUrl(__TARGET_ITEM_URL__);
          if (!target) return false;
          var cards = Array.prototype.slice.call(document.querySelectorAll('.product-item-wrap'));
          var card = cards.find(function(node) {
            var link = node.querySelector('.item-name .item-link, .item-name a, a.item-link');
            return link && normalizeUrl(link.getAttribute('href')) === target;
          });
          if (!card) return false;

          var button = card.querySelector('.buy-button.item-button');
          if (!visible(button) || label(button) !== '立即购买') {
            button = Array.prototype.slice.call(card.querySelectorAll(
              '.item-button, a, button, input[type=button], input[type=submit], div'))
              .find(function(node) { return visible(node) && label(node) === '立即购买'; });
          }
          if (!button || button.getAttribute('data-kongfz-auto-clicked') === '1') return false;

          button.setAttribute('data-kongfz-auto-clicked', '1');
          button.scrollIntoView({ block: 'center', inline: 'nearest' });
          button.click();
          return true;
        })();";

    private readonly WebView2 _webView;
    private readonly CoreWebView2Environment _environment;
    private readonly string _checkoutWindowTitle;
    private DateTime _acceptCheckoutPopupUntilUtc;
    private TaskCompletionSource<bool>? _checkoutOpened;
    private CheckoutBrowserWindow? _checkoutWindow;
    private bool _isDisposed;

    public OfficialCheckoutNavigator(
        WebView2 webView,
        CoreWebView2Environment environment,
        string? checkoutWindowTitle = null)
    {
        _webView = webView;
        _environment = environment;
        _checkoutWindowTitle = string.IsNullOrWhiteSpace(checkoutWindowTitle)
            ? "孔夫子确认页面"
            : checkoutWindowTitle;

        var coreWebView = TryGetCoreWebView()
            ?? throw new InvalidOperationException("孔夫子网页尚未初始化");
        coreWebView.NewWindowRequested += WebView_NewWindowRequested;
    }

    public event Action? OfficialCheckoutOpened;

    public async Task<bool> TryOpenForItemAsync(string itemUrl, CancellationToken cancellationToken)
    {
        if (_isDisposed || _checkoutWindow is not null) return false;
        if (!Uri.TryCreate(itemUrl, UriKind.Absolute, out var itemUri) || !IsKongfzUrl(itemUri)) return false;

        var coreWebView = TryGetCoreWebView();
        if (coreWebView is null ||
            !Uri.TryCreate(coreWebView.Source, UriKind.Absolute, out var currentUri) ||
            !IsSearchResultUrl(currentUri))
        {
            return false;
        }

        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _checkoutOpened = opened;
        try
        {
            var script = ClickCurrentSearchResultBuyScript.Replace(
                "__TARGET_ITEM_URL__",
                JsonSerializer.Serialize(itemUri.AbsoluteUri));

            for (var attempt = 0; attempt < MaxProbeAttempts; attempt += 1)
            {
                await Task.Delay(ProbeDelayMilliseconds, cancellationToken);
                coreWebView = TryGetCoreWebView();
                if (coreWebView is null) return false;

                _acceptCheckoutPopupUntilUtc = DateTime.UtcNow.AddSeconds(15);
                var response = await _webView.ExecuteScriptAsync(script);
                if (!string.Equals(response, "true", StringComparison.OrdinalIgnoreCase))
                {
                    _acceptCheckoutPopupUntilUtc = DateTime.MinValue;
                    continue;
                }

                var timeout = Task.Delay(CheckoutOpenTimeoutMilliseconds, cancellationToken);
                var completed = await Task.WhenAny(opened.Task, timeout);
                return completed == opened.Task && await opened.Task;
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            _acceptCheckoutPopupUntilUtc = DateTime.MinValue;
            if (ReferenceEquals(_checkoutOpened, opened)) _checkoutOpened = null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        var coreWebView = TryGetCoreWebView();
        _isDisposed = true;
        if (coreWebView is not null)
        {
            coreWebView.NewWindowRequested -= WebView_NewWindowRequested;
        }
    }

    private async void WebView_NewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!CanAcceptOfficialCheckoutPopup() || _checkoutWindow is not null ||
            !IsOfficialCheckoutPopupUri(e.Uri))
        {
            return;
        }

        var deferral = e.GetDeferral();
        CheckoutBrowserWindow? checkoutWindow = null;
        try
        {
            checkoutWindow = new CheckoutBrowserWindow(_environment, _checkoutWindowTitle);
            _checkoutWindow = checkoutWindow;
            checkoutWindow.OfficialCheckoutOpened += CheckoutWindow_OfficialCheckoutOpened;
            checkoutWindow.Closed += CheckoutWindow_Closed;

            // WPF WebView2 needs a shown parent window before it can create
            // CoreWebView2. The deferral holds the official popup script until
            // the same-profile target is ready.
            checkoutWindow.Show();
            await checkoutWindow.InitializeAsync();
            e.NewWindow = checkoutWindow.CoreWebView2;
            checkoutWindow.Activate();
        }
        catch (Exception)
        {
            if (ReferenceEquals(_checkoutWindow, checkoutWindow)) _checkoutWindow = null;
            checkoutWindow?.Close();
            e.Handled = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void CheckoutWindow_OfficialCheckoutOpened()
    {
        _checkoutOpened?.TrySetResult(true);
        OfficialCheckoutOpened?.Invoke();
    }

    private void CheckoutWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not CheckoutBrowserWindow checkoutWindow) return;

        checkoutWindow.OfficialCheckoutOpened -= CheckoutWindow_OfficialCheckoutOpened;
        checkoutWindow.Closed -= CheckoutWindow_Closed;
        if (ReferenceEquals(_checkoutWindow, checkoutWindow)) _checkoutWindow = null;
    }

    private bool CanAcceptOfficialCheckoutPopup()
    {
        return !_isDisposed && _checkoutOpened is not null &&
            DateTime.UtcNow <= _acceptCheckoutPopupUntilUtc;
    }

    private CoreWebView2? TryGetCoreWebView()
    {
        if (_isDisposed) return null;

        try
        {
            return _webView.CoreWebView2;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static bool IsOfficialCheckoutPopupUri(string rawUri)
    {
        if (string.Equals(rawUri, "about:blank", StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(rawUri, UriKind.Absolute, out var uri) && IsKongfzUrl(uri);
    }

    private static bool IsSearchResultUrl(Uri uri)
    {
        return string.Equals(uri.Host, "search.kongfz.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/product", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKongfzUrl(Uri uri)
    {
        return string.Equals(uri.Host, "kongfz.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".kongfz.com", StringComparison.OrdinalIgnoreCase);
    }
}
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KongfzBookMonitor.Windows
{

/// <summary>
/// Loads the official Kongfz search page in WebView2 and reads the rendered
/// product cards. It does not call an undocumented purchase API.
/// </summary>
public sealed class KongfzSearchClient
{
    private const string SearchPageBaseUrl = "https://search.kongfz.com/product/";
    private const int FirstCollectionDelayMilliseconds = 1000;
    private const int CollectionRetryDelayMilliseconds = 500;
    private const int MaxCollectionAttempts = 16;

    // Verified against the official search-v3 page: item cards render title,
    // price and bibliographic metadata in these DOM nodes.
    private const string ExtractRenderedItemsScript = @"
        (function() {
          function text(node) {
            return node ? (node.innerText || node.textContent || '').replace(/\s+/g, ' ').trim() : '';
          }

          function metadataValue(node, labels) {
            var records = node.querySelectorAll('.zl-info-item');
            for (var i = 0; i < records.length; i += 1) {
              var keyNode = records[i].querySelector('span');
              var valueNode = records[i].querySelector('.zl-info-value');
              var key = text(keyNode).replace(/[：:\s]/g, '');
              if (labels.indexOf(key) >= 0) return text(valueNode);
            }
            return '';
          }

          function absoluteUrl(raw) {
            if (!raw) return '';
            try { return new URL(raw, location.href).href; } catch (_) { return ''; }
          }

          function itemIdFromUrl(raw) {
            try {
              var parts = new URL(raw, location.href).pathname.split('/').filter(Boolean);
              for (var i = parts.length - 1; i >= 0; i -= 1) {
                if (/^\d+$/.test(parts[i])) return parts[i];
              }
            } catch (_) {}
            return '';
          }

          var abnormal = document.querySelector('.abnormal-view');
          var abnormalText = text(abnormal);
          var pageText = text(document.body);
          var verificationRequired = !!document.querySelector('#captcha-button, #captcha-element') ||
            /查询类似计算机软件的自动请求|请您进行验证后再继续搜索|滑动滑块完成拼图|自动请求|验证后/.test(pageText);
          var accessLimitReached = /搜索次数已达到上限|访问频率/.test(pageText);
          var loginPage = /(^|\/)login(?:\/|\?|$)/i.test(location.pathname) ||
            /请登录后|登录后再/.test(abnormalText);
          var loading = !!document.querySelector('.produc-list-skeleton, .produc-list-text-skeleton, .product-item-skeleton');
          var nodes = Array.prototype.slice.call(document.querySelectorAll('.product-item-wrap'));
          var items = nodes.map(function(node) {
            var titleLink = node.querySelector('.item-name .item-link, .item-name a, a.item-link');
            var rawUrl = titleLink ? titleLink.getAttribute('href') : '';
            var itemUrl = absoluteUrl(rawUrl);
            var priceNode = node.querySelector('.price-info, .price-int, .row-price__value');
            return {
              itemId: itemIdFromUrl(itemUrl),
              itemUrl: itemUrl,
              title: text(titleLink || node.querySelector('.item-name')),
              author: metadataValue(node, ['作者', '著者']),
              publisher: metadataValue(node, ['出版社', '出版机构']),
              priceText: text(priceNode)
            };
          }).filter(function(item) {
            return item.itemId && item.itemUrl && item.title && item.priceText;
          });

          var state = items.length > 0 ? 'ready' :
            verificationRequired ? 'verification' :
            accessLimitReached ? 'limited' :
            loginPage ? 'login' :
            loading ? 'loading' : 'empty';
          return {
            state: state,
            message: abnormalText,
            items: items
          };
        })()";

    private readonly WebView2 _webView;

    public KongfzSearchClient(WebView2 webView)
    {
        _webView = webView;
    }

    public async Task<IReadOnlyList<KongfzItem>> FetchAsync(MonitorConfig config, CancellationToken cancellationToken)
    {
        var normalized = config.Normalize();
        if (string.IsNullOrWhiteSpace(normalized.Keyword)) return Array.Empty<KongfzItem>();
        if (_webView.CoreWebView2 is null) throw new InvalidOperationException("孔夫子搜索 WebView2 尚未初始化");

        await NavigateAsync(BuildSearchUrl(normalized), cancellationToken);

        for (var attempt = 0; attempt <= MaxCollectionAttempts; attempt += 1)
        {
            var delay = attempt == 0
                ? FirstCollectionDelayMilliseconds
                : CollectionRetryDelayMilliseconds;
            await Task.Delay(delay, cancellationToken);

            var result = ParseScriptResult(await _webView.ExecuteScriptAsync(ExtractRenderedItemsScript));
            if (result is null) continue;

            switch (result.State)
            {
                case "ready":
                    return result.Items;
                case "empty":
                    return Array.Empty<KongfzItem>();
                case "login":
                    throw new KongfzLoginRequiredException(
                        string.IsNullOrWhiteSpace(result.Message) ? "孔夫子登录状态已失效" : result.Message);
                case "verification":
                    throw new KongfzVerificationRequiredException(
                        string.IsNullOrWhiteSpace(result.Message) ? "孔夫子官方搜索要求完成验证" : result.Message);
                case "limited":
                    throw new KongfzAccessLimitedException(
                        string.IsNullOrWhiteSpace(result.Message) ? "孔夫子官方搜索已达到访问上限" : result.Message);
                case "loading":
                    continue;
                default:
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(result.Message) ? "孔夫子官方搜索页面返回异常" : result.Message);
            }
        }

        throw new TimeoutException("孔夫子官方搜索结果加载超时");
    }

    /// <summary>
    /// Inspects the already displayed official page without navigating or
    /// issuing another search request. A null result means the page cannot be
    /// inspected yet, so a manual-verification wait should keep waiting.
    /// </summary>
    public async Task<bool?> IsCurrentPageVerificationRequiredAsync()
    {
        try
        {
            if (_webView.CoreWebView2 is null) return null;

            const string script = @"
                (function() {
                  var text = ((document.body && document.body.innerText) || '').replace(/\s+/g, ' ');
                  return !!document.querySelector('#captcha-button, #captcha-element') ||
                    /查询类似计算机软件的自动请求|请您进行验证后再继续搜索|滑动滑块完成拼图|自动请求|验证后/.test(text);
                })();";
            var response = await _webView.ExecuteScriptAsync(script);
            return string.Equals(response, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static string BuildSearchUrl(MonitorConfig config)
    {
        var normalized = config.Normalize();
        var actionPath = new List<string> { "keyword", "dataType", "sortType" };
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("keyword", normalized.Keyword),
            new("dataType", "0"),
            new("sortType", "3"),
        };

        if (!string.IsNullOrWhiteSpace(normalized.Author))
        {
            parameters.Add(new KeyValuePair<string, string>("author", normalized.Author));
            actionPath.Add("author");
        }

        if (!string.IsNullOrWhiteSpace(normalized.Publisher))
        {
            parameters.Add(new KeyValuePair<string, string>("press", normalized.Publisher));
            actionPath.Add("press");
        }

        // The current official advanced-search form converts its start/end
        // price inputs to product/?price=<minimum>~<maximum>. Use that result
        // URL directly instead of loading and submitting adv.html every round.
        if (normalized.MinPrice is not null || normalized.MaxPrice is not null)
        {
            var minPrice = normalized.MinPrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            var maxPrice = normalized.MaxPrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            parameters.Add(new KeyValuePair<string, string>("price", $"{minPrice}~{maxPrice}"));
            actionPath.Add("price");
        }

        parameters.Add(new KeyValuePair<string, string>("actionPath", string.Join(",", actionPath)));
        parameters.Add(new KeyValuePair<string, string>("page", "1"));
        parameters.Add(new KeyValuePair<string, string>("userArea", "1006e6"));

        var query = string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return $"{SearchPageBaseUrl}?{query}";
    }

    private async Task NavigateAsync(string url, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<CoreWebView2NavigationCompletedEventArgs>? onNavigationCompleted = null;
        onNavigationCompleted = (_, args) => completion.TrySetResult(args);
        _webView.NavigationCompleted += onNavigationCompleted;

        try
        {
            using var registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            _webView.CoreWebView2!.Navigate(url);
            var result = await completion.Task;
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException($"孔夫子官方搜索页面加载失败（{result.WebErrorStatus}）");
            }
        }
        finally
        {
            _webView.NavigationCompleted -= onNavigationCompleted;
        }
    }

    private static SearchResult? ParseScriptResult(string scriptResponse)
    {
        try
        {
            // ExecuteScriptAsync normally returns the JSON representation of
            // an object. It only returns a JSON string when the script itself
            // returned JSON.stringify(...). Accept both shapes so a rendered
            // result page cannot be mistaken for a collection timeout.
            using var responseDocument = JsonDocument.Parse(scriptResponse);
            var responseRoot = responseDocument.RootElement;
            if (responseRoot.ValueKind == JsonValueKind.Object)
            {
                return ParseSearchResult(responseRoot);
            }

            if (responseRoot.ValueKind != JsonValueKind.String) return null;

            var decoded = responseRoot.GetString();
            if (string.IsNullOrWhiteSpace(decoded)) return null;

            using var decodedDocument = JsonDocument.Parse(decoded);
            return decodedDocument.RootElement.ValueKind == JsonValueKind.Object
                ? ParseSearchResult(decodedDocument.RootElement)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SearchResult ParseSearchResult(JsonElement root)
    {
        var items = new List<KongfzItem>();
        if (root.TryGetProperty("items", out var itemArray) && itemArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in itemArray.EnumerateArray())
            {
                var item = ParseItem(element);
                if (item is not null) items.Add(item);
            }
        }

        return new SearchResult(
            GetString(root, "state"),
            GetString(root, "message"),
            items);
    }

    private static KongfzItem? ParseItem(JsonElement element)
    {
        var itemId = GetString(element, "itemId");
        var itemUrl = GetString(element, "itemUrl");
        var title = GetString(element, "title");
        var price = ParsePrice(GetString(element, "priceText"));
        if (string.IsNullOrWhiteSpace(itemId) ||
            string.IsNullOrWhiteSpace(title) ||
            price is null ||
            !IsKongfzUrl(itemUrl))
        {
            return null;
        }

        return new KongfzItem
        {
            ItemId = itemId,
            ItemUrl = itemUrl,
            Title = title,
            Author = GetString(element, "author"),
            Publisher = GetString(element, "publisher"),
            Price = price,
        };
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static double? ParsePrice(string rawPrice)
    {
        var normalized = rawPrice
            .Replace("￥", string.Empty)
            .Replace("¥", string.Empty)
            .Replace(",", string.Empty)
            .Trim();
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var price)
            ? price
            : null;
    }

    private static bool IsKongfzUrl(string rawUrl)
    {
        return Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Host, "kongfz.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".kongfz.com", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record SearchResult(string State, string Message, IReadOnlyList<KongfzItem> Items);
}

public sealed class KongfzLoginRequiredException : Exception
{
    public KongfzLoginRequiredException(string message) : base(message)
    {
    }
}

public sealed class KongfzVerificationRequiredException : Exception
{
    public KongfzVerificationRequiredException(string message) : base(message)
    {
    }
}

public sealed class KongfzAccessLimitedException : Exception
{
    public KongfzAccessLimitedException(string message) : base(message)
    {
    }
}
}

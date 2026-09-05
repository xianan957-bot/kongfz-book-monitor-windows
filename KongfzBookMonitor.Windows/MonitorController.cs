using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KongfzBookMonitor.Windows
{

public sealed class MonitorController : IDisposable
{
    private readonly MonitorConfigStore _configStore;
    private readonly ProcessedItemStore _processedItems;
    private readonly KongfzSearchClient _searchClient;
    private CancellationTokenSource? _cancellation;
    private Task? _monitoringTask;
    private int _completedRounds;
    private bool _pausedForOfficialCheckout;
    private bool _establishBaselineAfterVerification;

    public MonitorController(
        MonitorConfigStore configStore,
        ProcessedItemStore processedItems,
        KongfzSearchClient searchClient)
    {
        _configStore = configStore;
        _processedItems = processedItems;
        _searchClient = searchClient;
    }

    public bool IsRunning { get; private set; }

    public event Action<string>? StatusChanged;
    public event Action<int>? RoundCountChanged;
    public event Action<KongfzItem>? MatchedItem;
    public event Func<KongfzItem, CancellationToken, Task<bool>>? CheckoutRequested;
    public event Func<CancellationToken, Task>? VerificationWaitRequested;

    public void Start(MonitorConfig config)
    {
        if (IsRunning) return;

        var normalized = config.Normalize();
        normalized.Monitoring = true;
        _configStore.Save(normalized);
        _processedItems.Clear();

        _cancellation?.Dispose();
        _completedRounds = 0;
        _pausedForOfficialCheckout = false;
        _establishBaselineAfterVerification = false;
        _cancellation = new CancellationTokenSource();
        IsRunning = true;
        StatusChanged?.Invoke("监控状态：运行中");
        RoundCountChanged?.Invoke(_completedRounds);
        _monitoringTask = RunAsync(_cancellation.Token);
    }

    public void Stop()
    {
        _configStore.SetMonitoring(false);
        _cancellation?.Cancel();
        if (!IsRunning) StatusChanged?.Invoke("监控状态：已停止");
    }

    public async Task StopAsync()
    {
        Stop();

        var monitoringTask = _monitoringTask;
        if (monitoringTask is not null)
        {
            await monitoringTask;
        }
    }

    public void PauseAfterOfficialCheckoutOpened()
    {
        if (!IsRunning) return;

        _pausedForOfficialCheckout = true;
        _configStore.SetMonitoring(false);
        _cancellation?.Cancel();
        StatusChanged?.Invoke("监控状态：已进入官方下单确认页，监控已暂停");
    }

    public void Dispose()
    {
        Stop();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var config = _configStore.Load();
                if (!config.Monitoring || string.IsNullOrWhiteSpace(config.Keyword)) break;

                var completedRound = false;
                var roundCountReported = false;
                try
                {
                    var items = await _searchClient.FetchAsync(config, cancellationToken);
                    var checkoutOpened = await ProcessItemsAsync(items, config, cancellationToken);
                    completedRound = true;

                    if (checkoutOpened)
                    {
                        PauseAfterOfficialCheckoutOpened();
                        break;
                    }

                    if (cancellationToken.IsCancellationRequested) break;

                    StatusChanged?.Invoke("监控状态：运行中");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (KongfzLoginRequiredException)
                {
                    StatusChanged?.Invoke("监控状态：需要重新登录孔夫子");
                    completedRound = true;
                }
                catch (KongfzVerificationRequiredException)
                {
                    StatusChanged?.Invoke("监控状态：检测到孔夫子人机验证，已暂停等待手动验证");
                    completedRound = true;
                    ReportCompletedRound();
                    roundCountReported = true;

                    try
                    {
                        await WaitForManualVerificationAsync(cancellationToken);
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            _establishBaselineAfterVerification = true;
                            StatusChanged?.Invoke("监控状态：验证完成，继续监控");
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        // If the verification page cannot be inspected this
                        // time, leave it visible and retry through the normal
                        // next polling round rather than treating it as a new item.
                    }
                }
                catch (KongfzAccessLimitedException)
                {
                    StatusChanged?.Invoke("监控状态：孔夫子搜索已达到访问上限，等待下一轮");
                    completedRound = true;
                }
                catch (Exception error)
                {
                    StatusChanged?.Invoke(
                        $"监控状态：本轮搜索失败（{DescribeFailure(error)}），等待下一轮");
                    completedRound = true;
                }
                finally
                {
                    if (completedRound && !roundCountReported)
                    {
                        ReportCompletedRound();
                    }
                }

                try
                {
                    await Task.Delay(config.IntervalSeconds * 1000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            IsRunning = false;
            _monitoringTask = null;
            StatusChanged?.Invoke(_pausedForOfficialCheckout
                ? "监控状态：已进入官方下单确认页，监控已暂停"
                : "监控状态：已停止");
        }
    }

    private async Task<bool> ProcessItemsAsync(
        IReadOnlyList<KongfzItem> items,
        MonitorConfig config,
        CancellationToken cancellationToken)
    {
        var establishBaseline = _establishBaselineAfterVerification;
        var newlyMatchedItems = new List<KongfzItem>();
        var currentMatchedItems = new List<KongfzItem>();

        foreach (var item in items)
        {
            var isNew = !_processedItems.Contains(item.ItemId);
            try
            {
                if (!ItemFilter.Matches(item, config)) continue;

                // “最低价” means the item's displayed listing price, excluding
                // freight. It is selected from every matching item currently
                // rendered on page one, not from visual order in the list.
                currentMatchedItems.Add(item);
                if (isNew) newlyMatchedItems.Add(item);
            }
            finally
            {
                // Non-matching items must also be remembered so they are not
                // reconsidered as new on every later polling round.
                if (isNew) _processedItems.MarkProcessed(item);
            }
        }

        if (establishBaseline)
        {
            // Results shown immediately after a human verification are the
            // existing page backlog, not a reliable newly listed batch. Record
            // them for deduplication, then resume normal monitoring from the
            // following polling round without alerting or opening checkout.
            _establishBaselineAfterVerification = false;
            return false;
        }

        foreach (var item in NotificationItemsForRound(newlyMatchedItems, currentMatchedItems))
        {
            MatchedItem?.Invoke(item);
        }

        // Keep new-item monitoring semantics: a purchase attempt starts only
        // when this round has a new matching result. When it does, pick the
        // actual lowest price from all currently rendered matching candidates.
        if (newlyMatchedItems.Count == 0) return false;

        var lowestPricedItem = SelectLowestPricedItem(currentMatchedItems);
        if (lowestPricedItem is null) return false;

        return await RequestCheckoutAsync(lowestPricedItem, cancellationToken);
    }

    // Windows queues each balloon separately. A result page can contain many
    // matches, but this round can only lead to one official checkout attempt,
    // so notify only for that same lowest-price item.
    internal static IReadOnlyList<KongfzItem> NotificationItemsForRound(
        IReadOnlyList<KongfzItem> newlyMatchedItems,
        IReadOnlyList<KongfzItem> currentMatchedItems)
    {
        if (newlyMatchedItems.Count == 0) return Array.Empty<KongfzItem>();

        var lowestPricedItem = SelectLowestPricedItem(currentMatchedItems);
        return lowestPricedItem is null
            ? Array.Empty<KongfzItem>()
            : new[] { lowestPricedItem };
    }

    private static KongfzItem? SelectLowestPricedItem(IReadOnlyList<KongfzItem> items)
    {
        return items
            .Where(item => item.Price.HasValue)
            .OrderBy(item => item.Price!.Value)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private async Task<bool> RequestCheckoutAsync(KongfzItem item, CancellationToken cancellationToken)
    {
        var handlers = CheckoutRequested;
        if (handlers is null) return false;

        foreach (Func<KongfzItem, CancellationToken, Task<bool>> handler in handlers.GetInvocationList())
        {
            try
            {
                if (await handler(item, cancellationToken)) return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A browser click failure is not a search failure. The next
                // monitoring round can continue normally.
            }
        }

        return false;
    }

    private async Task WaitForManualVerificationAsync(CancellationToken cancellationToken)
    {
        var handlers = VerificationWaitRequested;
        if (handlers is null) return;

        foreach (Func<CancellationToken, Task> handler in handlers.GetInvocationList())
        {
            await handler(cancellationToken);
        }
    }

    private void ReportCompletedRound()
    {
        _completedRounds += 1;
        RoundCountChanged?.Invoke(_completedRounds);
    }

    private static string DescribeFailure(Exception error)
    {
        return error switch
        {
            TimeoutException => "搜索结果加载超时",
            InvalidOperationException => string.IsNullOrWhiteSpace(error.Message)
                ? "搜索页面异常"
                : error.Message,
            System.IO.IOException => "本地已处理商品记录无法保存",
            UnauthorizedAccessException => "本地数据目录没有写入权限",
            _ => error.GetType().Name,
        };
    }
}
}

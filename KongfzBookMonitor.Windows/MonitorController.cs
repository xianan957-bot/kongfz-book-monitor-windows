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
    public event Action<KongfzItem>? MatchedItem;
    public event Action<KongfzItem>? LowestPricedMatchedItem;

    public void Start(MonitorConfig config)
    {
        if (IsRunning) return;

        var normalized = config.Normalize();
        normalized.Monitoring = true;
        _configStore.Save(normalized);
        _processedItems.Clear();

        _cancellation = new CancellationTokenSource();
        IsRunning = true;
        StatusChanged?.Invoke("监控状态：运行中");
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

                try
                {
                    var items = await _searchClient.FetchAsync(config, cancellationToken);
                    ProcessItems(items, config);
                    StatusChanged?.Invoke("监控状态：运行中");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (KongfzLoginRequiredException)
                {
                    StatusChanged?.Invoke("监控状态：需要重新登录孔夫子");
                }
                catch (KongfzVerificationRequiredException)
                {
                    StatusChanged?.Invoke("监控状态：孔夫子搜索要求验证或已达到访问上限");
                }
                catch (Exception)
                {
                    StatusChanged?.Invoke("监控状态：本轮搜索失败，等待下一轮");
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
            StatusChanged?.Invoke("监控状态：已停止");
        }
    }

    private void ProcessItems(IReadOnlyList<KongfzItem> items, MonitorConfig config)
    {
        var matchingItems = new List<KongfzItem>();

        foreach (var item in items)
        {
            if (_processedItems.Contains(item.ItemId)) continue;

            try
            {
                if (ItemFilter.Matches(item, config))
                {
                    matchingItems.Add(item);
                }
            }
            finally
            {
                // Non-matching items must also be remembered so they are not
                // reconsidered as new on every later polling round.
                _processedItems.MarkProcessed(item);
            }
        }

        foreach (var item in matchingItems)
        {
            MatchedItem?.Invoke(item);
        }

        var lowestPricedItem = matchingItems
            .Where(item => item.Price.HasValue)
            .OrderBy(item => item.Price!.Value)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (lowestPricedItem is not null)
        {
            LowestPricedMatchedItem?.Invoke(lowestPricedItem);
        }
    }
}
}

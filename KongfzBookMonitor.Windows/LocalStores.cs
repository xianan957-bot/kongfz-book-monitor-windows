using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KongfzBookMonitor.Windows
{

internal static class AppDataPaths
{
    public static string Root
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KongfzBookMonitor");
            Directory.CreateDirectory(path);
            return path;
        }
    }
}

public sealed class MonitorConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _path = Path.Combine(AppDataPaths.Root, "monitor-config.json");

    public MonitorConfig Load()
    {
        try
        {
            if (!File.Exists(_path)) return new MonitorConfig();
            var config = JsonSerializer.Deserialize<MonitorConfig>(File.ReadAllText(_path), JsonOptions);
            return (config ?? new MonitorConfig()).Normalize();
        }
        catch (Exception)
        {
            return new MonitorConfig();
        }
    }

    public void Save(MonitorConfig config)
    {
        var normalized = config.Normalize();
        File.WriteAllText(_path, JsonSerializer.Serialize(normalized, JsonOptions));
    }

    public void SetMonitoring(bool monitoring)
    {
        var config = Load();
        config.Monitoring = monitoring;
        Save(config);
    }
}

public sealed class ProcessedItemStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path = Path.Combine(AppDataPaths.Root, "processed-items.json");
    private Dictionary<string, ProcessedItem>? _items;

    public bool Contains(string itemId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _items!.ContainsKey(itemId);
        }
    }

    public void MarkProcessed(KongfzItem item)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_items!.ContainsKey(item.ItemId)) return;

            _items[item.ItemId] = new ProcessedItem
            {
                ItemId = item.ItemId,
                ItemUrl = item.ItemUrl,
                Title = item.Title,
                Price = item.Price,
                ProcessedAt = DateTimeOffset.Now,
            };
            Persist();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items = new Dictionary<string, ProcessedItem>(StringComparer.Ordinal);
            Persist();
        }
    }

    private void EnsureLoaded()
    {
        if (_items is not null) return;

        try
        {
            var records = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<ProcessedItem>>(File.ReadAllText(_path), JsonOptions)
                : null;
            _items = (records ?? new List<ProcessedItem>())
                .Where(item => !string.IsNullOrWhiteSpace(item.ItemId))
                .GroupBy(item => item.ItemId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        }
        catch (Exception)
        {
            _items = new Dictionary<string, ProcessedItem>(StringComparer.Ordinal);
        }
    }

    private void Persist()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(_items!.Values, JsonOptions));
    }
}
}

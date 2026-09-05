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

public interface IMonitorConfigStore
{
    MonitorConfig Load();
    void Save(MonitorConfig config);
    void SetMonitoring(bool monitoring);
}

public sealed class MonitorConfigStore : IMonitorConfigStore
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

/// <summary>
/// Owns the five fixed monitoring configurations. It migrates the previous
/// single-rule file into slot one the first time the multi-rule app starts.
/// </summary>
public sealed class MonitorRulesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path = Path.Combine(AppDataPaths.Root, "monitor-rules.json");
    private readonly string _legacyPath = Path.Combine(AppDataPaths.Root, "monitor-config.json");

    public MonitorRuleSettings Load()
    {
        lock (_gate)
        {
            return LoadCore();
        }
    }

    public MonitorRule LoadRule(string ruleId)
    {
        lock (_gate)
        {
            var rule = LoadCore().Rules.FirstOrDefault(candidate => candidate.Id == ruleId);
            if (rule is null) throw new InvalidOperationException("监控任务不存在");
            return rule.Normalize();
        }
    }

    public void UpdateRule(string ruleId, Action<MonitorRule> update)
    {
        lock (_gate)
        {
            var settings = LoadCore();
            var rule = settings.Rules.FirstOrDefault(candidate => candidate.Id == ruleId);
            if (rule is null) throw new InvalidOperationException("监控任务不存在");

            update(rule);
            SaveCore(settings);
        }
    }

    public void Save(MonitorRuleSettings settings)
    {
        lock (_gate)
        {
            SaveCore(settings);
        }
    }

    private MonitorRuleSettings LoadCore()
    {
        try
        {
            if (File.Exists(_path))
            {
                var persisted = JsonSerializer.Deserialize<MonitorRuleSettings>(
                    File.ReadAllText(_path),
                    JsonOptions);
                return (persisted ?? new MonitorRuleSettings()).Normalize();
            }
        }
        catch (Exception)
        {
            // A malformed configuration falls back to the legacy/default
            // shape instead of preventing the user from opening the app.
        }

        var migrated = CreateMigratedSettings();
        SaveCore(migrated);
        return migrated;
    }

    private MonitorRuleSettings CreateMigratedSettings()
    {
        var firstRule = new MonitorRule { Slot = 1 };
        try
        {
            if (File.Exists(_legacyPath))
            {
                var legacy = JsonSerializer.Deserialize<MonitorConfig>(
                    File.ReadAllText(_legacyPath),
                    JsonOptions);
                if (legacy is not null) firstRule.Apply(legacy);
            }
        }
        catch (Exception)
        {
            // Keep an empty first slot if the former single-rule file cannot
            // be read. The original file is deliberately left untouched.
        }

        return new MonitorRuleSettings
        {
            Rules = new List<MonitorRule>
            {
                firstRule,
                new() { Slot = 2 },
                new() { Slot = 3 },
                new() { Slot = 4 },
                new() { Slot = 5 },
            },
        }.Normalize();
    }

    private void SaveCore(MonitorRuleSettings settings)
    {
        var normalized = settings.Normalize();
        File.WriteAllText(_path, JsonSerializer.Serialize(normalized, JsonOptions));
    }
}

/// <summary>
/// Presents one rule inside the existing MonitorController's single-rule
/// contract while persisting only that rule in MonitorRulesStore.
/// </summary>
public sealed class RuleMonitorConfigStore : IMonitorConfigStore
{
    private readonly MonitorRulesStore _rulesStore;
    private readonly string _ruleId;

    public RuleMonitorConfigStore(MonitorRulesStore rulesStore, string ruleId)
    {
        _rulesStore = rulesStore;
        _ruleId = ruleId;
    }

    public MonitorConfig Load()
    {
        return _rulesStore.LoadRule(_ruleId).ToMonitorConfig();
    }

    public void Save(MonitorConfig config)
    {
        _rulesStore.UpdateRule(_ruleId, rule => rule.Apply(config));
    }

    public void SetMonitoring(bool monitoring)
    {
        _rulesStore.UpdateRule(_ruleId, rule => rule.Monitoring = monitoring);
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
    private readonly string _path;
    private readonly string? _legacyPath;
    private Dictionary<string, ProcessedItem>? _items;

    public ProcessedItemStore(string? ruleId = null, bool migrateLegacyItems = false)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            _path = Path.Combine(AppDataPaths.Root, "processed-items.json");
            return;
        }

        var directory = Path.Combine(AppDataPaths.Root, "processed-items");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"{ruleId}.json");
        if (migrateLegacyItems)
        {
            _legacyPath = Path.Combine(AppDataPaths.Root, "processed-items.json");
            // Copy the former single-task de-duplication data before a new
            // monitoring run has a chance to clear only this rule's store.
            // The original legacy file is intentionally preserved.
            lock (_gate)
            {
                EnsureLoaded();
            }
        }
    }

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
            var sourcePath = File.Exists(_path)
                ? _path
                : _legacyPath;
            var records = !string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath)
                ? JsonSerializer.Deserialize<List<ProcessedItem>>(File.ReadAllText(sourcePath), JsonOptions)
                : null;
            _items = (records ?? new List<ProcessedItem>())
                .Where(item => !string.IsNullOrWhiteSpace(item.ItemId))
                .GroupBy(item => item.ItemId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            if (!string.Equals(sourcePath, _path, StringComparison.OrdinalIgnoreCase)) Persist();
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

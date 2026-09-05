using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace KongfzBookMonitor.Windows
{

public sealed class MonitorConfig
{
    public string Keyword { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public double? MinPrice { get; set; }
    public double? MaxPrice { get; set; }
    public int IntervalSeconds { get; set; } = 5;
    public bool Monitoring { get; set; }

    public MonitorConfig Normalize()
    {
        return new MonitorConfig
        {
            Keyword = Keyword?.Trim() ?? string.Empty,
            Author = Author?.Trim() ?? string.Empty,
            Publisher = Publisher?.Trim() ?? string.Empty,
            MinPrice = MinPrice,
            MaxPrice = MaxPrice,
            IntervalSeconds = Math.Clamp(IntervalSeconds, 1, 15),
            Monitoring = Monitoring,
        };
    }
}

/// <summary>
/// Persisted configuration for one of the five fixed monitoring slots.
/// Runtime-only state such as captcha waiting and completed round count is
/// intentionally kept outside this model.
/// </summary>
public sealed class MonitorRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Slot { get; set; }
    public string Keyword { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public double? MinPrice { get; set; }
    public double? MaxPrice { get; set; }
    public int IntervalSeconds { get; set; } = 5;
    public bool Monitoring { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Keyword)
        ? $"任务 {Slot}"
        : $"任务 {Slot}：{Keyword}";

    public MonitorRule Normalize()
    {
        return new MonitorRule
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
            Slot = Slot,
            Keyword = Keyword?.Trim() ?? string.Empty,
            Author = Author?.Trim() ?? string.Empty,
            Publisher = Publisher?.Trim() ?? string.Empty,
            MinPrice = MinPrice,
            MaxPrice = MaxPrice,
            IntervalSeconds = Math.Clamp(IntervalSeconds, 1, 15),
            Monitoring = Monitoring,
        };
    }

    public MonitorConfig ToMonitorConfig()
    {
        return new MonitorConfig
        {
            Keyword = Keyword,
            Author = Author,
            Publisher = Publisher,
            MinPrice = MinPrice,
            MaxPrice = MaxPrice,
            IntervalSeconds = IntervalSeconds,
            Monitoring = Monitoring,
        }.Normalize();
    }

    public void Apply(MonitorConfig config)
    {
        var normalized = config.Normalize();
        Keyword = normalized.Keyword;
        Author = normalized.Author;
        Publisher = normalized.Publisher;
        MinPrice = normalized.MinPrice;
        MaxPrice = normalized.MaxPrice;
        IntervalSeconds = normalized.IntervalSeconds;
        Monitoring = normalized.Monitoring;
    }
}

public sealed class MonitorRuleSettings
{
    public List<MonitorRule> Rules { get; set; } = new();

    public MonitorRuleSettings Normalize()
    {
        var normalizedRules = (Rules ?? new List<MonitorRule>())
            .Where(rule => rule is not null)
            .Select(rule => rule.Normalize())
            .ToList();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var fixedSlots = new List<MonitorRule>();

        for (var slot = 1; slot <= 5; slot += 1)
        {
            var rule = normalizedRules.FirstOrDefault(candidate => candidate.Slot == slot)
                ?? new MonitorRule { Slot = slot };
            rule.Slot = slot;
            if (!usedIds.Add(rule.Id)) rule.Id = Guid.NewGuid().ToString("N");
            fixedSlots.Add(rule);
        }

        return new MonitorRuleSettings { Rules = fixedSlots };
    }
}

public sealed class KongfzItem
{
    public string ItemId { get; set; } = string.Empty;
    public string ItemUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public double? Price { get; set; }
}

public sealed class ProcessedItem
{
    public string ItemId { get; set; } = string.Empty;
    public string ItemUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public double? Price { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
}

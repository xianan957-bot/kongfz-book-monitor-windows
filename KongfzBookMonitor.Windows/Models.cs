using System;

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

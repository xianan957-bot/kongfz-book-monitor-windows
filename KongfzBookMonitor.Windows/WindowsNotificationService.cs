using System;
using System.Collections.Generic;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace KongfzBookMonitor.Windows
{

public sealed class WindowsNotificationService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private string? _lastItemUrl;

    public WindowsNotificationService()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "孔夫子商品监控",
            Visible = true,
        };
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_lastItemUrl)) ItemClicked?.Invoke(_lastItemUrl);
        };
    }

    public event Action<string>? ItemClicked;

    public void ShowNewItem(KongfzItem item)
    {
        _lastItemUrl = item.ItemUrl;
        _notifyIcon.ShowBalloonTip(
            10000,
            item.Title,
            BuildSummary(item),
            Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private static string BuildSummary(KongfzItem item)
    {
        var pieces = new List<string>();
        if (item.Price is double price) pieces.Add($"¥{price:0.##}");
        if (!string.IsNullOrWhiteSpace(item.Author)) pieces.Add(item.Author);
        if (!string.IsNullOrWhiteSpace(item.Publisher)) pieces.Add(item.Publisher);
        return pieces.Count > 0 ? string.Join(" · ", pieces) : "点击查看商品";
    }
}
}

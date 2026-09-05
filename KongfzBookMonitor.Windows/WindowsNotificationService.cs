using System;
using System.Collections.Generic;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace KongfzBookMonitor.Windows
{

public sealed class WindowsNotificationService : IDisposable
{
    private readonly object _gate = new();
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Queue<NotificationEntry> _pending = new();
    private NotificationEntry? _current;
    private bool _isDisposed;

    public WindowsNotificationService()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "孔夫子商品监控",
            Visible = true,
        };
        _notifyIcon.BalloonTipClicked += NotifyIcon_BalloonTipClicked;
        _notifyIcon.BalloonTipClosed += NotifyIcon_BalloonTipClosed;
    }

    public event Action<string>? ItemClicked;

    public void ShowNewItem(KongfzItem item)
    {
        ShowNewItem(string.Empty, string.Empty, item);
    }

    /// <summary>
    /// Keeps each balloon associated with its originating monitor rule. A
    /// single tray icon can show only one balloon at a time, so entries are
    /// serialized rather than overwriting another task's item URL.
    /// </summary>
    public void ShowNewItem(string ruleId, string taskName, KongfzItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ItemUrl)) return;

        lock (_gate)
        {
            if (_isDisposed) return;

            var title = string.IsNullOrWhiteSpace(taskName)
                ? item.Title
                : $"{taskName}：{item.Title}";
            _pending.Enqueue(new NotificationEntry(
                ruleId ?? string.Empty,
                title,
                BuildSummary(item),
                item.ItemUrl));
            ShowNextLocked();
        }
    }

    /// <summary>
    /// Removes balloons that have not yet reached the Windows shell when a
    /// rule stops. The one already visible is left to expire normally because
    /// NotifyIcon cannot safely retract a shown system balloon.
    /// </summary>
    public void CancelRuleNotifications(string ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId)) return;

        lock (_gate)
        {
            if (_isDisposed || _pending.Count == 0) return;

            var retained = new Queue<NotificationEntry>();
            while (_pending.Count > 0)
            {
                var entry = _pending.Dequeue();
                if (!string.Equals(entry.RuleId, ruleId, StringComparison.Ordinal))
                {
                    retained.Enqueue(entry);
                }
            }

            while (retained.Count > 0) _pending.Enqueue(retained.Dequeue());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _pending.Clear();
            _current = null;
            _notifyIcon.BalloonTipClicked -= NotifyIcon_BalloonTipClicked;
            _notifyIcon.BalloonTipClosed -= NotifyIcon_BalloonTipClosed;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }

    private void NotifyIcon_BalloonTipClicked(object? sender, EventArgs e)
    {
        string? itemUrl;
        lock (_gate)
        {
            itemUrl = _current?.ItemUrl;
        }

        if (!string.IsNullOrWhiteSpace(itemUrl)) ItemClicked?.Invoke(itemUrl);
    }

    private void NotifyIcon_BalloonTipClosed(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_isDisposed) return;
            _current = null;
            ShowNextLocked();
        }
    }

    private void ShowNextLocked()
    {
        if (_current is not null || _pending.Count == 0 || _isDisposed) return;

        _current = _pending.Dequeue();
        _notifyIcon.ShowBalloonTip(
            10000,
            _current.Title,
            _current.Summary,
            Forms.ToolTipIcon.Info);
    }

    private static string BuildSummary(KongfzItem item)
    {
        var pieces = new List<string>();
        if (item.Price is double price) pieces.Add($"¥{price:0.##}");
        if (!string.IsNullOrWhiteSpace(item.Author)) pieces.Add(item.Author);
        if (!string.IsNullOrWhiteSpace(item.Publisher)) pieces.Add(item.Publisher);
        return pieces.Count > 0 ? string.Join(" · ", pieces) : "点击查看商品";
    }

    private sealed record NotificationEntry(
        string RuleId,
        string Title,
        string Summary,
        string ItemUrl);
}
}

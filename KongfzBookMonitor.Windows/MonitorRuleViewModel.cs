using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace KongfzBookMonitor.Windows
{

/// <summary>
/// UI state for one fixed task slot. It deliberately contains no browser or
/// monitor state, so each rule's visible status cannot mutate another rule.
/// </summary>
public sealed class MonitorRuleViewModel : INotifyPropertyChanged
{
    private MonitorRule _rule;
    private string _statusText;
    private int _roundCount;

    public MonitorRuleViewModel(MonitorRule rule)
    {
        _rule = rule.Normalize();
        _statusText = _rule.Monitoring ? "准备恢复监控" : "已停止";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string RuleId => _rule.Id;
    public int Slot => _rule.Slot;
    public string SlotText => $"任务 {Slot}";
    public string KeywordText => string.IsNullOrWhiteSpace(_rule.Keyword) ? "未设置关键词" : _rule.Keyword;
    public string CriteriaSummary => BuildCriteriaSummary(_rule);
    public string IntervalText => $"{_rule.IntervalSeconds} 秒";
    public string StatusText => _statusText;
    public string RoundCountText => $"{_roundCount} 轮";
    public MonitorRule Rule => _rule.Normalize();

    public void UpdateConfiguration(MonitorRule rule)
    {
        _rule = rule.Normalize();
        RaiseAllConfigurationProperties();
    }

    public void UpdateStatus(string status)
    {
        _statusText = RemoveStatusPrefix(status);
        OnPropertyChanged(nameof(StatusText));
    }

    public void UpdateRoundCount(int roundCount)
    {
        _roundCount = Math.Max(0, roundCount);
        OnPropertyChanged(nameof(RoundCountText));
    }

    private static string BuildCriteriaSummary(MonitorRule rule)
    {
        var summary = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(rule.Author)) summary.Append($"作者：{rule.Author}");
        if (!string.IsNullOrWhiteSpace(rule.Publisher))
        {
            if (summary.Length > 0) summary.Append("；");
            summary.Append($"出版社：{rule.Publisher}");
        }

        if (rule.MinPrice is not null || rule.MaxPrice is not null)
        {
            if (summary.Length > 0) summary.Append("；");
            var minimum = rule.MinPrice?.ToString("0.##", CultureInfo.InvariantCulture) ?? "不限";
            var maximum = rule.MaxPrice?.ToString("0.##", CultureInfo.InvariantCulture) ?? "不限";
            summary.Append($"价格：¥{minimum}～¥{maximum}");
        }

        return summary.Length == 0 ? "无附加筛选" : summary.ToString();
    }

    private static string RemoveStatusPrefix(string status)
    {
        const string prefix = "监控状态：";
        return status.StartsWith(prefix, StringComparison.Ordinal) ? status[prefix.Length..] : status;
    }

    private void RaiseAllConfigurationProperties()
    {
        OnPropertyChanged(nameof(RuleId));
        OnPropertyChanged(nameof(Slot));
        OnPropertyChanged(nameof(SlotText));
        OnPropertyChanged(nameof(KeywordText));
        OnPropertyChanged(nameof(CriteriaSummary));
        OnPropertyChanged(nameof(IntervalText));
        OnPropertyChanged(nameof(Rule));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KongfzBookMonitor.Windows
{

public partial class MainWindow : Window
{
    private readonly Dictionary<string, MonitorRuleSession> _sessions = new(StringComparer.Ordinal);
    private MonitorRulesStore? _rulesStore;
    private CoreWebView2Environment? _webViewEnvironment;
    private WindowsNotificationService? _notificationService;
    private Timer? _expirationTimer;
    private bool _isInitialized;
    private bool _isClosing;
    private bool _isExpired;
    private bool _expirationNoticeShown;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<MonitorRuleViewModel> Rules { get; } = new();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized) return;
        _isInitialized = true;

        if (UsageExpirationPolicy.HasExpired(DateTime.Now))
        {
            await ExpireApplicationAsync();
            return;
        }

        ArmExpirationTimer();
        _rulesStore = new MonitorRulesStore();
        var settings = _rulesStore.Load();
        Rules.Clear();
        foreach (var rule in settings.Rules.OrderBy(rule => rule.Slot))
        {
            Rules.Add(new MonitorRuleViewModel(rule));
        }

        try
        {
            // Every browser uses the same WebView2 environment, so manual
            // login Cookie/session data are shared, but each task gets its own
            // browser control and cannot navigate another task's result page.
            BrowserPageTab.IsSelected = true;
            BrowserTabs.SelectedItem = LoginBrowserTab;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);

            _webViewEnvironment = await WebViewEnvironmentFactory.CreateAsync();
            await LoginWebView.EnsureCoreWebView2Async(_webViewEnvironment);

            _notificationService = new WindowsNotificationService();
            _notificationService.ItemClicked += itemUrl => Dispatcher.BeginInvoke(
                new Action(() => OpenItemWindow(itemUrl)));

            foreach (var ruleViewModel in Rules.OrderBy(rule => rule.Slot))
            {
                await CreateSessionAsync(ruleViewModel);
            }

            RuleList.SelectedItem = Rules.FirstOrDefault();
            MainTabs.SelectedIndex = 0;
            UpdateSummary();

            if (!EnsureUsageAvailable()) return;

            // Preserve the former single-task behavior: a task that was
            // intentionally left running is resumed on next launch. Each one
            // resumes independently and cannot change another task's state.
            foreach (var session in _sessions.Values.OrderBy(item => item.ViewModel.Slot))
            {
                var config = session.ConfigStore.Load();
                if (config.Monitoring && !string.IsNullOrWhiteSpace(config.Keyword))
                {
                    StartSession(session, config, showMessageForEmptyKeyword: false);
                }
            }
        }
        catch (Exception error)
        {
            if (_isExpired || _isClosing) return;
            SummaryText.Text = "监控状态：WebView2 初始化失败";
            MessageBox.Show(
                this,
                $"无法初始化 Windows WebView2：{error.Message}",
                "孔夫子商品监控",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task CreateSessionAsync(MonitorRuleViewModel ruleViewModel)
    {
        if (_rulesStore is null || _webViewEnvironment is null || !EnsureUsageAvailable()) return;

        var webView = new WebView2();
        var browserTab = new TabItem
        {
            Header = $"{ruleViewModel.SlotText} 网页",
            Content = CreateTaskBrowserContent(ruleViewModel, webView),
        };
        BrowserTabs.Items.Add(browserTab);
        BrowserTabs.SelectedItem = browserTab;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await webView.EnsureCoreWebView2Async(_webViewEnvironment);

        var session = new MonitorRuleSession(
            ruleViewModel.Rule,
            ruleViewModel,
            _rulesStore,
            webView,
            browserTab,
            _webViewEnvironment);
        _sessions.Add(session.RuleId, session);

        session.Controller.StatusChanged += status => HandleSessionStatusChanged(session, status);
        session.Controller.RoundCountChanged += count => HandleSessionRoundCountChanged(session, count);
        session.Controller.MatchedItem += item => HandleMatchedItem(session, item);
        session.Controller.CheckoutRequested += (item, cancellationToken) =>
            HandleCheckoutRequestedAsync(session, item, cancellationToken);
        session.Controller.VerificationWaitRequested += cancellationToken =>
            WaitForManualVerificationAsync(session, cancellationToken);
        session.CheckoutNavigator.OfficialCheckoutOpened += session.Controller.PauseAfterOfficialCheckoutOpened;
    }

    private static DockPanel CreateTaskBrowserContent(MonitorRuleViewModel ruleViewModel, WebView2 webView)
    {
        var panel = new DockPanel();
        var hint = new TextBlock
        {
            Margin = new Thickness(16, 12, 16, 8),
            TextWrapping = TextWrapping.Wrap,
            Text = $"{ruleViewModel.SlotText} 的独立搜索页面。若该任务出现人机验证，请只在此页手动完成验证；其他任务会继续运行。",
        };
        DockPanel.SetDock(hint, Dock.Top);
        panel.Children.Add(hint);
        panel.Children.Add(webView);
        return panel;
    }

    private void OpenLoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUsageAvailable()) return;
        if (LoginWebView.CoreWebView2 is null)
        {
            MessageBox.Show(this, "WebView2 正在初始化，请稍后再试。", "孔夫子商品监控");
            return;
        }

        BrowserPageTab.IsSelected = true;
        BrowserTabs.SelectedItem = LoginBrowserTab;
        LoginWebView.CoreWebView2.Navigate("https://www.kongfz.com/");
    }

    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RuleList.SelectedItem is not MonitorRuleViewModel ruleViewModel) return;
        LoadRuleIntoInputs(ruleViewModel.Rule);
        SelectedRuleText.Text = $"编辑 {ruleViewModel.SlotText}";
    }

    private void EditRuleButton_Click(object sender, RoutedEventArgs e)
    {
        SelectRuleFromButton(sender);
    }

    private void StartRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUsageAvailable()) return;
        if (GetSessionFromButton(sender) is not { } session) return;
        SelectRule(session.ViewModel);
        StartSession(session, session.ConfigStore.Load(), showMessageForEmptyKeyword: true);
    }

    private async void StopRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUsageAvailable()) return;
        if (GetSessionFromButton(sender) is not { } session) return;
        SelectRule(session.ViewModel);
        await StopSessionAsync(session);
    }

    private void OpenRuleBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUsageAvailable()) return;
        if (GetSessionFromButton(sender) is not { } session) return;
        SelectRule(session.ViewModel);
        OpenTaskBrowser(session);
    }

    private async void SaveSelectedRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUsageAvailable()) return;
        if (GetSelectedSession() is not { } session) return;
        if (!TryReadConfig(out var config)) return;

        if (session.Controller.IsRunning)
        {
            await StopSessionAsync(session);
        }

        SaveSessionConfiguration(session, config, monitoring: false);
        session.ViewModel.UpdateStatus("已保存，尚未开始");
        UpdateSummary();
    }

    private async void StartSelectedRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUsageAvailable()) return;
        if (GetSelectedSession() is not { } session) return;
        if (!TryReadConfig(out var config)) return;

        // This matches the prior single-keyword start behavior: the current
        // editor values are the configuration that starts monitoring. If that
        // same task is already running, stop only it before restarting.
        if (session.Controller.IsRunning)
        {
            await StopSessionAsync(session);
        }

        SaveSessionConfiguration(session, config, monitoring: false);
        StartSession(session, config, showMessageForEmptyKeyword: true);
    }

    private async void StopSelectedRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUsageAvailable()) return;
        if (GetSelectedSession() is not { } session) return;
        await StopSessionAsync(session);
    }

    private void OpenSelectedRuleBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUsageAvailable()) return;
        if (GetSelectedSession() is not { } session) return;
        OpenTaskBrowser(session);
    }

    private void StartAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUsageAvailable()) return;
        var startedCount = 0;
        foreach (var session in _sessions.Values.OrderBy(item => item.ViewModel.Slot))
        {
            if (session.Controller.IsRunning) continue;
            var config = session.ConfigStore.Load();
            if (string.IsNullOrWhiteSpace(config.Keyword)) continue;
            if (StartSession(session, config, showMessageForEmptyKeyword: false)) startedCount += 1;
        }

        if (startedCount == 0 && !_sessions.Values.Any(session => session.Controller.IsRunning))
        {
            MessageBox.Show(this, "请先在至少一个任务中设置关键词。", "孔夫子商品监控");
        }
    }

    private async void StopAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUsageAvailable()) return;
        var sessions = _sessions.Values.ToArray();
        foreach (var session in sessions)
        {
            session.Controller.Stop();
            session.ViewModel.UpdateStatus("正在停止");
            _notificationService?.CancelRuleNotifications(session.RuleId);
        }

        UpdateSummary();
        await Task.WhenAll(sessions.Select(session => session.Controller.StopAsync()));
        foreach (var session in sessions) RefreshSessionConfiguration(session);
        UpdateSummary();
    }

    private bool StartSession(
        MonitorRuleSession session,
        MonitorConfig config,
        bool showMessageForEmptyKeyword)
    {
        if (!EnsureUsageAvailable()) return false;
        if (session.Controller.IsRunning) return false;
        if (string.IsNullOrWhiteSpace(config.Keyword))
        {
            if (showMessageForEmptyKeyword)
            {
                MessageBox.Show(this, $"请先为 {session.ViewModel.SlotText} 输入关键词。", "孔夫子商品监控");
            }

            return false;
        }

        var normalized = config.Normalize();
        normalized.Monitoring = true;
        session.Controller.Start(normalized);
        RefreshSessionConfiguration(session);
        UpdateSummary();
        return true;
    }

    private async Task StopSessionAsync(MonitorRuleSession session)
    {
        if (_isExpired) return;
        _notificationService?.CancelRuleNotifications(session.RuleId);
        session.ViewModel.UpdateStatus("正在停止");
        UpdateSummary();

        if (session.Controller.IsRunning)
        {
            await session.Controller.StopAsync();
        }
        else
        {
            session.ConfigStore.SetMonitoring(false);
        }

        RefreshSessionConfiguration(session);
        session.ViewModel.UpdateStatus("已停止");
        UpdateSummary();
    }

    private void SaveSessionConfiguration(
        MonitorRuleSession session,
        MonitorConfig config,
        bool monitoring)
    {
        var normalized = config.Normalize();
        normalized.Monitoring = monitoring;
        session.ConfigStore.Save(normalized);
        RefreshSessionConfiguration(session);
    }

    private async Task<bool> HandleCheckoutRequestedAsync(
        MonitorRuleSession session,
        KongfzItem item,
        CancellationToken cancellationToken)
    {
        if (!Dispatcher.CheckAccess())
        {
            var operation = Dispatcher.InvokeAsync(() =>
                HandleCheckoutRequestedAsync(session, item, cancellationToken));
            return await operation.Task.Unwrap();
        }

        if (!EnsureUsageAvailable()) return false;

        // Reuse only this task's existing official result page. This keeps
        // the candidate selection, “立即购买” click and confirmation popup
        // tied to the same task and avoids cross-keyword navigation.
        OpenTaskBrowser(session);
        return await session.CheckoutNavigator.TryOpenForItemAsync(item.ItemUrl, cancellationToken);
    }

    private async Task WaitForManualVerificationAsync(
        MonitorRuleSession session,
        CancellationToken cancellationToken)
    {
        if (!Dispatcher.CheckAccess())
        {
            var operation = Dispatcher.InvokeAsync(() =>
                WaitForManualVerificationAsync(session, cancellationToken));
            await operation.Task.Unwrap();
            return;
        }

        if (!EnsureUsageAvailable()) return;

        OpenTaskBrowser(session);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var verificationRequired = await session.SearchClient.IsCurrentPageVerificationRequiredAsync();
            if (verificationRequired == false) return;

            // This only observes the current task's already-open verification
            // page. It does not submit or bypass any verification step.
            await Task.Delay(500, cancellationToken);
        }
    }

    private void HandleMatchedItem(MonitorRuleSession session, KongfzItem item)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isClosing && !_isExpired)
            {
                _notificationService?.ShowNewItem(session.RuleId, session.ViewModel.SlotText, item);
            }
        }));
    }

    private void HandleSessionStatusChanged(MonitorRuleSession session, string status)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosing || _isExpired) return;

            session.ViewModel.UpdateStatus(status);
            var verificationWaiting = status.StartsWith(
                "监控状态：检测到孔夫子人机验证",
                StringComparison.Ordinal);
            if (verificationWaiting && !session.VerificationAlertActive)
            {
                session.VerificationAlertActive = true;
                SystemSounds.Exclamation.Play();
            }
            else if (!verificationWaiting)
            {
                session.VerificationAlertActive = false;
            }

            RefreshSessionConfiguration(session);
            UpdateSummary();
        }));
    }

    private void HandleSessionRoundCountChanged(MonitorRuleSession session, int roundCount)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosing || _isExpired) return;
            session.ViewModel.UpdateRoundCount(roundCount);
            UpdateSummary();
        }));
    }

    private void OpenTaskBrowser(MonitorRuleSession session)
    {
        BrowserPageTab.IsSelected = true;
        BrowserTabs.SelectedItem = session.BrowserTab;
    }

    private void OpenItemWindow(string itemUrl)
    {
        if (_webViewEnvironment is null) return;

        try
        {
            new ItemBrowserWindow(_webViewEnvironment, itemUrl).Show();
        }
        catch (ArgumentException)
        {
            // Parsed URLs are validated again by the browser window. Invalid
            // data must not result in an arbitrary navigation.
        }
    }

    private MonitorRuleSession? GetSelectedSession()
    {
        return RuleList.SelectedItem is MonitorRuleViewModel ruleViewModel
            ? GetSession(ruleViewModel)
            : null;
    }

    private MonitorRuleSession? GetSessionFromButton(object sender)
    {
        return sender is FrameworkElement { Tag: MonitorRuleViewModel ruleViewModel }
            ? GetSession(ruleViewModel)
            : null;
    }

    private MonitorRuleSession? GetSession(MonitorRuleViewModel ruleViewModel)
    {
        return _sessions.TryGetValue(ruleViewModel.RuleId, out var session) ? session : null;
    }

    private void SelectRuleFromButton(object sender)
    {
        if (sender is not FrameworkElement { Tag: MonitorRuleViewModel ruleViewModel }) return;
        SelectRule(ruleViewModel);
    }

    private void SelectRule(MonitorRuleViewModel ruleViewModel)
    {
        RuleList.SelectedItem = ruleViewModel;
        RuleList.ScrollIntoView(ruleViewModel);
    }

    private void RefreshSessionConfiguration(MonitorRuleSession session)
    {
        try
        {
            var config = session.ConfigStore.Load();
            var rule = session.ViewModel.Rule;
            rule.Apply(config);
            session.ViewModel.UpdateConfiguration(rule);
        }
        catch (InvalidOperationException)
        {
            // A failed configuration refresh must not change any other task.
        }
    }

    private void UpdateSummary()
    {
        if (_isExpired)
        {
            SummaryText.Text = "使用期限已到期，请联系管理员";
            return;
        }

        var running = _sessions.Values.Count(session => session.Controller.IsRunning);
        var waitingForVerification = Rules.Count(rule => rule.StatusText.StartsWith("检测到孔夫子人机验证", StringComparison.Ordinal));
        var pausedForConfirmation = Rules.Count(rule => rule.StatusText.StartsWith("已进入官方下单确认页", StringComparison.Ordinal));
        SummaryText.Text = $"共 5 个独立任务：运行中 {running} 个，等待手动验证 {waitingForVerification} 个，官方确认页暂停 {pausedForConfirmation} 个。";
    }

    private void LoadRuleIntoInputs(MonitorRule rule)
    {
        KeywordInput.Text = rule.Keyword;
        AuthorInput.Text = rule.Author;
        PublisherInput.Text = rule.Publisher;
        MinPriceInput.Text = rule.MinPrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        MaxPriceInput.Text = rule.MaxPrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        IntervalInput.Text = rule.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private bool TryReadConfig(out MonitorConfig config)
    {
        config = new MonitorConfig();
        var keyword = KeywordInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            MessageBox.Show(this, "请输入关键词。", "孔夫子商品监控");
            return false;
        }

        if (!TryReadOptionalPrice(MinPriceInput.Text, "最低价格", out var minPrice) ||
            !TryReadOptionalPrice(MaxPriceInput.Text, "最高价格", out var maxPrice)) return false;

        if (minPrice is double lowerBound && maxPrice is double upperBound && lowerBound > upperBound)
        {
            MessageBox.Show(this, "最低价格不能高于最高价格。", "孔夫子商品监控");
            return false;
        }

        if (!int.TryParse(IntervalInput.Text.Trim(), out var interval) || interval < 1 || interval > 15)
        {
            MessageBox.Show(this, "刷新间隔必须是 1～15 秒。", "孔夫子商品监控");
            return false;
        }

        config = new MonitorConfig
        {
            Keyword = keyword,
            Author = AuthorInput.Text.Trim(),
            Publisher = PublisherInput.Text.Trim(),
            MinPrice = minPrice,
            MaxPrice = maxPrice,
            IntervalSeconds = interval,
            Monitoring = true,
        };
        return true;
    }

    private bool TryReadOptionalPrice(string rawPrice, string displayName, out double? price)
    {
        price = null;
        var text = rawPrice.Trim();
        if (string.IsNullOrEmpty(text)) return true;

        var parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsedPrice) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedPrice);
        if (!parsed || parsedPrice < 0)
        {
            MessageBox.Show(this, $"{displayName}格式不正确。", "孔夫子商品监控");
            return false;
        }

        price = parsedPrice;
        return true;
    }

    private bool EnsureUsageAvailable()
    {
        if (!_isExpired && !UsageExpirationPolicy.HasExpired(DateTime.Now)) return true;

        _ = ExpireApplicationAsync();
        return false;
    }

    private void ArmExpirationTimer()
    {
        var delay = UsageExpirationPolicy.GetTimeUntilExpiration(DateTime.Now);
        _expirationTimer?.Dispose();
        _expirationTimer = new Timer(
            _ =>
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() => _ = ExpireApplicationAsync()));
                }
                catch (InvalidOperationException)
                {
                    // The dispatcher can already be shutting down.
                }
            },
            state: null,
            dueTime: delay,
            period: Timeout.InfiniteTimeSpan);
    }

    private async Task ExpireApplicationAsync()
    {
        if (!Dispatcher.CheckAccess())
        {
            var operation = Dispatcher.InvokeAsync(ExpireApplicationAsync);
            await operation.Task.Unwrap();
            return;
        }

        if (_isExpired || _isClosing) return;

        _isExpired = true;
        _expirationTimer?.Dispose();
        _expirationTimer = null;
        MainTabs.IsEnabled = false;
        UpdateSummary();

        var sessions = _sessions.Values.ToArray();
        foreach (var session in sessions)
        {
            session.Controller.Stop();
            _notificationService?.CancelRuleNotifications(session.RuleId);
        }

        var stopTasks = Task.WhenAll(sessions.Select(session => session.Controller.StopAsync()));
        await Task.WhenAny(stopTasks, Task.Delay(TimeSpan.FromSeconds(2)));

        if (!_expirationNoticeShown)
        {
            _expirationNoticeShown = true;
            MessageBox.Show(
                this,
                "使用期限已到期，请联系管理员",
                "孔夫子商品监控",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        Application.Current?.Shutdown();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _isClosing = true;
        _expirationTimer?.Dispose();
        _expirationTimer = null;
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
        _notificationService?.Dispose();
        LoginWebView.Dispose();
    }
}
}

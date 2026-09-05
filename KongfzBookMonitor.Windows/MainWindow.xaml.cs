using System;
using System.ComponentModel;
using System.Globalization;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace KongfzBookMonitor.Windows
{

public partial class MainWindow : Window
{
    private MonitorConfigStore? _configStore;
    private ProcessedItemStore? _processedItemStore;
    private CoreWebView2Environment? _webViewEnvironment;
    private KongfzSearchClient? _searchClient;
    private MonitorController? _monitorController;
    private WindowsNotificationService? _notificationService;
    private OfficialCheckoutNavigator? _checkoutNavigator;
    private bool _verificationAlertActive;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _configStore = new MonitorConfigStore();
        _processedItemStore = new ProcessedItemStore();
        LoadConfigIntoInputs(_configStore.Load());

        try
        {
            // WebView2 must be attached to the selected tab before its first
            // initialization. Switch back once the embedded browser is ready.
            LoginTab.IsSelected = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);

            _webViewEnvironment = await WebViewEnvironmentFactory.CreateAsync();
            await MonitorWebView.EnsureCoreWebView2Async(_webViewEnvironment);

            _searchClient = new KongfzSearchClient(MonitorWebView);
            _checkoutNavigator = new OfficialCheckoutNavigator(MonitorWebView, _webViewEnvironment);
            _notificationService = new WindowsNotificationService();
            _notificationService.ItemClicked += url => Dispatcher.BeginInvoke(
                new Action(() => OpenItemWindow(url)));

            _monitorController = new MonitorController(
                _configStore,
                _processedItemStore,
                _searchClient);
            _monitorController.StatusChanged += HandleMonitorStatusChanged;
            _monitorController.RoundCountChanged += count => Dispatcher.BeginInvoke(
                new Action(() => RoundCountText.Text = $"已监控轮次：{count}"));
            _monitorController.MatchedItem += HandleMatchedItem;
            _monitorController.CheckoutRequested += HandleCheckoutRequestedAsync;
            _monitorController.VerificationWaitRequested += WaitForManualVerificationAsync;
            _checkoutNavigator.OfficialCheckoutOpened += _monitorController.PauseAfterOfficialCheckoutOpened;

            var config = _configStore.Load();
            StatusText.Text = config.Monitoring ? "监控状态：准备恢复监控" : "监控状态：已停止";
            MainTabs.SelectedIndex = 0;
            if (config.Monitoring && !string.IsNullOrWhiteSpace(config.Keyword))
            {
                _monitorController.Start(config);
            }
        }
        catch (Exception error)
        {
            StatusText.Text = "监控状态：WebView2 初始化失败";
            MessageBox.Show(
                this,
                $"无法初始化 Windows WebView2：{error.Message}",
                "孔夫子商品监控",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenLoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorWebView.CoreWebView2 is null)
        {
            MessageBox.Show(this, "WebView2 正在初始化，请稍后再试。", "孔夫子商品监控");
            return;
        }

        LoginTab.IsSelected = true;
        MonitorWebView.CoreWebView2.Navigate("https://www.kongfz.com/");
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitorController is null || _configStore is null)
        {
            MessageBox.Show(this, "监控组件尚未初始化。", "孔夫子商品监控");
            return;
        }

        if (_monitorController.IsRunning)
        {
            MessageBox.Show(this, "监控已经在运行中。", "孔夫子商品监控");
            return;
        }

        if (!TryReadConfig(out var config)) return;
        _monitorController.Start(config);
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitorController is null) return;

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        StatusText.Text = "监控状态：正在停止";
        try
        {
            await _monitorController.StopAsync();
            StatusText.Text = "监控状态：已停止";
        }
        finally
        {
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = true;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _monitorController?.Dispose();
        _checkoutNavigator?.Dispose();
        _notificationService?.Dispose();
        MonitorWebView.Dispose();
    }

    private void HandleMatchedItem(KongfzItem item)
    {
        _notificationService?.ShowNewItem(item);
    }

    private async Task<bool> HandleCheckoutRequestedAsync(KongfzItem item, CancellationToken cancellationToken)
    {
        if (_checkoutNavigator is null) return false;

        // Reuse the same WebView that rendered and parsed this result page.
        // This avoids opening another product-search window and preserves the
        // current official page state for the exact matching card.
        LoginTab.IsSelected = true;
        return await _checkoutNavigator.TryOpenForItemAsync(item.ItemUrl, cancellationToken);
    }

    private async Task WaitForManualVerificationAsync(CancellationToken cancellationToken)
    {
        if (_searchClient is null) return;

        LoginTab.IsSelected = true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var verificationRequired = await _searchClient.IsCurrentPageVerificationRequiredAsync();
            if (verificationRequired == false) return;

            // This only watches the page the user is already solving. It does
            // not submit, move, or otherwise automate the human verification.
            await Task.Delay(500, cancellationToken);
        }
    }

    private void HandleMonitorStatusChanged(string status)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            StatusText.Text = status;
            var verificationWaiting = status.StartsWith(
                "监控状态：检测到孔夫子人机验证",
                StringComparison.Ordinal);
            if (verificationWaiting && !_verificationAlertActive)
            {
                _verificationAlertActive = true;
                SystemSounds.Exclamation.Play();
            }
            else if (!verificationWaiting)
            {
                _verificationAlertActive = false;
            }
        }));
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

    private void LoadConfigIntoInputs(MonitorConfig config)
    {
        KeywordInput.Text = config.Keyword;
        AuthorInput.Text = config.Author;
        PublisherInput.Text = config.Publisher;
        MinPriceInput.Text = config.MinPrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        MaxPriceInput.Text = config.MaxPrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        IntervalInput.Text = config.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
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
}
}

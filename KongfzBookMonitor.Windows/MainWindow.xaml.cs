using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
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
            _webViewEnvironment = await WebViewEnvironmentFactory.CreateAsync();
            await MonitorWebView.EnsureCoreWebView2Async(_webViewEnvironment);

            _searchClient = new KongfzSearchClient(MonitorWebView);
            _notificationService = new WindowsNotificationService();
            _notificationService.ItemClicked += url => Dispatcher.BeginInvoke(new Action(() => OpenItemWindow(url)));

            _monitorController = new MonitorController(
                _configStore,
                _processedItemStore,
                _searchClient);
            _monitorController.StatusChanged += status => Dispatcher.BeginInvoke(
                new Action(() => StatusText.Text = status));
            _monitorController.MatchedItem += HandleMatchedItem;

            var config = _configStore.Load();
            StatusText.Text = config.Monitoring ? "监控状态：准备恢复监控" : "监控状态：已停止";
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

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _monitorController?.Stop();
        StatusText.Text = "监控状态：已停止";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _monitorController?.Dispose();
        _notificationService?.Dispose();
        MonitorWebView.Dispose();
    }

    private void HandleMatchedItem(KongfzItem item)
    {
        _notificationService?.ShowNewItem(item);
        OpenItemWindow(item.ItemUrl);
    }

    private void OpenItemWindow(string itemUrl)
    {
        if (_webViewEnvironment is null) return;

        try
        {
            new ItemBrowserWindow(_webViewEnvironment, itemUrl, autoCheckout: true).Show();
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
        MaxPriceInput.Text = config.MaxPrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ConditionInput.Text = config.Condition;
        ShopInput.Text = config.Shop;
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

        var maxPriceText = MaxPriceInput.Text.Trim();
        double? maxPrice = null;
        if (!string.IsNullOrEmpty(maxPriceText))
        {
            var parsed = double.TryParse(maxPriceText, NumberStyles.Float, CultureInfo.CurrentCulture, out var localPrice) ||
                double.TryParse(maxPriceText, NumberStyles.Float, CultureInfo.InvariantCulture, out localPrice);
            if (!parsed || localPrice < 0)
            {
                MessageBox.Show(this, "最高价格格式不正确。", "孔夫子商品监控");
                return false;
            }
            maxPrice = localPrice;
        }

        if (!int.TryParse(IntervalInput.Text.Trim(), out var interval) || interval < 5 || interval > 15)
        {
            MessageBox.Show(this, "刷新间隔必须是 5～15 秒。", "孔夫子商品监控");
            return false;
        }

        config = new MonitorConfig
        {
            Keyword = keyword,
            Author = AuthorInput.Text.Trim(),
            Publisher = PublisherInput.Text.Trim(),
            MaxPrice = maxPrice,
            Condition = ConditionInput.Text.Trim(),
            Shop = ShopInput.Text.Trim(),
            IntervalSeconds = interval,
            Monitoring = true,
        };
        return true;
    }
}
}

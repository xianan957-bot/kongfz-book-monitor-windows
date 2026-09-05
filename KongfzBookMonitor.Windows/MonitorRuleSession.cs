using System;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KongfzBookMonitor.Windows
{

/// <summary>
/// The complete runtime chain for one task: configuration, de-duplication,
/// official result page, monitoring loop and official confirmation popup.
/// A session never shares those parts with another task; only the supplied
/// WebView2 environment (and therefore manual login Cookie profile) is shared.
/// </summary>
internal sealed class MonitorRuleSession : IDisposable
{
    public MonitorRuleSession(
        MonitorRule rule,
        MonitorRuleViewModel viewModel,
        MonitorRulesStore rulesStore,
        WebView2 webView,
        TabItem browserTab,
        CoreWebView2Environment webViewEnvironment)
    {
        RuleId = rule.Id;
        ViewModel = viewModel;
        WebView = webView;
        BrowserTab = browserTab;
        ConfigStore = new RuleMonitorConfigStore(rulesStore, RuleId);
        ProcessedItems = new ProcessedItemStore(RuleId, migrateLegacyItems: rule.Slot == 1);
        SearchClient = new KongfzSearchClient(WebView);
        Controller = new MonitorController(ConfigStore, ProcessedItems, SearchClient);
        CheckoutNavigator = new OfficialCheckoutNavigator(
            WebView,
            webViewEnvironment,
            $"{viewModel.SlotText} 孔夫子确认页面");
    }

    public string RuleId { get; }
    public MonitorRuleViewModel ViewModel { get; }
    public WebView2 WebView { get; }
    public TabItem BrowserTab { get; }
    public RuleMonitorConfigStore ConfigStore { get; }
    public ProcessedItemStore ProcessedItems { get; }
    public KongfzSearchClient SearchClient { get; }
    public MonitorController Controller { get; }
    public OfficialCheckoutNavigator CheckoutNavigator { get; }
    public bool VerificationAlertActive { get; set; }

    public void Dispose()
    {
        Controller.Dispose();
        CheckoutNavigator.Dispose();
        WebView.Dispose();
    }
}
}

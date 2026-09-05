using System;
using System.IO;
using System.Linq;
using KongfzBookMonitor.Windows;

namespace KongfzBookMonitor.Windows.Tests
{
internal static class Program
{
    private static int Main()
    {
        try
        {
            KeepsOfficiallyFilteredCardWhenCardMetadataIsMissing();
            ReadsTheListingPriceBeforeFreightText();
            NotifiesOnlyTheLowestPriceChosenForCheckout();
            RetriesOfficialPurchaseOnceAfterManualVerification();
            KeepsFiveRuleConfigurationsIndependent();
            UsesPackagedFixedWebView2RuntimeWhenPresent();
            ExpiresAtTheConfiguredLocalDeadline();
            Console.WriteLine("All regression checks passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void KeepsOfficiallyFilteredCardWhenCardMetadataIsMissing()
    {
        var config = new MonitorConfig
        {
            Keyword = "针灸大成",
            Author = "杨继洲",
            Publisher = "人民卫生出版社",
            MinPrice = 1,
            MaxPrice = 20,
        };
        var lowestPricedOfficialResult = new KongfzItem
        {
            ItemId = "lowest",
            ItemUrl = "https://book.kongfz.com/1/lowest",
            Title = "针灸大成",
            // The official advanced-search URL already applied the author
            // and publisher parameters. A card's optional DOM metadata can
            // still be absent, so it must not hide this valid result.
            Author = string.Empty,
            Publisher = string.Empty,
            Price = 6.0,
        };
        var higherPricedOfficialResult = new KongfzItem
        {
            ItemId = "higher",
            ItemUrl = "https://book.kongfz.com/1/higher",
            Title = "针灸大成",
            Author = "杨继洲",
            Publisher = "人民卫生出版社",
            Price = 15.8,
        };

        var matchingResults = new[] { lowestPricedOfficialResult, higherPricedOfficialResult }
            .Where(item => ItemFilter.Matches(item, config))
            .OrderBy(item => item.Price)
            .ToArray();

        Assert(matchingResults.Length == 2,
            "A valid official-search result was discarded because optional card metadata was missing.");
        Assert(matchingResults[0].ItemId == "lowest",
            "The cheapest listing price was not selected from the matching official-search results.");

        var conflictingMetadata = new KongfzItem
        {
            ItemId = "conflicting",
            ItemUrl = "https://book.kongfz.com/1/conflicting",
            Title = "针灸大成",
            Author = "其他作者",
            Publisher = "人民卫生出版社",
            Price = 6.0,
        };
        Assert(!ItemFilter.Matches(conflictingMetadata, config),
            "Visible metadata that conflicts with the configured author must still be rejected.");
    }

    private static void ReadsTheListingPriceBeforeFreightText()
    {
        var price = KongfzSearchClient.ParseListingPrice("¥6.00 快递 ¥8.00");
        Assert(price == 6.0,
            "The first currency amount on a result card must be read as the listing price, not discarded with freight text.");
    }

    private static void NotifiesOnlyTheLowestPriceChosenForCheckout()
    {
        var seventeenYuanNewItem = new KongfzItem
        {
            ItemId = "new-17",
            ItemUrl = "https://book.kongfz.com/1/new-17",
            Title = "针灸大成",
            Price = 17.0,
        };
        var fifteenYuanNewItem = new KongfzItem
        {
            ItemId = "new-15",
            ItemUrl = "https://book.kongfz.com/1/new-15",
            Title = "针灸大成",
            Price = 15.8,
        };
        var sixYuanLowestCurrentItem = new KongfzItem
        {
            ItemId = "current-6",
            ItemUrl = "https://book.kongfz.com/1/current-6",
            Title = "针灸大成",
            Price = 6.0,
        };

        var notificationItems = MonitorController.NotificationItemsForRound(
            new[] { seventeenYuanNewItem, fifteenYuanNewItem },
            new[] { seventeenYuanNewItem, fifteenYuanNewItem, sixYuanLowestCurrentItem });

        Assert(notificationItems.Count == 1,
            "A monitoring round must produce only one notification, not one for every matching card.");
        Assert(notificationItems[0].ItemId == sixYuanLowestCurrentItem.ItemId,
            "The notification must describe the same lowest-price item selected for official checkout.");
    }

    private static void RetriesOfficialPurchaseOnceAfterManualVerification()
    {
        Assert(MonitorController.ShouldRequestCheckoutForRound(
                newlyMatchedItemCount: 0,
                retryCheckoutAfterVerification: true),
            "After a user completes verification, the current result page must get one official purchase retry even when its items were already processed.");
        Assert(!MonitorController.ShouldRequestCheckoutForRound(
                newlyMatchedItemCount: 0,
                retryCheckoutAfterVerification: false),
            "Normal monitoring must still wait for a genuinely new matching item.");
    }

    private static void KeepsFiveRuleConfigurationsIndependent()
    {
        var configuredSlots = new MonitorRuleSettings
        {
            Rules = new[]
            {
                new MonitorRule
                {
                    Id = "rule-one",
                    Slot = 1,
                    Keyword = "针灸大成",
                    Author = "杨继洲",
                    MinPrice = 1,
                    MaxPrice = 20,
                    IntervalSeconds = 1,
                    Monitoring = true,
                },
                new MonitorRule
                {
                    Id = "rule-two",
                    Slot = 2,
                    Keyword = "鲁迅全集",
                    Publisher = "人民文学出版社",
                    MinPrice = 30,
                    MaxPrice = 80,
                    IntervalSeconds = 15,
                    Monitoring = false,
                },
            }.ToList(),
        }.Normalize();

        Assert(configuredSlots.Rules.Count == 5,
            "The desktop monitor must always expose exactly five fixed task slots.");
        Assert(configuredSlots.Rules.Select(rule => rule.Slot).Distinct().Count() == 5,
            "Each fixed task slot must remain unique after configuration normalization.");
        Assert(configuredSlots.Rules.Select(rule => rule.Id).Distinct().Count() == 5,
            "Each task must have its own persistent identity for separate de-duplication.");

        var first = configuredSlots.Rules.Single(rule => rule.Slot == 1).ToMonitorConfig();
        var second = configuredSlots.Rules.Single(rule => rule.Slot == 2).ToMonitorConfig();
        Assert(first.Keyword == "针灸大成" && first.Author == "杨继洲" && first.MinPrice == 1 && first.MaxPrice == 20 && first.IntervalSeconds == 1,
            "The first task's single-keyword filter chain changed during multi-task normalization.");
        Assert(second.Keyword == "鲁迅全集" && second.Publisher == "人民文学出版社" && second.MinPrice == 30 && second.MaxPrice == 80 && second.IntervalSeconds == 15,
            "The second task's filter chain was mixed with another keyword's settings.");

        first.Keyword = "已修改的任务一";
        Assert(second.Keyword == "鲁迅全集",
            "Mutating one task's monitor config must not change another task's keyword.");
    }

    private static void ExpiresAtTheConfiguredLocalDeadline()
    {
        var deadline = UsageExpirationPolicy.ExpirationLocalTime;
        Assert(deadline == new DateTime(2026, 9, 30, 23, 59, 59, DateTimeKind.Local),
            "The requested local usage deadline changed unexpectedly.");
        Assert(!UsageExpirationPolicy.HasExpired(deadline.AddTicks(-1)),
            "The app must remain usable immediately before the deadline.");
        Assert(UsageExpirationPolicy.HasExpired(deadline),
            "The app must stop at the exact configured deadline.");
        Assert(UsageExpirationPolicy.HasExpired(deadline.AddSeconds(1)),
            "The app must remain unavailable after the deadline.");
        Assert(UsageExpirationPolicy.GetTimeUntilExpiration(deadline.AddTicks(-1)) == TimeSpan.FromTicks(1),
            "The expiration timer did not calculate the final pre-deadline interval correctly.");
    }

    private static void UsesPackagedFixedWebView2RuntimeWhenPresent()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"kongfz-webview2-test-{Guid.NewGuid():N}");
        var runtimeDirectory = Path.Combine(temporaryRoot, WebViewEnvironmentFactory.FixedRuntimeDirectoryName);

        try
        {
            Directory.CreateDirectory(runtimeDirectory);
            File.WriteAllText(Path.Combine(runtimeDirectory, "msedgewebview2.exe"), string.Empty);

            Assert(WebViewEnvironmentFactory.ResolvePackagedFixedRuntimePath(temporaryRoot) == runtimeDirectory,
                "The packaged fixed WebView2 Runtime was not selected when its executable was present.");

            File.Delete(Path.Combine(runtimeDirectory, "msedgewebview2.exe"));
            var versionedRuntimeDirectory = Path.Combine(runtimeDirectory, "Microsoft.WebView2.FixedVersionRuntime.test.x64");
            Directory.CreateDirectory(versionedRuntimeDirectory);
            File.WriteAllText(Path.Combine(versionedRuntimeDirectory, "msedgewebview2.exe"), string.Empty);
            Assert(WebViewEnvironmentFactory.ResolvePackagedFixedRuntimePath(temporaryRoot) == versionedRuntimeDirectory,
                "The version-named directory produced by the official fixed-runtime CAB was not selected.");

            Assert(WebViewEnvironmentFactory.ResolvePackagedFixedRuntimePath(Path.Combine(temporaryRoot, "missing")) is null,
                "A missing packaged WebView2 Runtime must not be treated as usable.");
            Assert(WebViewEnvironmentFactory.RequiresWindows10FixedRuntimeAccess(new Version(10, 0, 19045)),
                "Windows 10 must receive the fixed WebView2 Runtime access configuration.");
            Assert(!WebViewEnvironmentFactory.RequiresWindows10FixedRuntimeAccess(new Version(10, 0, 22631)),
                "Windows 11 must not need the Windows 10 fixed-runtime access configuration.");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
}

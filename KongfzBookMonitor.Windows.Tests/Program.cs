using System;
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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
}

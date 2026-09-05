using System;

namespace KongfzBookMonitor.Windows
{

public static class ItemFilter
{
    public static bool Matches(KongfzItem item, MonitorConfig config)
    {
        var keyword = config.Keyword.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            var searchable = string.Join(
                " ",
                item.Title,
                item.Author,
                item.Publisher);
            if (!ContainsText(searchable, keyword)) return false;
        }

        var expectedAuthor = config.Author.Trim();
        // Author and publisher are already submitted to Kongfz's official
        // advanced-search URL. Some result cards omit their optional metadata
        // nodes, so a missing value cannot disprove that server-side filter.
        // A value that is present must still agree with the configured filter.
        if (!string.IsNullOrEmpty(expectedAuthor) &&
            !string.IsNullOrWhiteSpace(item.Author) &&
            !ContainsText(item.Author, expectedAuthor))
        {
            return false;
        }

        var expectedPublisher = config.Publisher.Trim();
        if (!string.IsNullOrEmpty(expectedPublisher) &&
            !string.IsNullOrWhiteSpace(item.Publisher) &&
            !ContainsText(item.Publisher, expectedPublisher))
        {
            return false;
        }

        if (config.MinPrice is double minPrice)
        {
            if (item.Price is not double minimumPriceCandidate || minimumPriceCandidate < minPrice) return false;
        }

        if (config.MaxPrice is double maxPrice)
        {
            if (item.Price is not double maximumPriceCandidate || maximumPriceCandidate > maxPrice) return false;
        }

        return true;
    }

    private static bool ContainsText(string actual, string expected)
    {
        return actual?.Trim().IndexOf(expected.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
}

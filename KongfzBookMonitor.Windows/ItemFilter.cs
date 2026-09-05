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
                item.Publisher,
                item.Condition,
                item.Shop);
            if (!ContainsText(searchable, keyword)) return false;
        }

        var expectedAuthor = config.Author.Trim();
        if (!string.IsNullOrEmpty(expectedAuthor) && !ContainsText(item.Author, expectedAuthor))
        {
            return false;
        }

        var expectedPublisher = config.Publisher.Trim();
        if (!string.IsNullOrEmpty(expectedPublisher) && !ContainsText(item.Publisher, expectedPublisher))
        {
            return false;
        }

        if (config.MaxPrice is double maxPrice)
        {
            if (item.Price is not double price || price > maxPrice) return false;
        }

        var expectedShop = config.Shop.Trim();
        if (!string.IsNullOrEmpty(expectedShop) &&
            !string.Equals(item.Shop.Trim(), expectedShop, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedCondition = config.Condition.Trim();
        if (!string.IsNullOrEmpty(expectedCondition) && !ConditionMatches(item.Condition, expectedCondition))
        {
            return false;
        }

        return true;
    }

    private static bool ContainsText(string actual, string expected)
    {
        return actual?.Trim().IndexOf(expected.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool ConditionMatches(string actual, string expected)
    {
        var actualNormalized = NormalizeCondition(actual);
        var expectedNormalized = NormalizeCondition(expected);

        const string suffix = "以上";
        if (expectedNormalized.EndsWith(suffix, StringComparison.Ordinal))
        {
            var actualGrade = ParseGrade(actualNormalized);
            var thresholdGrade = ParseGrade(expectedNormalized[..^suffix.Length]);
            if (actualGrade is not null && thresholdGrade is not null)
            {
                return actualGrade >= thresholdGrade;
            }
        }

        return string.Equals(actualNormalized, expectedNormalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCondition(string value)
    {
        return (value ?? string.Empty).Replace(" ", string.Empty).Replace("　", string.Empty);
    }

    private static double? ParseGrade(string value)
    {
        if (value.Contains("全新", StringComparison.Ordinal)) return 10.0;

        return value.TrimEnd('品') switch
        {
            "十" or "10" => 10.0,
            "九五" or "9.5" or "95" => 9.5,
            "九" or "9" => 9.0,
            "八五" or "8.5" or "85" => 8.5,
            "八" or "8" => 8.0,
            "七五" or "7.5" or "75" => 7.5,
            "七" or "7" => 7.0,
            "六五" or "6.5" or "65" => 6.5,
            "六" or "6" => 6.0,
            "五五" or "5.5" or "55" => 5.5,
            "五" or "5" => 5.0,
            _ => null,
        };
    }
}
}

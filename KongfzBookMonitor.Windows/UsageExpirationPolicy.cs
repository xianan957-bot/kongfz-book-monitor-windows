using System;

namespace KongfzBookMonitor.Windows
{

/// <summary>
/// Local, offline usage deadline. The comparison intentionally uses the
/// Windows local clock because the requested deadline is a local calendar
/// timestamp rather than a server-issued UTC license.
/// </summary>
internal static class UsageExpirationPolicy
{
    internal static readonly DateTime ExpirationLocalTime = new(
        2026,
        9,
        30,
        23,
        59,
        59,
        DateTimeKind.Local);

    internal static bool HasExpired(DateTime localNow)
    {
        return localNow >= ExpirationLocalTime;
    }

    internal static TimeSpan GetTimeUntilExpiration(DateTime localNow)
    {
        return HasExpired(localNow)
            ? TimeSpan.Zero
            : ExpirationLocalTime - localNow;
    }
}
}

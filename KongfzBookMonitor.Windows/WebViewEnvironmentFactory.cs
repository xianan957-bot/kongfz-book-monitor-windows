using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace KongfzBookMonitor.Windows
{

internal static class WebViewEnvironmentFactory
{
    public static Task<CoreWebView2Environment> CreateAsync()
    {
        var profilePath = Path.Combine(AppDataPaths.Root, "WebView2Profile");
        Directory.CreateDirectory(profilePath);
        return CoreWebView2Environment.CreateAsync(userDataFolder: profilePath);
    }
}
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace KongfzBookMonitor.Windows
{

internal static class WebViewEnvironmentFactory
{
    internal const string FixedRuntimeDirectoryName = "WebView2FixedRuntime";

    public static Task<CoreWebView2Environment> CreateAsync()
    {
        var profilePath = Path.Combine(AppDataPaths.Root, "WebView2Profile");
        Directory.CreateDirectory(profilePath);

        var fixedRuntimePath = ResolvePackagedFixedRuntimePath(AppContext.BaseDirectory);
        if (fixedRuntimePath is not null)
        {
            EnsureFixedRuntimeCanRunOnThisDevice(fixedRuntimePath);
            return CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: fixedRuntimePath,
                userDataFolder: profilePath);
        }

        return CoreWebView2Environment.CreateAsync(userDataFolder: profilePath);
    }

    internal static string? ResolvePackagedFixedRuntimePath(string applicationBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(applicationBaseDirectory))
        {
            throw new ArgumentException("应用程序目录不能为空。", nameof(applicationBaseDirectory));
        }

        var runtimeDirectory = Path.Combine(applicationBaseDirectory, FixedRuntimeDirectoryName);
        if (File.Exists(Path.Combine(runtimeDirectory, "msedgewebview2.exe"))) return runtimeDirectory;

        if (!Directory.Exists(runtimeDirectory)) return null;

        // Microsoft distributes fixed runtimes in a version-named top-level
        // folder when the CAB is expanded with the documented expand.exe flow.
        return Directory.EnumerateDirectories(runtimeDirectory)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "msedgewebview2.exe")));
    }

    internal static bool RequiresWindows10FixedRuntimeAccess(Version osVersion)
    {
        return osVersion.Major == 10 && osVersion.Build < 22000;
    }

    private static void EnsureFixedRuntimeCanRunOnThisDevice(string fixedRuntimePath)
    {
        if (fixedRuntimePath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "随程序提供的 WebView2 运行时不能从网络共享目录运行，请先将整个软件文件夹复制到本机磁盘。");
        }

        // Fixed WebView2 Runtime 120+ needs these read/execute permissions for
        // unpackaged apps on Windows 10. Windows 11 does not require them.
        if (!RequiresWindows10FixedRuntimeAccess(Environment.OSVersion.Version)) return;

        var runtimeExecutable = Path.Combine(fixedRuntimePath, "msedgewebview2.exe");
        var markerPath = Path.Combine(AppDataPaths.Root, "webview2-fixed-runtime-access.txt");
        var markerContent = $"{fixedRuntimePath}\n{File.GetLastWriteTimeUtc(runtimeExecutable).Ticks}";

        try
        {
            if (File.Exists(markerPath) && File.ReadAllText(markerPath) == markerContent) return;

            GrantRuntimeAccess(fixedRuntimePath, "S-1-15-2-2"); // ALL RESTRICTED APPLICATION PACKAGES
            GrantRuntimeAccess(fixedRuntimePath, "S-1-15-2-1"); // ALL APPLICATION PACKAGES
            File.WriteAllText(markerPath, markerContent);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                "无法准备随程序提供的 WebView2 运行时。请确认已完整解压软件文件夹，并且当前 Windows 用户对该文件夹有修改权限。",
                error);
        }
    }

    private static void GrantRuntimeAccess(string fixedRuntimePath, string securityIdentifier)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "icacls.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add(fixedRuntimePath);
        startInfo.ArgumentList.Add("/grant");
        startInfo.ArgumentList.Add($"*{securityIdentifier}:(OI)(CI)(RX)");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Windows 权限配置程序。");
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd().Trim();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"icacls.exe 返回错误代码 {process.ExitCode}。"
                    : error);
        }
    }
}
}

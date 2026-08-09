using System.Diagnostics;

namespace Korat.Cli.Util;

internal static class BrowserLauncher
{
    public static void TryOpen(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
                return;
            }

            Process.Start("xdg-open", url);
        }
        catch
        {
            // Best-effort only; URL is always printed separately.
        }
    }

    public static string BuildApproveUrl(string cloudUrl, string requestId)
    {
        // SPA route is `/approve/$requestId` served under the `/app` basepath (see
        // apps/Korat.App/src/routes/approve.$requestId.tsx). The old `/space/approve.html`
        // path matched no route and the server returned it as a file → the browser
        // downloaded approve.html instead of rendering. requestId is a path segment.
        var baseUri = cloudUrl.TrimEnd('/');
        return $"{baseUri}/app/approve/{Uri.EscapeDataString(requestId)}";
    }
}

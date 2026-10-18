using Microsoft.AspNetCore.Mvc.Testing;

namespace Korat.Cloud.IntegrationTests;

internal static class ForwardedTestHost
{
    // This assembly runs sequentially. Program reads these settings before the factory's
    // ConfigureAppConfiguration callbacks, so set them only for host construction.
    public static WebApplicationFactory<Program> Create(KoratTestHost source, bool enabled = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["KORAT__CLOUD__TRUSTFORWARDEDIP"] = enabled.ToString(),
            ["KORAT__CLOUD__TRUSTEDPROXIES"] = "10.42.0.0/16",
            ["GitHubAuth__ClientId"] = "proxy-test-client",
            ["GitHubAuth__ClientSecret"] = "proxy-test-not-a-secret",
        };
        var previous = settings.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        WebApplicationFactory<Program>? host = null;
        try
        {
            foreach (var (key, value) in settings) Environment.SetEnvironmentVariable(key, value);
            host = source.WithWebHostBuilder(_ => { });
            using var client = host.CreateClient();
            return host;
        }
        catch
        {
            host?.Dispose();
            throw;
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
        }
    }
}

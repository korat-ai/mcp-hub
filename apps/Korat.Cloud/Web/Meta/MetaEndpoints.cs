using System.Reflection;

namespace Korat.Cloud.Web.Meta;

/// <summary>
/// Build/runtime metadata for the console "version" footer. Authenticated-only — instance
/// identifiers (Fly machine/region/image) are operational info, not for anonymous callers.
/// </summary>
public static class MetaEndpoints
{
    public static void MapMetaEndpoints(this WebApplication app)
    {
        var route = app.MapGet("/api/version", (IWebHostEnvironment env) =>
        {
            static string? Env(string key) => Environment.GetEnvironmentVariable(key);

            // git sha: explicit build env first, else the "+<sha>" the .NET SDK appends to the
            // informational version when built inside a git repo, else unknown.
            var commit = Env("KORAT_GIT_SHA");
            if (string.IsNullOrWhiteSpace(commit))
            {
                var informational = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (informational is not null && informational.Contains('+'))
                    commit = informational.Split('+', 2)[1];
            }

            return Results.Ok(new
            {
                commit = string.IsNullOrWhiteSpace(commit) ? "unknown" : commit,
                environment = env.EnvironmentName,
                region = Env("FLY_REGION"),
                machineId = Env("FLY_MACHINE_ID"),
                imageRef = Env("FLY_IMAGE_REF"),
                serverTimeUtc = DateTimeOffset.UtcNow,
            });
        });

        // Authenticated-only (session/CLI via IAuthResolver).
        RequireAuthExtensions.RequireSpaceOwner(route);
    }
}

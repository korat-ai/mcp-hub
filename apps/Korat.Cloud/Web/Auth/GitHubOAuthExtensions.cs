using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace Korat.Cloud.Web.Auth;

public static class GitHubOAuthExtensions
{
    public const string Scheme = "GitHub";

    public static AuthenticationBuilder AddGitHubOAuth(this AuthenticationBuilder builder, Action<OAuthOptions> configure)
    {
        return builder.AddOAuth(Scheme, options =>
        {
            options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
            options.TokenEndpoint = "https://github.com/login/oauth/access_token";
            options.UserInformationEndpoint = "https://api.github.com/user";
            options.CallbackPath = "/signin/github/callback";
            options.UsePkce = true;
            // The OAuth correlation cookie must survive GitHub's CROSS-SITE redirect back to
            // our callback. The ASP.NET default does not reliably round-trip cross-site here
            // (observed live: "Correlation failed" with the correlation cookie absent from the
            // callback request). SameSite=None (which requires Secure) lets the browser send it
            // on the top-level cross-site GET from github.com.
            options.CorrelationCookie.SameSite = SameSiteMode.None;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Scope.Add("read:user");
            options.Scope.Add("user:email");
            options.SaveTokens = false;

            // Map basic profile claims.
            options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
            options.ClaimActions.MapJsonKey(ClaimTypes.Name, "login");

            // After token exchange, fetch /user and /user/emails to get the verified primary email.
            options.Events.OnCreatingTicket = async ctx =>
            {
                var userRequest = new HttpRequestMessage(HttpMethod.Get, ctx.Options.UserInformationEndpoint);
                userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken!);
                userRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                userRequest.Headers.UserAgent.ParseAdd("Korat-Cloud/1.0");
                using var userResp = await ctx.Backchannel.SendAsync(userRequest, HttpCompletionOption.ResponseHeadersRead, ctx.HttpContext.RequestAborted);
                userResp.EnsureSuccessStatusCode();
                using var userDoc = JsonDocument.Parse(await userResp.Content.ReadAsStringAsync(ctx.HttpContext.RequestAborted));
                ctx.RunClaimActions(userDoc.RootElement);

                var emailsRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/emails");
                emailsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken!);
                emailsRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                emailsRequest.Headers.UserAgent.ParseAdd("Korat-Cloud/1.0");
                using var emailsResp = await ctx.Backchannel.SendAsync(emailsRequest, HttpCompletionOption.ResponseHeadersRead, ctx.HttpContext.RequestAborted);
                emailsResp.EnsureSuccessStatusCode();
                using var emailsDoc = JsonDocument.Parse(await emailsResp.Content.ReadAsStringAsync(ctx.HttpContext.RequestAborted));

                foreach (var entry in emailsDoc.RootElement.EnumerateArray())
                {
                    if (entry.GetProperty("primary").GetBoolean() && entry.GetProperty("verified").GetBoolean())
                    {
                        var email = entry.GetProperty("email").GetString();
                        if (!string.IsNullOrEmpty(email))
                        {
                            ctx.Identity!.AddClaim(new Claim(ClaimTypes.Email, email));
                            ctx.Identity.AddClaim(new Claim("email_verified", "true"));
                        }
                        break;
                    }
                }
            };

            options.Events.OnRemoteFailure = ctx =>
            {
                var log = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GitHubOAuth");
                log.LogWarning("GitHub federation failed: {Failure}", ctx.Failure?.Message);
                ctx.Response.Redirect("/app/signin?error=github");
                ctx.HandleResponse();
                return Task.CompletedTask;
            };

            configure(options);
        });
    }
}

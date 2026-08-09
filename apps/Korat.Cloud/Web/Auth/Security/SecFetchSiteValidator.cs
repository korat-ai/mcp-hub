namespace Korat.Cloud.Web.Auth.Security;

/// <summary>
/// Validates that an OAuth callback arrived as a cross-site top-level GET (real IdP redirect),
/// not as a same-origin request (potential CSRF / spoofing attempt). None-set Sec-Fetch-Site
/// is treated as legitimate to accommodate pre-Sec-Fetch-Metadata browsers.
/// </summary>
public static class SecFetchSiteValidator
{
    public static bool IsLegitimateCallback(HttpContext ctx)
    {
        var sfs = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        // OAuth callbacks always arrive cross-site (from the IdP). Same-origin or none-set
        // callback is suspect — but none-set means a pre-Sec-Fetch-Metadata browser, so accept those.
        return string.IsNullOrEmpty(sfs) || sfs == "cross-site";
    }
}

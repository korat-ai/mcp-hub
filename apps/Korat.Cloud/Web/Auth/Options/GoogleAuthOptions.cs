namespace Korat.Cloud.Web.Auth.Options;

public sealed class GoogleAuthOptions
{
    public const string SectionName = "GoogleAuth";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

namespace Korat.Cloud.Web.Auth.Options;

public sealed class GitHubAuthOptions
{
    public const string SectionName = "GitHubAuth";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

namespace EucLens.Core;

/// <summary>
/// The only addresses EUC Lens opens in the browser: pages of its own repository on github.com and the author's website, both over HTTPS. A link built from anything else (for example a reply from a server) is refused.
/// </summary>
public static class SafeLinks
{
    public static bool IsOfficial(Uri? uri) => IsOfficial(uri, AppIdentity.Current.RepositoryUrl) || IsOfficialWebsite(uri, AppIdentity.Current.Website);

    /// <summary>The configured website's host exactly (no subdomain, no other port, no user information), any page on it, over HTTPS.</summary>
    public static bool IsOfficialWebsite(Uri? uri, string websiteUrl) =>
        uri is { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true } && string.IsNullOrEmpty(uri.UserInfo)
        && Uri.TryCreate(websiteUrl, UriKind.Absolute, out var site) && site.Scheme == Uri.UriSchemeHttps && string.Equals(uri.Host, site.Host, StringComparison.OrdinalIgnoreCase);

    public static bool IsOfficial(Uri? uri, string repositoryUrl)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return false;
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repository) || repository.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.Equals(uri.Host, repository.Host, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var root = repository.AbsolutePath.TrimEnd('/');
        return uri.AbsolutePath.Equals(root, StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EucLens.Core;

/// <summary>
/// Asks GitHub's public release list whether a newer EUC Lens exists. This is the only code in the application that opens a network connection, and it runs only when the
/// user asks (nothing calls it on startup or when a page opens).
/// <list type="bullet">
/// <item>One HTTPS GET to <c>api.github.com</c> for this repository's releases. No credentials, cookies, identifiers, machine or user names, and no data of the session are sent;
/// the request carries the same generic user agent for everybody ("EucLens/&lt;version&gt;").</item>
/// <item>Redirects are followed by hand, at most three, and only to the same API host over HTTPS; anything else ends the check as <see cref="UpdateOutcome.UnsafeRedirect"/>.</item>
/// <item>Bounded: an overall timeout (15 s), a response limit (1 MiB counted after decompression), JSON depth and element limits.</item>
/// <item>The reply is data, not instructions. Only the fields below are read and each is validated; the release page address is rebuilt from the validated tag; text is reduced to plain, bounded text. Nothing is downloaded or run.</item>
/// <item>The system proxy and certificate trust are used as configured: a certificate problem ends the check, it is never ignored.</item>
/// </list>
/// </summary>
public sealed partial class GitHubUpdateChecker : IUpdateChecker, IDisposable
{
    public const int MaximumResponseBytes = 1 << 20;
    public const int MaximumRedirects = 3;
    public const int MaximumReleases = 50;
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(15);
    internal const string ApiHost = "api.github.com";

    private readonly HttpClient http;
    private readonly string owner;
    private readonly string name;
    private readonly string repositoryPage;
    private readonly TimeSpan timeout;
    private readonly TimeProvider time;

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,100}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <summary>
    /// The release asset convention: the portable Windows x64 executable of a release is published under exactly this name (case-sensitive, nothing before or after it).
    /// Tags, source archives, checksum files and every other name are not Windows builds and are never offered. Documented in docs/ABOUT_AND_UPDATES.md.
    /// </summary>
    public const string WindowsAssetName = "EucLens-win-x64.exe";

    [GeneratedRegex(@"\[([^\]]{1,200})\]\([^)]{0,500}\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex MarkdownLink();

    /// <param name="handler">Tests pass a fake handler; the application passes none and gets the production handler.</param>
    /// <param name="repositoryUrl">https://github.com/owner/name; the application's own repository by default.</param>
    public GitHubUpdateChecker(HttpMessageHandler? handler = null, string? repositoryUrl = null, TimeSpan? timeout = null, TimeProvider? time = null)
    {
        repositoryUrl ??= AppIdentity.Current.RepositoryUrl;
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repository) || repository.Scheme != Uri.UriSchemeHttps || repository.Host != "github.com")
            throw new ArgumentException("The repository must be an https://github.com/<owner>/<name> address.", nameof(repositoryUrl));
        var parts = repository.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || !NamePattern().IsMatch(parts[0]) || !NamePattern().IsMatch(parts[1])) throw new ArgumentException("The repository address has no owner and name.", nameof(repositoryUrl));
        (owner, name) = (parts[0], parts[1]);
        repositoryPage = $"https://github.com/{owner}/{name}";
        this.timeout = timeout ?? DefaultTimeout;
        this.time = time ?? TimeProvider.System;
        http = new HttpClient(handler ?? CreateProductionHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The system proxy (with the user's own Windows credentials for it, as a browser does), the operating system's certificate trust, no cookies, no automatic redirects.</summary>
    private static SocketsHttpHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = true,
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        MaxResponseHeadersLength = 32,
    };

    public void Dispose() => http.Dispose();

    public async Task<UpdateCheckResult> CheckAsync(ReleaseVersion installed, bool includePrereleases, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try { return await RunAsync(installed, includePrereleases, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Result(UpdateOutcome.Cancelled, "The update check was cancelled.", installed); }
        catch (OperationCanceledException) { return Result(UpdateOutcome.TimedOut, "GitHub did not answer in time. Try again later; EUC Lens keeps working.", installed); }
        catch (UnsafeRedirectException) { return Result(UpdateOutcome.UnsafeRedirect, "The answer pointed somewhere other than GitHub's release service, so it was ignored.", installed); }
        catch (InvalidResponseException) { return Result(UpdateOutcome.InvalidResponse, "GitHub's answer could not be understood, so it was ignored.", installed); }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException || ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return Result(UpdateOutcome.SecureConnectionFailed, "The secure connection to GitHub could not be verified (certificate or proxy policy). Nothing was changed.", installed);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Net.Sockets.SocketException or InvalidOperationException or UriFormatException or NotSupportedException)
        {
            // A reply is never quoted: not the exception text, not the response.
            return Result(UpdateOutcome.Offline, "GitHub could not be reached. Check the network connection, proxy or firewall. EUC Lens works the same offline.", installed);
        }
    }

    private async Task<UpdateCheckResult> RunAsync(ReleaseVersion installed, bool includePrereleases, CancellationToken token)
    {
        var uri = new Uri($"https://{ApiHost}/repos/{owner}/{name}/releases?per_page=30");
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("EucLens", AppVersion.Current));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (hop >= MaximumRedirects || response.Headers.Location is not { } location) throw new UnsafeRedirectException();
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!IsOfficialApiAddress(next)) throw new UnsafeRedirectException();
                uri = next;
                continue;
            }
            return await InterpretAsync(response, installed, includePrereleases, token).ConfigureAwait(false);
        }
    }

    /// <summary>HTTPS, the API host exactly, the default port, no user information, and a repository path.</summary>
    internal static bool IsOfficialApiAddress(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && string.Equals(uri.Host, ApiHost, StringComparison.OrdinalIgnoreCase)
        && (uri.AbsolutePath.StartsWith("/repos/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/repositories/", StringComparison.Ordinal));

    private async Task<UpdateCheckResult> InterpretAsync(HttpResponseMessage response, ReleaseVersion installed, bool includePrereleases, CancellationToken token)
    {
        var status = (int)response.StatusCode;
        if (status == 404) return Result(UpdateOutcome.NoPublicRelease, "No public release has been published yet.", installed);
        if (status == 429 || (status == 403 && (response.Headers.Contains("Retry-After") || HeaderValue(response, "x-ratelimit-remaining") == "0")))
            return Result(UpdateOutcome.RateLimited, "GitHub's request limit was reached." + RetryHint(response) + " EUC Lens keeps working.", installed);
        if (status >= 500) return Result(UpdateOutcome.ServiceUnavailable, "GitHub's release service is not available right now. Try again later.", installed);
        if (status != 200) return Result(UpdateOutcome.InvalidResponse, "GitHub answered in a way EUC Lens does not recognise, so the answer was ignored.", installed);
        var media = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!media.Contains("json", StringComparison.OrdinalIgnoreCase)) throw new InvalidResponseException();
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes) throw new InvalidResponseException();
        var body = await ReadLimitedAsync(response.Content, token).ConfigureAwait(false);
        return Interpret(body, installed, includePrereleases);
    }

    private static string? HeaderValue(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private string RetryHint(HttpResponseMessage response)
    {
        // Only a plausible reset time within the next day is shown, formatted by the program, never copied from the reply.
        if (!long.TryParse(HeaderValue(response, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1_000_000_000 or > 99_999_999_999) return "";
        var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var now = time.GetUtcNow();
        return reset > now && reset - now <= TimeSpan.FromDays(1) ? $" It can be tried again after {TimeZoneInfo.ConvertTime(reset, time.LocalTimeZone):t}." : "";
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes) throw new InvalidResponseException();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Validates the JSON reply and decides the outcome.</summary>
    internal UpdateCheckResult Interpret(ReadOnlySpan<byte> json, ReleaseVersion installed, bool includePrereleases)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException) { throw new InvalidResponseException(); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidResponseException();
            var count = 0;
            var candidates = new List<Candidate>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (++count > MaximumReleases) break;
                if (TryReadCandidate(element) is { } candidate) candidates.Add(candidate);
            }
            if (count == 0) return Result(UpdateOutcome.NoPublicRelease, "No public release has been published yet.", installed);
            if (candidates.Count == 0) throw new InvalidResponseException();
            var channel = candidates.Where(c => includePrereleases || !c.IsPrerelease).ToList();
            if (channel.Count == 0)
                return Result(UpdateOutcome.NoPublicRelease, "No full release has been published yet. Pre-releases are only offered when you include them.", installed);
            var newest = channel.MaxBy(c => c.Version)!;
            var newestWithBuild = channel.Where(c => c.HasWindowsBuild).MaxBy(c => c.Version);
            if (newestWithBuild is not null && newestWithBuild.Version > installed)
                return new(UpdateOutcome.UpdateAvailable, $"Version {newestWithBuild.Version} is available.", installed, newestWithBuild.ToRelease(repositoryPage));
            if (newest.Version > installed)
                return Result(UpdateOutcome.NoCompatibleBuild, $"Version {newest.Version} is listed on GitHub but has no Windows download attached yet, so there is nothing to offer.", installed);
            return Result(UpdateOutcome.UpToDate, newest.Version.Equals(installed) ? $"You have the latest version ({installed})." : $"This version ({installed}) is newer than the latest published release ({newest.Version}).", installed);
        }
    }

    private Candidate? TryReadCandidate(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (Bool(element, "draft") == true) return null;
        var tag = String(element, "tag_name", 64);
        if (tag is null || !ReleaseVersion.TryParse(tag, out var version)) return null;
        // The tag is the only part of the reply that reaches an address, and it has passed the strict version pattern: digits, letters, ".", "-", "+" and an optional leading "v".
        var prerelease = Bool(element, "prerelease") == true || version.IsPrerelease;
        var title = Plain(String(element, "name", 200) ?? "", 100, 1);
        if (title.Length == 0) title = $"EUC Lens {version}";
        DateTimeOffset? published = null;
        if (String(element, "published_at", 40) is { } when_ && DateTimeOffset.TryParse(when_, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            && parsed.Year >= 2008 && parsed <= time.GetUtcNow().AddDays(1)) published = parsed;
        var summary = Plain(String(element, "body", 100_000) ?? "", 320, 4);
        var hasBuild = false;
        if (element.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var seen = 0;
            foreach (var asset in assets.EnumerateArray())
            {
                if (++seen > 100) break;
                if (asset.ValueKind == JsonValueKind.Object && String(asset, "name", 120) is { } assetName && string.Equals(assetName, WindowsAssetName, StringComparison.Ordinal)) { hasBuild = true; break; }
            }
        }
        return new(version, tag, title, published, summary, prerelease, hasBuild);
    }

    private static bool? Bool(JsonElement o, string property) => o.TryGetProperty(property, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static string? String(JsonElement o, string property, int maximum) =>
        o.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s && s.Length <= maximum ? s : null;

    /// <summary>Plain text for display: no markup, links reduced to their text, control, format (zero-width, direction override) and private-use characters removed, bounded in lines and length.</summary>
    internal static string Plain(string text, int maximumLength, int maximumLines)
    {
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var cleaned = new StringBuilder(raw.Length);
            foreach (var ch in raw)
            {
                var category = char.GetUnicodeCategory(ch);
                if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) cleaned.Append(' ');
                else cleaned.Append(ch);
            }
            string line;
            try { line = MarkdownLink().Replace(cleaned.ToString(), "$1"); } catch (RegexMatchTimeoutException) { line = cleaned.ToString(); }
            line = line.Replace("`", "", StringComparison.Ordinal).Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal).Trim().TrimStart('#', '>').Trim();
            if (line.Length == 0) continue;
            lines.Add(Regex.Replace(line, @"\s{2,}", " ", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)));
            if (lines.Count == maximumLines) break;
        }
        var joined = string.Join('\n', lines);
        return joined.Length <= maximumLength ? joined : joined[..(maximumLength - 1)].TrimEnd() + "…";
    }

    private static UpdateCheckResult Result(UpdateOutcome outcome, string message, ReleaseVersion installed) => new(outcome, message, installed);

    private sealed record Candidate(ReleaseVersion Version, string Tag, string Title, DateTimeOffset? PublishedAt, string Summary, bool IsPrerelease, bool HasWindowsBuild)
    {
        public AvailableRelease ToRelease(string repositoryPage) =>
            new(Version, Tag, Title, PublishedAt, Summary, new Uri($"{repositoryPage}/releases/tag/{Uri.EscapeDataString(Tag)}"), IsPrerelease);
    }

    private sealed class UnsafeRedirectException : Exception;
    private sealed class InvalidResponseException : Exception;
}

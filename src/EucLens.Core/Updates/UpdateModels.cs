namespace EucLens.Core;

/// <summary>How an update check ended. Every ending is an ordinary result; none throws, and none affects monitoring or recording.</summary>
public enum UpdateOutcome
{
    /// <summary>A newer release with a Windows download exists.</summary>
    UpdateAvailable,
    /// <summary>The installed version is the newest one (or newer, for example a build made before its release).</summary>
    UpToDate,
    /// <summary>No release is published for the checked channel (a repository that has none, a private one, or only pre-releases while checking stable).</summary>
    NoPublicRelease,
    /// <summary>A newer release is listed, but it has no Windows build attached, so there is nothing to offer.</summary>
    NoCompatibleBuild,
    /// <summary>The network, a proxy or a firewall did not let the request through.</summary>
    Offline,
    /// <summary>The secure connection could not be verified (certificate trust). It is never bypassed.</summary>
    SecureConnectionFailed,
    TimedOut,
    RateLimited,
    ServiceUnavailable,
    /// <summary>The reply was too large, not JSON, or not shaped like a list of releases.</summary>
    InvalidResponse,
    /// <summary>The server tried to send the request somewhere other than the official GitHub API.</summary>
    UnsafeRedirect,
    Cancelled,
}

/// <summary>A release EUC Lens may offer. Everything in it was validated; the page address is built here from the validated tag, never copied from the reply.</summary>
public sealed record AvailableRelease(ReleaseVersion Version, string Tag, string Title, DateTimeOffset? PublishedAt, string Summary, Uri PageUri, bool IsPrerelease);

/// <summary>The result of one check: a short sentence that is safe to show (no response text, no exception text) and the release when there is one to offer.</summary>
public sealed record UpdateCheckResult(UpdateOutcome Outcome, string Message, ReleaseVersion Installed, AvailableRelease? Release = null)
{
    public bool IsUpdateAvailable => Outcome == UpdateOutcome.UpdateAvailable && Release is not null;
}

public interface IUpdateChecker
{
    /// <summary>Asks the official release list once. Never throws for network, server or content problems.</summary>
    Task<UpdateCheckResult> CheckAsync(ReleaseVersion installed, bool includePrereleases, CancellationToken cancellationToken);
}

using System.Globalization;
using System.Runtime.InteropServices;
using EucLens.Core;

namespace EucLens.App.ViewModels;

/// <summary>One labelled line of the About page.</summary>
public sealed record AboutFact(string Label, string Value);

/// <summary>
/// The About page: who the application is (read from the assembly metadata, nothing typed twice), the licence and acknowledgments, the release notes and an update check.
/// <para>
/// Privacy: building this object, showing the page or changing an option makes no network request. The only network access is <see cref="CheckForUpdatesCommand"/>, which the user
/// presses; the optional checker is created at that moment. "Download update" opens the official release page in the browser; EUC Lens never downloads or starts a file.
/// </para>
/// </summary>
public sealed class AboutViewModel : ObservableObject
{
    private readonly IDesktopServices desktop;
    private readonly AppIdentity identity;
    private readonly ReleaseVersion installed;
    private IUpdateChecker? checker;
    private string updateStatus = IdleStatus;
    private string copyStatus = "";
    private string linkStatus = "";
    private bool isChecking;
    private bool includePrereleases;
    private AvailableRelease? release;
    private int checks;

    public const string IdleStatus = "EUC Lens does not look for updates by itself. Select Check for updates to ask GitHub's public release list. Nothing about this computer, your session or your recordings is sent.";

    public AboutViewModel(IDesktopServices desktop, IUpdateChecker? updateChecker = null, AppIdentity? identity = null)
    {
        this.desktop = desktop;
        this.identity = identity ?? AppIdentity.Current;
        checker = updateChecker;
        installed = ReleaseVersion.TryParse(this.identity.Version, out var parsed) ? parsed : ReleaseVersion.Zero;
        Facts =
        [
            new("Version", this.identity.Version),
            new("Build", BuildText),
            new("Platform", this.identity.Platform),
            new("Maintainer", this.identity.Maintainer),
            new("Copyright", this.identity.Copyright),
            new("License", this.identity.License),
        ];
        CopyVersionCommand = new(CopyVersion);
        OpenRepositoryCommand = new(() => Open(new Uri(this.identity.RepositoryUrl)));
        OpenReleaseNotesCommand = new(() => Open(release?.PageUri ?? new Uri(this.identity.RepositoryUrl.TrimEnd('/') + "/releases")));
        DownloadUpdateCommand = new(() => { if (release is { } r) Open(r.PageUri); }, () => HasUpdate);
        CheckForUpdatesCommand = new(CheckAsync);
    }

    public AppIdentity Identity => identity;
    public string Product => identity.Product;
    public string Description => identity.Description;
    public string Version => identity.Version;
    /// <summary>The discreet line in the navigation footer.</summary>
    public string FooterVersion => $"Version {identity.Version}";
    /// <summary>The abbreviated commit the build recorded, or a plain statement that it recorded none (a local build).</summary>
    public string BuildText => identity.ShortCommit is { } commit ? commit : "not recorded (local build)";
    public IReadOnlyList<AboutFact> Facts { get; }
    public string RepositoryAddress => identity.RepositoryUrl;
    public string LicenseText { get; } = LegalTexts.License;
    public string ThirdPartyText { get; } = LegalTexts.ThirdPartyNotices;
    public string ReleaseNotesText => LegalTexts.ReleaseNotesFor(identity.Version);

    public RelayCommand CopyVersionCommand { get; }
    public RelayCommand OpenRepositoryCommand { get; }
    public RelayCommand OpenReleaseNotesCommand { get; }
    public RelayCommand DownloadUpdateCommand { get; }
    public AsyncCommand CheckForUpdatesCommand { get; }

    /// <summary>How many update checks were started: zero until the user asks. The Windows validation and the tests rely on it.</summary>
    public int CheckCount => checks;

    public string UpdateStatus { get => updateStatus; private set => Set(ref updateStatus, value); }
    public string CopyStatus { get => copyStatus; private set => Set(ref copyStatus, value); }
    public string LinkStatus { get => linkStatus; private set => Set(ref linkStatus, value); }
    public bool IsChecking { get => isChecking; private set { if (Set(ref isChecking, value)) CheckForUpdatesCommand.Refresh(); } }
    /// <summary>The pre-release channel is an explicit, per-visit choice (never saved): off, only stable releases are offered.</summary>
    public bool IncludePrereleases { get => includePrereleases; set => Set(ref includePrereleases, value); }

    public bool HasUpdate => release is not null;
    public string InstalledVersionText => $"Installed: {identity.Version}";
    public string LatestVersionText => release is null ? "" : $"Latest available: {release.Version}{(release.IsPrerelease ? " (pre-release)" : "")}";
    public string ReleaseDateText => release?.PublishedAt is { } at ? $"Published {at.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)} (UTC)" : "";
    public string ReleaseTitle => release?.Title ?? "";
    public string ReleaseSummary => release?.Summary ?? "";

    /// <summary>The system information a person pastes into a support request: product, version, build, platform and runtime. No computer name, user name or address.</summary>
    public string VersionInformation =>
        $"{identity.Product} {identity.Version}{Environment.NewLine}Build: {BuildText}{Environment.NewLine}Platform: Windows {IdentityPlatform}{Environment.NewLine}Runtime: {RuntimeInformation.FrameworkDescription}{Environment.NewLine}Windows: {RuntimeInformation.OSDescription}";

    private static string IdentityPlatform => AppIdentity.ArchitectureName(RuntimeInformation.ProcessArchitecture);

    private void CopyVersion() => CopyStatus = desktop.CopyText(VersionInformation) ? "Version information copied to the clipboard." : "The clipboard is not available right now.";

    private void Open(Uri address) => LinkStatus = desktop.OpenUrl(address) ? "Opened in your browser. EUC Lens itself downloads and installs nothing." : $"The browser could not be opened. The address is {address.AbsoluteUri}";

    private async Task CheckAsync()
    {
        checks++;
        SetRelease(null);
        UpdateStatus = "Checking GitHub for releases…";
        IsChecking = true;
        UpdateCheckResult result;
        try
        {
            checker ??= new GitHubUpdateChecker();
            result = await checker.CheckAsync(installed, IncludePrereleases, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The checker reports every network and content problem as a result; this is the last line of defence, and says nothing from the failure itself.
            result = new(UpdateOutcome.InvalidResponse, "The update check could not be completed.", installed);
        }
        finally { IsChecking = false; }
        UpdateStatus = result.Message;
        SetRelease(result.IsUpdateAvailable ? result.Release : null);
    }

    private void SetRelease(AvailableRelease? value)
    {
        release = value;
        foreach (var name in new[] { nameof(HasUpdate), nameof(LatestVersionText), nameof(ReleaseDateText), nameof(ReleaseTitle), nameof(ReleaseSummary) }) Raise(name);
        DownloadUpdateCommand.Refresh();
    }

    /// <summary>Windows validation only: the page shows the answer of a fake checker instead of calling GitHub.</summary>
    internal void UseCheckerForValidation(IUpdateChecker validation) { checker = validation; SetRelease(null); UpdateStatus = IdleStatus; LinkStatus = ""; CopyStatus = ""; }
}

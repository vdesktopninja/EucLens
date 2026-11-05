using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EucLens.App;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Diagnostics;
using EucLens.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EucLens.Tests;

/// <summary>
/// Application identity, the About page and the update check. No test reaches the real GitHub: every reply is a fake handler's, so the suite does not depend on the network
/// or on GitHub's availability, and the tests that matter for privacy prove that nothing is requested until the user asks.
/// </summary>
public sealed class AboutAndUpdateTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../.."));
    private static string Source(string relative) => File.ReadAllText(Path.Combine(Repo(), relative));
    /// <summary>obj and bin hold generated files (global usings include System.Net.Http); the check is of hand-written source, on any path separator.</summary>
    private static bool IsBuildOutput(string path) { var normalized = path.Replace('\\', '/'); return normalized.Contains("/obj/", StringComparison.Ordinal) || normalized.Contains("/bin/", StringComparison.Ordinal); }
    private static ReleaseVersion V(string text) => ReleaseVersion.TryParse(text, out var v) ? v : throw new ArgumentException(text);
    private static readonly ReleaseVersion Installed = V("0.5.1");

    // ---------- identity: one source, accurate metadata ----------

    [Fact]
    public void TheVersionIsWrittenOnceAndEveryCarrierAgrees()
    {
        var props = Source("Directory.Build.props");
        var declared = Regex.Match(props, @"<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.Equal("0.5.1", declared);
        Assert.Equal(declared, AppVersion.Current);
        Assert.Equal(declared, AppIdentity.Current.Version);
        var core = typeof(AppVersion).Assembly;
        Assert.Equal(declared + ".0", core.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
        Assert.Equal(declared, core.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        // No source revision is appended to the product version: Explorer shows "0.5.1" exactly.
        Assert.DoesNotContain("+", core.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void NoViewAndNoAboutCodeTypesTheVersionNumberAgain()
    {
        foreach (var xaml in Directory.EnumerateFiles(Path.Combine(Repo(), "src/EucLens.App"), "*.xaml", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
            Assert.DoesNotMatch(@"\b\d+\.\d+\.\d+\b", File.ReadAllText(xaml));
        foreach (var code in new[] { "src/EucLens.App/ViewModels/AboutViewModel.cs", "src/EucLens.Core/About/AppIdentity.cs", "src/EucLens.Core/Updates/GitHubUpdateChecker.cs" })
            Assert.DoesNotContain("0.5.1", Source(code), StringComparison.Ordinal);
    }

    [Fact]
    public void TheIdentityIsTheOwnerApprovedOneAndNothingIsInvented()
    {
        var identity = AppIdentity.Current;
        Assert.Equal("EUC Lens", identity.Product);
        Assert.Equal("Remote Session Performance Diagnostics", identity.Description);
        // The owner-confirmed public identity: author, professional brand (the publisher Windows shows) and website.
        Assert.Equal("Dominik Jakubowski", identity.Author);
        Assert.Equal("VDI Ninja", identity.Publisher);
        Assert.Equal("Dominik Jakubowski, VDI Ninja", identity.AuthorLine);
        Assert.Equal("https://vdesktop.ninja", identity.Website);
        Assert.Equal("MIT", identity.License);
        Assert.Equal("https://github.com/vdesktopninja/RDA", identity.RepositoryUrl);
        // The copyright is the repository's own: the first copyright line of LICENSE, unchanged.
        var license = Source("LICENSE");
        Assert.StartsWith("MIT License", license, StringComparison.Ordinal);
        Assert.Contains(identity.Copyright, license, StringComparison.Ordinal);
        Assert.Equal("Copyright (c) 2026 EUC Lens contributors", identity.Copyright);
        // The brand is not a legal entity and the copyright line was not rewritten: the existing contributors stay named in it, and LICENSE is exactly the MIT text with that line.
        Assert.DoesNotContain("Jakubowski", license, StringComparison.Ordinal);
        Assert.DoesNotContain("VDI Ninja", license, StringComparison.Ordinal);
        Assert.Contains("Permission is hereby granted, free of charge", license, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExecutableFileDescriptionAndTheSharedPropertiesAreConfigured()
    {
        var project = Source("src/EucLens.App/EucLens.App.csproj");
        Assert.Contains("<AssemblyTitle>EUC Lens - Remote Session Performance Diagnostics</AssemblyTitle>", project, StringComparison.Ordinal);
        var props = Source("Directory.Build.props");
        foreach (var expected in new[] { "<Product>EUC Lens</Product>", "<Description>Remote Session Performance Diagnostics</Description>", "<Authors>Dominik Jakubowski</Authors>", "<Company>VDI Ninja</Company>", "<EucLensWebsite>https://vdesktop.ninja</EucLensWebsite>", "<Copyright>Copyright (c) 2026 EUC Lens contributors</Copyright>", "<PackageLicenseExpression>MIT</PackageLicenseExpression>" })
            Assert.Contains(expected, props, StringComparison.Ordinal);
        var core = typeof(AppVersion).Assembly;
        Assert.Equal("EUC Lens", core.GetCustomAttribute<AssemblyProductAttribute>()!.Product);
        Assert.Equal("Remote Session Performance Diagnostics", core.GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description);
        Assert.Equal("VDI Ninja", core.GetCustomAttribute<AssemblyCompanyAttribute>()!.Company);
        var metadata = core.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
        Assert.Equal("Dominik Jakubowski", metadata["Author"]);
        Assert.Equal("https://vdesktop.ninja", metadata["Website"]);
        Assert.Equal("https://github.com/vdesktopninja/RDA", metadata["RepositoryUrl"]);        // the repository address is unchanged
    }

    [Theory]
    [InlineData("727acd1", true)]
    [InlineData("727ACD1346D5415CECC4695BD7D3D0E949274158", true)]
    [InlineData("727acd", false)]
    [InlineData("727acd1346d5415cecc4695bd7d3d0e9492741580", false)]
    [InlineData("727acd1; rm -rf", false)]
    [InlineData("<script>", false)]
    [InlineData("", false)]
    public void OnlyAHexadecimalCommitIsDisplayed(string text, bool valid) => Assert.Equal(valid, AppIdentity.IsCommit(text));

    [Fact]
    public void TheBuildIsAbbreviatedWhenItIsKnownAndSaidToBeUnrecordedWhenItIsNot()
    {
        var known = AppIdentity.Current with { Commit = "727acd1346d5415cecc4695bd7d3d0e949274158" };
        Assert.Equal("727acd1", known.ShortCommit);
        Assert.Equal("0.5.1 (727acd1)", known.VersionWithBuild);
        var local = AppIdentity.Current with { Commit = null };
        Assert.Null(local.ShortCommit);
        Assert.Equal("0.5.1", local.VersionWithBuild);
        Assert.Equal("not recorded (local build)", new AboutViewModel(new AboutDesktop(), identity: local).BuildText);
        Assert.Equal("727acd1", new AboutViewModel(new AboutDesktop(), identity: known).BuildText);
    }

    [Fact]
    public void TheBuildPassesTheCommitIntoTheAssemblyAndTheWorkflowSuppliesIt()
    {
        Assert.Contains("""<AssemblyMetadata Include="CommitSha" Value="$(EucLensCommit)" Condition="'$(EucLensCommit)' != ''" />""", Source("Directory.Build.props"), StringComparison.Ordinal);
        var workflow = Source(".github/workflows/windows.yml");
        Assert.Matches(@"dotnet build EucLens\.sln[^\n]*-p:EucLensCommit=\$\{\{ github\.sha \}\}", workflow);
        Assert.Matches(@"dotnet publish src/EucLens\.App/EucLens\.App\.csproj[^\n]*-p:EucLensCommit=\$\{\{ github\.sha \}\}", workflow);
    }

    // ---------- legal texts ----------

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    [Fact]
    public void TheEmbeddedLicenseAndNoticesAreTheRepositorysOwnFiles()
    {
        Assert.Equal(Normalize(Source("LICENSE")), Normalize(LegalTexts.License));
        Assert.Equal(Normalize(Source("THIRD_PARTY_NOTICES.md")), Normalize(LegalTexts.ThirdPartyNotices));
        Assert.Equal(Normalize(Source("CHANGELOG.md")), Normalize(LegalTexts.Changelog));
        Assert.StartsWith("MIT License", LegalTexts.License, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryVerbatimLicenseFileIsEmbeddedUnchangedAndNamedInTheNotices()
    {
        var folder = Path.Combine(Repo(), "licenses");
        var files = Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(files!, LegalTexts.LicenseFileNames());
        foreach (var required in new[] { "dotnet-runtime-LICENSE.TXT", "dotnet-runtime-THIRD-PARTY-NOTICES.TXT", "dotnet-windowsdesktop-LICENSE.txt", "skiasharp-LICENSE.txt", "skiasharp-native-THIRD-PARTY-NOTICES.txt", "glfw-COPYING.md", "opentk-LICENSE.md", "glwpfcontrol-LICENSE.md", "livecharts-LICENSE.txt", "README.md" })
            Assert.Contains(required, files);
        var full = LegalTexts.FullThirdPartyTexts();
        foreach (var file in files)
        {
            Assert.Contains(Normalize(File.ReadAllText(Path.Combine(folder, file!))), full.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
            // Every notice file (not the README) is referenced by name in the readable summary.
            if (file != "README.md") Assert.Contains(file!, LegalTexts.ThirdPartyNotices, StringComparison.Ordinal);
        }
        Assert.Contains("THIRD-PARTY SOFTWARE NOTICES AND INFORMATION", full, StringComparison.Ordinal);
        Assert.Contains("License notice for zlib-ng", full, StringComparison.Ordinal);
        Assert.Contains("zlib/libpng", LegalTexts.ThirdPartyNotices, StringComparison.Ordinal);
        Assert.True(full.Length > 200_000);
    }

    [Fact]
    public void TheNoticesNameVerifiedLicensesAndNeverVagueOnes()
    {
        var notices = LegalTexts.ThirdPartyNotices;
        Assert.DoesNotContain("MIT-style", notices, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MIT style", notices, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-like", notices, StringComparison.OrdinalIgnoreCase);
        // The MIT components' verbatim texts really are MIT, and the GLFW file really is the zlib/libpng terms.
        foreach (var file in new[] { "livecharts-LICENSE.txt", "opentk-LICENSE.md", "glwpfcontrol-LICENSE.md", "skiasharp-LICENSE.txt", "dotnet-runtime-LICENSE.TXT", "dotnet-windowsdesktop-LICENSE.txt" })
            Assert.Contains("Permission is hereby granted, free of charge", Source("licenses/" + file), StringComparison.Ordinal);
        Assert.Contains("must not be misrepresented", Source("licenses/glfw-COPYING.md"), StringComparison.Ordinal);
        // The copyright holders named in the summary are the ones in the verbatim texts.
        Assert.Contains("Alberto Rodriguez Orozco", Source("licenses/livecharts-LICENSE.txt"), StringComparison.Ordinal);
        Assert.Contains("Alberto Rodriguez Orozco", notices, StringComparison.Ordinal);
        Assert.Contains("Stefanos Apostolopoulos", Source("licenses/opentk-LICENSE.md"), StringComparison.Ordinal);
        Assert.Contains("Stefanos Apostolopoulos", notices, StringComparison.Ordinal);
        Assert.Contains("Xamarin", Source("licenses/skiasharp-LICENSE.txt"), StringComparison.Ordinal);
        Assert.Contains("Xamarin", notices, StringComparison.Ordinal);
        Assert.Contains("varon", notices, StringComparison.Ordinal);
        // What a package does not state is said not to be stated.
        Assert.Contains("state no license", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPackageTheApplicationRestoresIsAcknowledged()
    {
        var assets = Path.Combine(Repo(), "src/EucLens.App/obj/project.assets.json");
        Assert.True(File.Exists(assets), "Restore the solution first: the acknowledgments are compared with the restored package list.");
        using var document = JsonDocument.Parse(File.ReadAllText(assets));
        var packages = document.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(l => l.Value.GetProperty("type").GetString() == "package").Select(l => l.Name.Split('/')[0]).ToList();
        Assert.True(packages.Count >= 10);
        var notices = LegalTexts.ThirdPartyNotices;
        var missing = packages.Where(p => !notices.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(missing.Count == 0, "Not acknowledged in THIRD_PARTY_NOTICES.md: " + string.Join(", ", missing));
        Assert.Contains("MIT", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseNotesAreTheChangelogSectionOfExactlyThisVersion()
    {
        var notes = LegalTexts.ReleaseNotesFor("0.5.1");
        Assert.StartsWith("## 0.5.1", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("## 0.5.0", notes, StringComparison.Ordinal);
        var log = "# Changelog\n\n## 0.5.10 — later\n\nten\n\n## 0.5.1 — this\n\none\n\n## 0.5.0 — before\n\nzero\n";
        Assert.Equal("## 0.5.1 — this\n\none", LegalTexts.ReleaseNotesFor(log, "0.5.1"));
        Assert.Contains("not included", LegalTexts.ReleaseNotesFor(log, "0.5"), StringComparison.Ordinal);
        Assert.Contains("not included", LegalTexts.ReleaseNotesFor(log, "9.9.9"), StringComparison.Ordinal);
    }

    // ---------- the About page ----------

    private sealed class AboutDesktop : IDesktopServices
    {
        public string? Copied { get; private set; }
        public bool ClipboardWorks { get; set; } = true;
        public List<Uri> Opened { get; } = [];
        public bool BrowserWorks { get; set; } = true;
        public string? SelectExportPath(bool json) => null;
        public bool ConfirmNewRecording() => true;
        public void ApplyTheme(bool dark) { }
        public Task SavePreferencesAsync(Preferences preferences) => Task.CompletedTask;
        public Task<Preferences?> LoadPreferencesAsync() => Task.FromResult<Preferences?>(null);
        public bool CopyText(string text) { if (!ClipboardWorks) return false; Copied = text; return true; }
        public bool OpenUrl(Uri url) { if (!SafeLinks.IsOfficial(url) || !BrowserWorks) return false; Opened.Add(url); return true; }
    }

    private sealed class CountingChecker(Func<UpdateCheckResult>? answer = null) : IUpdateChecker
    {
        public int Calls { get; private set; }
        public bool? LastIncludePrereleases { get; private set; }
        public Task<UpdateCheckResult> CheckAsync(ReleaseVersion installed, bool includePrereleases, CancellationToken cancellationToken)
        {
            Calls++; LastIncludePrereleases = includePrereleases;
            return Task.FromResult(answer?.Invoke() ?? new UpdateCheckResult(UpdateOutcome.UpToDate, "You have the latest version (0.5.1).", installed));
        }
    }

    private static AvailableRelease Release(string version = "0.6.0", bool pre = false) =>
        new(V(version), "v" + version, "EUC Lens " + version, new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero), "Faster history.\nBetter charts.", new Uri($"https://github.com/vdesktopninja/RDA/releases/tag/v{version}"), pre);

    [Fact]
    public void AboutShowsTheIdentityFromTheSingleSource()
    {
        var about = new AboutViewModel(new AboutDesktop(), new CountingChecker());
        var facts = about.Facts.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal(AppVersion.Current, facts["Version"]);
        Assert.Equal(AppIdentity.Current.Copyright, facts["Copyright"]);
        Assert.Equal("MIT", facts["License"]);
        Assert.DoesNotContain("Maintainer", facts.Keys);
        Assert.Equal("Dominik Jakubowski", about.Author);
        Assert.Equal("VDI Ninja", about.Publisher);
        Assert.Equal("https://vdesktop.ninja", about.Website);
        Assert.Equal(about.BuildText, facts["Build"]);
        Assert.Contains("x64", AppIdentity.ArchitectureName(System.Runtime.InteropServices.Architecture.X64));
        Assert.StartsWith("Windows 10", facts["Platform"], StringComparison.Ordinal);
        Assert.Equal("EUC Lens", about.Product);
        Assert.Equal("Remote Session Performance Diagnostics", about.Description);
        Assert.Equal("Version " + AppVersion.Current, about.FooterVersion);
        Assert.Equal("https://github.com/vdesktopninja/RDA", about.RepositoryAddress);
        Assert.NotEmpty(about.LicenseText); Assert.NotEmpty(about.ThirdPartyText); Assert.StartsWith("## " + AppVersion.Current, about.ReleaseNotesText, StringComparison.Ordinal);
    }

    [Fact]
    public void ThisUnpublishedCandidateIsNotLabelledStableAndNoChannelIsShown()
    {
        var about = new AboutViewModel(new AboutDesktop(), new CountingChecker());
        var everything = string.Join('\n', about.Facts.Select(f => f.Label + f.Value)) + about.FooterVersion + about.UpdateStatus + about.Product + about.Description;
        Assert.DoesNotContain("stable", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("channel", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stable", Source("src/EucLens.App/MainWindow.xaml"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stable release", Source("src/EucLens.App/Views/AboutView.xaml"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryBindingOfTheAboutPageAndTheFooterResolvesToAMemberOfTheViewModel()
    {
        var xaml = Source("src/EucLens.App/Views/AboutView.xaml") + Source("src/EucLens.App/MainWindow.xaml");
        var names = Regex.Matches(xaml, @"\{Binding About\.([A-Za-z]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(names.Count >= 15);
        var members = typeof(AboutViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToHashSet();
        var unresolved = names.Where(n => !members.Contains(n)).ToList();
        Assert.True(unresolved.Count == 0, "Bindings without a member: " + string.Join(", ", unresolved));
        // Every button has an accessible name (its text or an explicit one).
        foreach (Match button in Regex.Matches(Source("src/EucLens.App/Views/AboutView.xaml"), @"<Button [^>]*>"))
            Assert.Contains("AutomationProperties.Name=", button.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AboutIsALastPageAndTheFooterShowsTheInstalledVersion()
    {
        var vm = new MainViewModel(new MockMetricsProvider(), new DiagnosticIntelligenceEngine(DiagnosticRuleSet.Default()), new SessionRecorder(), new AboutDesktop(), NullLogger<MainViewModel>.Instance, updateChecker: new CountingChecker());
        Assert.IsType<AboutPage>(vm.Pages[^1]);
        Assert.Equal("About", vm.Pages[^1].Name);
        Assert.Same(vm.About, ((AboutPage)vm.Pages[^1]).About);
        Assert.Equal("Version " + AppVersion.Current, vm.About.FooterVersion);
        Assert.Contains("{Binding About.FooterVersion}", Source("src/EucLens.App/MainWindow.xaml"), StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public void CopyingTheVersionPutsProductVersionBuildAndPlatformOnTheClipboardAndNothingPersonal()
    {
        var desktop = new AboutDesktop();
        var about = new AboutViewModel(desktop, new CountingChecker());
        about.CopyVersionCommand.Execute(null);
        Assert.Equal("Version information copied to the clipboard.", about.CopyStatus);
        var text = desktop.Copied!;
        Assert.StartsWith("EUC Lens " + AppVersion.Current, text, StringComparison.Ordinal);
        Assert.Contains("Build: ", text, StringComparison.Ordinal);
        Assert.Contains("Platform: Windows ", text, StringComparison.Ordinal);
        Assert.Contains("Author: Dominik Jakubowski, VDI Ninja", text, StringComparison.Ordinal);
        Assert.Contains("Website: https://vdesktop.ninja", text, StringComparison.Ordinal);
        Assert.Contains("Runtime: ", text, StringComparison.Ordinal);
        // The author's own name is part of the text on purpose; everything else must hold nothing of this computer or its user (the developer's account name can equal the author's first name).
        var withoutAuthor = string.Join('\n', text.Split('\n').Where(l => !l.StartsWith("Author: ", StringComparison.Ordinal)));
        Assert.DoesNotContain(Environment.MachineName, withoutAuthor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, withoutAuthor, StringComparison.OrdinalIgnoreCase);
        desktop.ClipboardWorks = false;
        about.CopyVersionCommand.Execute(null);
        Assert.Equal("The clipboard is not available right now.", about.CopyStatus);
    }

    [Fact]
    public void TheWebsiteIsAClickableLinkThatOpensOnlyTheOfficialSiteInTheDefaultBrowser()
    {
        var desktop = new AboutDesktop();
        var about = new AboutViewModel(desktop, new CountingChecker());
        about.OpenWebsiteCommand.Execute(null);
        Assert.Equal(["https://vdesktop.ninja/"], desktop.Opened.Select(u => u.AbsoluteUri));
        Assert.StartsWith("Opened in your browser", about.LinkStatus, StringComparison.Ordinal);
        // The identity block shows the author, the brand and the website as a named button, in that order, and the button is the website's link.
        var xaml = Source("src/EucLens.App/Views/AboutView.xaml");
        var block = Regex.Match(xaml, @"(?s)<StackPanel Margin=""0,0,0,12"" AutomationProperties\.Name=""Author and brand"">.*?</StackPanel>").Value;
        Assert.True(block.Length > 0);
        Assert.True(block.IndexOf("About.Author", StringComparison.Ordinal) < block.IndexOf("About.Publisher", StringComparison.Ordinal) && block.IndexOf("About.Publisher", StringComparison.Ordinal) < block.IndexOf("About.Website", StringComparison.Ordinal));
        Assert.Contains("Command=\"{Binding About.OpenWebsiteCommand}\"", block, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=", block, StringComparison.Ordinal);
        // The desktop service opens a link through the shell (the default browser) only after the allowlist; nothing else starts a process for a link.
        var services = Source("src/EucLens.App/DesktopServices.cs");
        Assert.Contains("if (!SafeLinks.IsOfficial(url)) return false;", services, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = true", services, StringComparison.Ordinal);
        // A browser that cannot be started is reported with the address.
        desktop.BrowserWorks = false;
        about.OpenWebsiteCommand.Execute(null);
        Assert.Contains("https://vdesktop.ninja", about.LinkStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPlaceThatNamesTheAuthorAgrees()
    {
        var identity = AppIdentity.Current;
        var about = new AboutViewModel(new AboutDesktop(), new CountingChecker());
        // About, the copied version information, the shared build properties and the documentation use the same author, brand and website.
        Assert.Equal(("Dominik Jakubowski", "VDI Ninja", "https://vdesktop.ninja"), (about.Author, about.Publisher, about.Website));
        Assert.Contains($"Author: {identity.AuthorLine}", about.VersionInformation, StringComparison.Ordinal);
        Assert.Contains($"Website: {identity.Website}", about.VersionInformation, StringComparison.Ordinal);
        var props = Source("Directory.Build.props");
        Assert.Contains("<Authors>Dominik Jakubowski</Authors>", props, StringComparison.Ordinal);
        Assert.Contains("<Company>VDI Ninja</Company>", props, StringComparison.Ordinal);
        foreach (var doc in new[] { "docs/ABOUT_AND_UPDATES.md", "README.md", "docs/WINDOWS_ACCEPTANCE.md" })
        {
            var text = Source(doc);
            Assert.Contains("Dominik Jakubowski", text, StringComparison.Ordinal);
            Assert.Contains("https://vdesktop.ninja", text, StringComparison.Ordinal);
            Assert.DoesNotContain("pending the owner", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pending your confirmation", text, StringComparison.OrdinalIgnoreCase);
        }
        // The old unconfirmed wording is gone everywhere.
        foreach (var file in new[] { "CHANGELOG.md", "docs/ABOUT_AND_UPDATES.md", "docs/WINDOWS_ACCEPTANCE.md", "docs/SELF_UPDATE_DESIGN.md", "docs/STABLE_RELEASE_0.5.1.md" })
            Assert.DoesNotContain("pending the owner", Source(file), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShowingTheAboutPageAndChangingItsOptionsMakesNoRequest()
    {
        var checker = new CountingChecker();
        var vm = new MainViewModel(new MockMetricsProvider(), new DiagnosticIntelligenceEngine(DiagnosticRuleSet.Default()), new SessionRecorder(), new AboutDesktop(), NullLogger<MainViewModel>.Instance, updateChecker: checker);
        vm.SelectedPage = vm.Pages.OfType<AboutPage>().Single();
        vm.About.IncludePrereleases = true;
        vm.About.IncludePrereleases = false;
        vm.About.CopyVersionCommand.Execute(null);
        vm.About.OpenReleaseNotesCommand.Execute(null);
        vm.SelectedPage = vm.Pages[0];
        Assert.Equal(0, checker.Calls);
        Assert.Equal(0, vm.About.CheckCount);
        Assert.Equal(AboutViewModel.IdleStatus, vm.About.UpdateStatus);
        Assert.False(vm.About.HasUpdate);
    }

    [Fact]
    public void NoNetworkCodeExistsOutsideTheUpdateCheckerAndItIsOnlyCreatedByTheAboutViewModel()
    {
        var network = new Regex(@"System\.Net\.Http|HttpClient|HttpWebRequest|WebRequest|WebClient|System\.Net\.Sockets|TcpClient|UdpClient|api\.github\.com|NetworkInformation\.Ping", RegexOptions.CultureInvariant);
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repo(), "src"), "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
        {
            var name = Path.GetRelativePath(Repo(), file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            if (name != "src/EucLens.Core/Updates/GitHubUpdateChecker.cs" && network.IsMatch(text)) offenders.Add(name);
            if (name is not ("src/EucLens.Core/Updates/GitHubUpdateChecker.cs" or "src/EucLens.App/ViewModels/AboutViewModel.cs") && text.Contains("new GitHubUpdateChecker(", StringComparison.Ordinal)) offenders.Add(name + " (creates the checker)");
        }
        Assert.Empty(offenders);
        // The checker is created at the moment of the first explicit check, never in a constructor of the page.
        var about = Source("src/EucLens.App/ViewModels/AboutViewModel.cs");
        Assert.Matches(@"checker \?\?= new GitHubUpdateChecker\(\);", about);
        Assert.DoesNotContain("new GitHubUpdateChecker", Regex.Match(about, @"(?s)public AboutViewModel\(.*?\n    \}").Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUpdateCheckIsOnlyStartedByTheButtonAndPassesTheChoiceOfChannel()
    {
        var checker = new CountingChecker(() => new UpdateCheckResult(UpdateOutcome.UpdateAvailable, "Version 0.6.0 is available.", Installed, Release()));
        var about = new AboutViewModel(new AboutDesktop(), checker);
        about.IncludePrereleases = true;
        about.CheckForUpdatesCommand.Execute(null);
        Assert.Equal(1, checker.Calls);
        Assert.True(checker.LastIncludePrereleases);
        Assert.True(about.HasUpdate);
        Assert.Equal("Installed: " + AppVersion.Current, about.InstalledVersionText);
        Assert.Equal("Latest available: 0.6.0", about.LatestVersionText);
        Assert.Equal("Published 1 December 2026 (UTC)", about.ReleaseDateText);
        Assert.Equal("Faster history.\nBetter charts.", about.ReleaseSummary);
        Assert.Equal("Version 0.6.0 is available.", about.UpdateStatus);
        Assert.True(about.DownloadUpdateCommand.CanExecute(null));
    }

    [Fact]
    public void DownloadAndReleaseNotesOpenOnlyTheOfficialReleasePageAndNothingIsDownloaded()
    {
        var desktop = new AboutDesktop();
        var about = new AboutViewModel(desktop, new CountingChecker(() => new UpdateCheckResult(UpdateOutcome.UpdateAvailable, "Version 0.6.0 is available.", Installed, Release())));
        Assert.False(about.DownloadUpdateCommand.CanExecute(null));          // nothing to download before a check found something
        about.DownloadUpdateCommand.Execute(null);
        Assert.Empty(desktop.Opened);
        about.CheckForUpdatesCommand.Execute(null);
        about.DownloadUpdateCommand.Execute(null);
        about.OpenReleaseNotesCommand.Execute(null);
        about.OpenRepositoryCommand.Execute(null);
        Assert.Equal(["https://github.com/vdesktopninja/RDA/releases/tag/v0.6.0", "https://github.com/vdesktopninja/RDA/releases/tag/v0.6.0", "https://github.com/vdesktopninja/RDA"], desktop.Opened.Select(u => u.AbsoluteUri));
        Assert.All(desktop.Opened, u => Assert.Equal(Uri.UriSchemeHttps, u.Scheme));
        // Without an update the release notes link goes to the repository's release list.
        var plain = new AboutViewModel(desktop, new CountingChecker());
        plain.OpenReleaseNotesCommand.Execute(null);
        Assert.Equal("https://github.com/vdesktopninja/RDA/releases", desktop.Opened[^1].AbsoluteUri);
        // A browser that cannot be started is reported with the address, not hidden.
        desktop.BrowserWorks = false;
        plain.OpenRepositoryCommand.Execute(null);
        Assert.Contains("https://github.com/vdesktopninja/RDA", plain.LinkStatus, StringComparison.Ordinal);
        Assert.Contains("could not be opened", plain.LinkStatus, StringComparison.Ordinal);
        // The only code that starts a process for a link is the desktop service, behind the allowlist.
        var services = Source("src/EucLens.App/DesktopServices.cs");
        Assert.Contains("if (!SafeLinks.IsOfficial(url)) return false;", services, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadFile", Source("src/EucLens.App/ViewModels/AboutViewModel.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePageStaysResponsiveWhileTheCheckIsPending()
    {
        var gate = new TaskCompletionSource<UpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var about = new AboutViewModel(new AboutDesktop(), new PendingChecker(gate.Task));
        var running = about.CheckForUpdatesCommand.ExecuteAsync();
        // Control came back at once: the page shows progress, the button is disabled, and everything else still answers.
        Assert.False(running.IsCompleted);
        Assert.True(about.IsChecking);
        Assert.Equal("Checking GitHub for releases…", about.UpdateStatus);
        Assert.False(about.CheckForUpdatesCommand.CanExecute(null));
        about.CopyVersionCommand.Execute(null);
        about.IncludePrereleases = true;
        gate.SetResult(new UpdateCheckResult(UpdateOutcome.UpToDate, "You have the latest version (0.5.1).", Installed));
        await running;
        Assert.False(about.IsChecking);
        Assert.Equal("You have the latest version (0.5.1).", about.UpdateStatus);
        Assert.True(about.CheckForUpdatesCommand.CanExecute(null));
    }

    [Fact]
    public async Task ACheckerThatFailsUnexpectedlyEndsAsANeutralMessageWithoutItsText()
    {
        var about = new AboutViewModel(new AboutDesktop(), new ThrowingChecker());
        await about.CheckForUpdatesCommand.ExecuteAsync();
        Assert.Equal("The update check could not be completed.", about.UpdateStatus);
        Assert.DoesNotContain("SECRET", about.UpdateStatus, StringComparison.Ordinal);
        Assert.False(about.HasUpdate);
        Assert.False(about.IsChecking);
    }

    private sealed class PendingChecker(Task<UpdateCheckResult> answer) : IUpdateChecker
    {
        public Task<UpdateCheckResult> CheckAsync(ReleaseVersion installed, bool includePrereleases, CancellationToken cancellationToken) => answer;
    }

    private sealed class ThrowingChecker : IUpdateChecker
    {
        public Task<UpdateCheckResult> CheckAsync(ReleaseVersion installed, bool includePrereleases, CancellationToken cancellationToken) => throw new InvalidOperationException("SECRET internal detail");
    }

    // ---------- semantic versions ----------

    [Theory]
    [InlineData("0.9.0", "0.10.0")]
    [InlineData("0.5.1", "0.5.2")]
    [InlineData("0.5.9", "0.6.0")]
    [InlineData("1.9.9", "2.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("0.5.1-rc.1", "0.5.1")]
    [InlineData("v0.5.1", "v0.5.2")]
    public void VersionsCompareByTheirNumbersNotAlphabetically(string older, string newer)
    {
        Assert.True(V(older) < V(newer), $"{older} < {newer}");
        Assert.True(V(newer) > V(older));
        Assert.NotEqual(V(older), V(newer));
        // Alphabetically "0.9.0" is greater than "0.10.0": the comparison must not be text.
        if (older == "0.9.0") Assert.True(string.CompareOrdinal(older, newer) > 0);
    }

    [Fact]
    public void BuildMetadataAndTheLeadingVAreIgnoredByTheComparison()
    {
        Assert.Equal(V("0.5.1"), V("v0.5.1"));
        Assert.Equal(V("0.5.1+abc"), V("0.5.1+def"));
        Assert.Equal("0.5.1", V("v0.5.1+abc").ToString());
        Assert.Equal("1.0.0-rc.1", V("1.0.0-rc.1").ToString());
        Assert.True(V("1.0.0-rc.1").IsPrerelease);
        Assert.False(V("1.0.0").IsPrerelease);
        Assert.Equal(V("1.0.0").GetHashCode(), V("v1.0.0+x").GetHashCode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-a..b")]
    [InlineData("1.2.3-a b")]
    [InlineData("latest")]
    [InlineData("release-0.6")]
    [InlineData("../../1.2.3")]
    [InlineData("1.2.3\n")]
    [InlineData("9999999.0.0")]
    [InlineData("٣.٢.١")]
    [InlineData("1.2.3-é")]
    public void TextThatIsNotAVersionIsRejected(string text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void AnEnormousOrPathologicalVersionIsRejectedQuickly()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(ReleaseVersion.TryParse(new string('1', 5000) + ".0.0", out _));
        Assert.False(ReleaseVersion.TryParse("1.0.0-" + string.Join('.', Enumerable.Repeat("a", 500)), out _));
        Assert.False(ReleaseVersion.TryParse(null, out _));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    // ---------- links ----------

    [Theory]
    [InlineData("https://github.com/vdesktopninja/RDA", true)]
    [InlineData("https://github.com/vdesktopninja/RDA/", true)]
    [InlineData("https://github.com/vdesktopninja/RDA/releases", true)]
    [InlineData("https://github.com/vdesktopninja/RDA/releases/tag/v0.6.0", true)]
    [InlineData("https://GitHub.com/VDesktopNinja/rda/releases", true)]
    [InlineData("http://github.com/vdesktopninja/RDA", false)]
    [InlineData("https://github.com.evil.example/vdesktopninja/RDA", false)]
    [InlineData("https://evil.example/vdesktopninja/RDA", false)]
    [InlineData("https://github.com@evil.example/vdesktopninja/RDA", false)]
    [InlineData("https://user:pw@github.com/vdesktopninja/RDA", false)]
    [InlineData("https://github.com:8443/vdesktopninja/RDA", false)]
    [InlineData("https://github.com/vdesktopninja/RDA-evil", false)]
    [InlineData("https://github.com/vdesktopninja/other", false)]
    [InlineData("https://github.com/vdesktopninja", false)]
    [InlineData("https://github.com/", false)]
    [InlineData("https://vdesktop.ninja", true)]
    [InlineData("https://vdesktop.ninja/", true)]
    [InlineData("https://VDesktop.Ninja/blog/euc-lens", true)]
    [InlineData("http://vdesktop.ninja", false)]
    [InlineData("https://www.vdesktop.ninja", false)]
    [InlineData("https://vdesktop.ninja.evil.example", false)]
    [InlineData("https://evilvdesktop.ninja", false)]
    [InlineData("https://vdesktop.ninja@evil.example/", false)]
    [InlineData("https://user:pw@vdesktop.ninja/", false)]
    [InlineData("https://vdesktop.ninja:8443/", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-settings:", false)]
    public void OnlyTheOwnRepositoryAndTheAuthorsWebsiteOverHttpsMayBeOpened(string address, bool official)
    {
        Assert.True(Uri.TryCreate(address, UriKind.Absolute, out var uri));
        Assert.Equal(official, SafeLinks.IsOfficial(uri));
        Assert.False(SafeLinks.IsOfficial(null));
        Assert.False(SafeLinks.IsOfficial(new Uri("/vdesktopninja/RDA", UriKind.Relative)));
    }

    // ---------- the update checker, with mocked replies ----------

    private sealed class FakeHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<(string Url, string Method, string[] Headers)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Seen.Add((request.RequestUri!.AbsoluteUri, request.Method.Method, request.Headers.Select(h => h.Key).ToArray()));
            return await reply(request, Requests.Count);
        }
    }

    private static FakeHandler Replying(HttpResponseMessage response) => new((_, _) => Task.FromResult(response));
    private static FakeHandler Replying(string json, string mediaType = "application/json") => new((_, _) => Task.FromResult(Json(json, mediaType)));
    private static FakeHandler Throwing(Exception exception) => new((_, _) => throw exception);

    private static HttpResponseMessage Json(string json, string mediaType = "application/json", HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new(status) { Content = content };
    }

    private static string Assets(params string[] names) => "[" + string.Join(',', names.Select(n => $$"""{"name":"{{n}}","browser_download_url":"https://evil.example/{{n}}"}""")) + "]";

    private static string Rel(string tag, bool prerelease = false, bool draft = false, string? assets = null, string body = "Notes.", string name = "", string published = "2026-10-01T08:00:00Z", string html = "https://github.com/vdesktopninja/RDA/releases/tag/x") =>
        $$"""{"tag_name":{{JsonSerializer.Serialize(tag)}},"name":{{JsonSerializer.Serialize(name)}},"draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"published_at":{{JsonSerializer.Serialize(published)}},"html_url":{{JsonSerializer.Serialize(html)}},"body":{{JsonSerializer.Serialize(body)}},"assets":{{assets ?? Assets(GitHubUpdateChecker.WindowsAssetName)}}}""";

    private static string List(params string[] releases) => "[" + string.Join(',', releases) + "]";

    private static async Task<UpdateCheckResult> CheckAsync(HttpMessageHandler handler, bool prereleases = false, ReleaseVersion? installed = null, TimeSpan? timeout = null, CancellationToken token = default)
    {
        using var checker = new GitHubUpdateChecker(handler, timeout: timeout);
        return await checker.CheckAsync(installed ?? Installed, prereleases, token);
    }

    [Fact]
    public async Task ANewerReleaseWithAWindowsBuildIsOfferedWithItsValidatedDetails()
    {
        var handler = Replying(List(Rel("v0.5.0"), Rel("v0.6.0", name: "EUC Lens 0.6.0", body: "# What's new\n\n- Faster history\n- Better charts", html: "https://evil.example/phish")));
        var result = await CheckAsync(handler);
        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
        Assert.True(result.IsUpdateAvailable);
        var release = result.Release!;
        Assert.Equal(V("0.6.0"), release.Version);
        Assert.Equal("EUC Lens 0.6.0", release.Title);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), release.PublishedAt);
        Assert.Equal("What's new\n- Faster history\n- Better charts", release.Summary);
        // The page address is built from the validated tag: the reply's own html_url is never used.
        Assert.Equal("https://github.com/vdesktopninja/RDA/releases/tag/v0.6.0", release.PageUri.AbsoluteUri);
        Assert.Equal("Version 0.6.0 is available.", result.Message);
        Assert.False(release.IsPrerelease);
    }

    [Fact]
    public async Task TheRequestIsOneAnonymousHttpsGetToTheOfficialReleaseList()
    {
        var handler = Replying(List(Rel("v0.5.1")));
        await CheckAsync(handler);
        var request = Assert.Single(handler.Seen);
        Assert.Equal("https://api.github.com/repos/vdesktopninja/RDA/releases?per_page=30", request.Url);
        Assert.Equal("GET", request.Method);
        // Only these headers: no credentials, no cookies, no identifiers.
        Assert.Equal(["Accept", "User-Agent", "X-GitHub-Api-Version"], request.Headers.OrderBy(h => h));
        var message = handler.Requests[0];
        Assert.Equal("EucLens/" + AppVersion.Current, message.Headers.UserAgent.ToString());
        Assert.Null(message.Headers.Authorization);
        Assert.Null(message.Content);
        Assert.DoesNotContain(Environment.MachineName, string.Join(' ', message.Headers.SelectMany(h => h.Value)), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, string.Join(' ', message.Headers.SelectMany(h => h.Value)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoPublishedReleaseIsAnOrdinaryNeutralResult()
    {
        var empty = await CheckAsync(Replying("[]"));
        Assert.Equal(UpdateOutcome.NoPublicRelease, empty.Outcome);
        Assert.Equal("No public release has been published yet.", empty.Message);
        Assert.Null(empty.Release);
        // A repository that is private or has no releases answers 404 to an anonymous request.
        var notFound = await CheckAsync(Replying(Json("""{"message":"Not Found"}""", status: HttpStatusCode.NotFound)));
        Assert.Equal(UpdateOutcome.NoPublicRelease, notFound.Outcome);
        Assert.Equal("No public release has been published yet.", notFound.Message);
    }

    [Theory]
    [InlineData("0.5.1", "You have the latest version (0.5.1).")]
    [InlineData("0.5.0", "This version (0.5.1) is newer than the latest published release (0.5.0).")]
    public async Task AnIdenticalOrOlderReleaseOffersNothing(string published, string message)
    {
        var result = await CheckAsync(Replying(List(Rel("v" + published))));
        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
        Assert.Null(result.Release);
        Assert.Equal(message, result.Message);
    }

    [Fact]
    public async Task PreReleasesAndDraftsAreNotOfferedToTheStableUser()
    {
        var list = List(Rel("v0.7.0-rc.1", prerelease: true), Rel("v0.8.0", prerelease: true), Rel("v0.9.0", draft: true), Rel("v0.5.0"));
        var stable = await CheckAsync(Replying(list));
        Assert.Equal(UpdateOutcome.UpToDate, stable.Outcome);
        Assert.Null(stable.Release);
        // Opted in: the newest pre-release is offered and said to be one. A draft is never offered.
        var testing = await CheckAsync(Replying(list), prereleases: true);
        Assert.Equal(UpdateOutcome.UpdateAvailable, testing.Outcome);
        Assert.Equal(V("0.8.0"), testing.Release!.Version);
        Assert.True(testing.Release.IsPrerelease);
        var about = new AboutViewModel(new AboutDesktop(), new CountingChecker(() => testing));
        about.CheckForUpdatesCommand.Execute(null);
        Assert.Equal("Latest available: 0.8.0 (pre-release)", about.LatestVersionText);
    }

    [Fact]
    public async Task OnlyPreReleasesPublishedMeansNoStableReleaseYet()
    {
        var result = await CheckAsync(Replying(List(Rel("v0.6.0-rc.1", prerelease: true))));
        Assert.Equal(UpdateOutcome.NoPublicRelease, result.Outcome);
        Assert.Contains("No full release", result.Message, StringComparison.Ordinal);
        // A tag that names a pre-release is one even if the flag says otherwise.
        var mislabelled = await CheckAsync(Replying(List(Rel("v0.6.0-beta.1", prerelease: false))));
        Assert.Equal(UpdateOutcome.NoPublicRelease, mislabelled.Outcome);
    }

    [Fact]
    public async Task TheNewestReleaseIsChosenBySemanticVersionNotByOrderOrAlphabet()
    {
        var result = await CheckAsync(Replying(List(Rel("v0.9.0"), Rel("v0.10.0"), Rel("v0.2.0"))));
        Assert.Equal(V("0.10.0"), result.Release!.Version);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""["EucLens-win-x64.exe","other.zip"]""")]
    [InlineData("""[{"name":"EucLens-win-x64.exe.sha256"}]""")]
    [InlineData("""[{"name":"source.zip"},{"name":"evil.exe"},{"name":"EucLens-0.6.0.tar.gz"},{"name":"../EucLens-win-x64.exe"},{"name":"notEucLens-win-x64.exe"}]""")]
    [InlineData("""[{"name":"EucLens.exe"},{"name":"EucLens-0.6.0-win-x64.exe"},{"name":"EucLens-win-x64.zip"},{"name":"EucLens-win-arm64.exe"}]""")]
    [InlineData("""[{"name":"eucLens-win-x64.exe"},{"name":"EUCLENS-WIN-X64.EXE"},{"name":"EucLens-win-x64.exe "},{"name":" EucLens-win-x64.exe"},{"name":"EucLens-win-x64.exe\n"}]""")]
    public async Task ANewerReleaseWithoutAWindowsBuildIsNotOfferedAndSaysSo(string assets)
    {
        var result = await CheckAsync(Replying(List(Rel("v0.6.0", assets: assets))));
        Assert.Equal(UpdateOutcome.NoCompatibleBuild, result.Outcome);
        Assert.Null(result.Release);
        Assert.Contains("0.6.0", result.Message, StringComparison.Ordinal);
        Assert.Contains("no Windows download", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheExactReleaseAssetNameIsAWindowsBuild()
    {
        Assert.Equal("EucLens-win-x64.exe", GitHubUpdateChecker.WindowsAssetName);
        var result = await CheckAsync(Replying(List(Rel("v0.6.0", assets: Assets("EucLens-win-x64.exe")))));
        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
        // The convention is documented where the next release will be made.
        Assert.Contains("EucLens-win-x64.exe", Source("docs/ABOUT_AND_UPDATES.md"), StringComparison.Ordinal);
        Assert.Contains("EucLens-win-x64.exe", Source("docs/STABLE_RELEASE_0.5.1.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATagThatIsNotAVersionIsIgnoredAndASourceArchiveIsNeverABinary()
    {
        // Tags such as "nightly" or "euc-lens-0.6" are not versions; the release that has a real tag decides.
        var result = await CheckAsync(Replying(List(Rel("nightly"), Rel("euc-lens-0.6"), Rel("v0.6.0", assets: Assets("EucLens-win-x64.exe")))));
        Assert.Equal(V("0.6.0"), result.Release!.Version);
        // Only a source archive: GitHub lists none as an asset, so there is no build.
        var sourceOnly = await CheckAsync(Replying(List(Rel("v0.6.0", assets: "[]"))));
        Assert.Equal(UpdateOutcome.NoCompatibleBuild, sourceOnly.Outcome);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{\"message\":\"object, not a list\"}")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("[1,2,3]")]
    [InlineData("[null,[],\"x\"]")]
    [InlineData("[{\"tag_name\":123}]")]
    [InlineData("[{\"tag_name\":\"../../evil\"}]")]
    [InlineData("[{\"tag_name\":\"v1.0.0\\u0000\"}]")]
    [InlineData("[{}]")]
    [InlineData("[{\"tag_name\":\"v0.6.0\"")]
    public async Task MalformedReplyDataIsIgnoredAndNeverThrows(string json)
    {
        var result = await CheckAsync(Replying(json));
        Assert.Equal(UpdateOutcome.InvalidResponse, result.Outcome);
        Assert.Null(result.Release);
        Assert.Equal("GitHub's answer could not be understood, so it was ignored.", result.Message);
    }

    [Fact]
    public async Task DeeplyNestedJsonOversizedTagsAndHugeListsAreBounded()
    {
        var deep = new string('[', 5000) + new string(']', 5000);
        Assert.Equal(UpdateOutcome.InvalidResponse, (await CheckAsync(Replying(deep))).Outcome);
        var longTag = "v1." + new string('9', 70) + ".0";
        Assert.Equal(UpdateOutcome.InvalidResponse, (await CheckAsync(Replying(List(Rel(longTag))))).Outcome);
        // 3,000 entries: only the first 50 are read, so a newer release far down the list is not seen (and the list stays under the size limit).
        var many = Enumerable.Range(0, 3000).Select(i => i == 2000 ? Rel("v9.0.0") : Rel("v0.1." + (i % 100))).ToArray();
        var result = await CheckAsync(Replying(List(many)));
        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task HostileTextInTheReplyIsReducedToPlainBoundedText()
    {
        var body = "## Release\u202E gnp.exe\n\n[Download now](javascript:alert(1)) and [site](https://evil.example/x)\n<script>alert(1)</script>\u200B**bold**`code`\n" + new string('x', 5000) + "\nline five\nline six";
        var result = await CheckAsync(Replying(List(Rel("v0.6.0", body: body, name: "Name\u202E with\nnewline and \u0007 bell"))));
        var release = result.Release!;
        Assert.DoesNotContain("javascript", release.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", release.Summary, StringComparison.Ordinal);
        Assert.Contains("Download now", release.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain('\u202E', release.Summary + release.Title);
        Assert.DoesNotContain('\u200B', release.Summary);
        Assert.DoesNotContain('\u0007', release.Title);
        Assert.DoesNotContain("**", release.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("`", release.Summary, StringComparison.Ordinal);
        Assert.True(release.Summary.Length <= 320);
        Assert.True(release.Summary.Count(c => c == '\n') <= 3);
        Assert.DoesNotContain('\n', release.Title);
        Assert.True(release.Title.Length <= 100);
        // Whatever the text said, the address is the repository's own page for the validated tag.
        Assert.True(SafeLinks.IsOfficial(release.PageUri));
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("1850-01-01T00:00:00Z")]
    [InlineData("2999-01-01T00:00:00Z")]
    [InlineData("")]
    public async Task AnImplausibleReleaseDateIsLeftOutInsteadOfShown(string published)
    {
        var result = await CheckAsync(Replying(List(Rel("v0.6.0", published: published))));
        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
        Assert.Null(result.Release!.PublishedAt);
        var about = new AboutViewModel(new AboutDesktop(), new CountingChecker(() => result));
        about.CheckForUpdatesCommand.Execute(null);
        Assert.Equal("", about.ReleaseDateText);
    }

    [Fact]
    public async Task ANonJsonOrOversizedReplyIsRejected()
    {
        Assert.Equal(UpdateOutcome.InvalidResponse, (await CheckAsync(Replying(List(Rel("v0.6.0")), "text/html"))).Outcome);
        var padded = List(Rel("v0.6.0", body: new string('a', GitHubUpdateChecker.MaximumResponseBytes)));
        Assert.Equal(UpdateOutcome.InvalidResponse, (await CheckAsync(Replying(padded))).Outcome);
        // No Content-Length: the stream itself is counted.
        var streamed = new FakeHandler((_, _) =>
        {
            var content = new StreamContent(new RepeatingStream(GitHubUpdateChecker.MaximumResponseBytes * 3));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        Assert.Equal(UpdateOutcome.InvalidResponse, (await CheckAsync(streamed)).Outcome);
    }

    private sealed class RepeatingStream(long length) : Stream
    {
        private long position;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { var n = (int)Math.Min(count, length - position); Array.Fill(buffer, (byte)' ', offset, n); position += n; return n; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---------- redirects ----------

    private static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.MovedPermanently)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    [Fact]
    public async Task ARedirectWithinTheOfficialApiIsFollowedWithTheSameAnonymousRequest()
    {
        var handler = new FakeHandler((_, n) => Task.FromResult(n == 1 ? Redirect("https://api.github.com/repositories/123456/releases?per_page=30") : Json(List(Rel("v0.6.0")))));
        var result = await CheckAsync(handler);
        Assert.Equal(UpdateOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal(2, handler.Seen.Count);
        Assert.Equal("https://api.github.com/repositories/123456/releases?per_page=30", handler.Seen[1].Url);
        Assert.Equal(handler.Seen[0].Headers, handler.Seen[1].Headers);
        // A relative redirect stays on the same host.
        var relative = new FakeHandler((_, n) => Task.FromResult(n == 1 ? Redirect("/repos/vdesktopninja/RDA/releases?per_page=30", HttpStatusCode.TemporaryRedirect) : Json("[]")));
        Assert.Equal(UpdateOutcome.NoPublicRelease, (await CheckAsync(relative)).Outcome);
    }

    [Theory]
    [InlineData("http://api.github.com/repos/vdesktopninja/RDA/releases")]
    [InlineData("https://evil.example/repos/vdesktopninja/RDA/releases")]
    [InlineData("https://api.github.com.evil.example/repos/vdesktopninja/RDA/releases")]
    [InlineData("https://api.github.com@evil.example/repos/vdesktopninja/RDA/releases")]
    [InlineData("https://user:pw@api.github.com/repos/vdesktopninja/RDA/releases")]
    [InlineData("https://api.github.com:8443/repos/vdesktopninja/RDA/releases")]
    [InlineData("https://github.com/vdesktopninja/RDA/releases")]
    [InlineData("https://api.github.com/user")]
    [InlineData("https://api.github.com/")]
    [InlineData("ftp://api.github.com/repos/x/y")]
    [InlineData("file:///etc/passwd")]
    public async Task ARedirectAnywhereElseEndsTheCheckWithoutAnotherRequest(string location)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Redirect(location, HttpStatusCode.Found)));
        var result = await CheckAsync(handler);
        Assert.Equal(UpdateOutcome.UnsafeRedirect, result.Outcome);
        Assert.Single(handler.Seen);                      // the unsafe target was never contacted
        Assert.Equal("The answer pointed somewhere other than GitHub's release service, so it was ignored.", result.Message);
    }

    [Fact]
    public async Task TooManyRedirectsAndARedirectWithoutATargetAreRefused()
    {
        var loop = new FakeHandler((_, n) => Task.FromResult(Redirect($"https://api.github.com/repos/vdesktopninja/RDA/releases?hop={n}")));
        Assert.Equal(UpdateOutcome.UnsafeRedirect, (await CheckAsync(loop)).Outcome);
        Assert.Equal(GitHubUpdateChecker.MaximumRedirects + 1, loop.Seen.Count);
        var none = Replying(new HttpResponseMessage(HttpStatusCode.Found));
        Assert.Equal(UpdateOutcome.UnsafeRedirect, (await CheckAsync(none)).Outcome);
    }

    // ---------- offline, timeouts, limits ----------

    [Fact]
    public async Task OfflineProxyAndFirewallFailuresAreNeutralAndDoNotQuoteTheException()
    {
        foreach (var failure in new Exception[]
        {
            new HttpRequestException("SECRET-host-12345 could not be resolved"),
            new HttpRequestException("proxy", new SocketException((int)SocketError.HostUnreachable)),
            new IOException("SECRET connection reset"),
            new InvalidOperationException("SECRET misconfigured"),
        })
        {
            var result = await CheckAsync(Throwing(failure));
            Assert.Equal(UpdateOutcome.Offline, result.Outcome);
            Assert.DoesNotContain("SECRET", result.Message, StringComparison.Ordinal);
            Assert.Contains("works the same offline", result.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ACertificateProblemIsReportedAndNeverBypassed()
    {
        var result = await CheckAsync(Throwing(new HttpRequestException("ssl", new AuthenticationException("The remote certificate is invalid SECRET"))));
        Assert.Equal(UpdateOutcome.SecureConnectionFailed, result.Outcome);
        Assert.DoesNotContain("SECRET", result.Message, StringComparison.Ordinal);
        // The production handler uses the operating system's trust: no callback that accepts everything.
        var source = Source("src/EucLens.Core/Updates/GitHubUpdateChecker.cs");
        Assert.DoesNotContain("ServerCertificateCustomValidationCallback", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DangerousAcceptAnyServerCertificateValidator", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoteCertificateValidationCallback", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ServicePointManager", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASilentServerEndsAsATimeoutInsteadOfBlocking()
    {
        var waiting = new TokenHandler();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await CheckAsync(waiting, timeout: TimeSpan.FromMilliseconds(150));
        Assert.Equal(UpdateOutcome.TimedOut, result.Outcome);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Contains("did not answer in time", result.Message, StringComparison.Ordinal);
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task TheUserCanCancelAndThatIsNotATimeout()
    {
        using var cancel = new CancellationTokenSource();
        var pending = CheckAsync(new TokenHandler(), token: cancel.Token);
        cancel.CancelAfter(100);
        var result = await pending;
        Assert.Equal(UpdateOutcome.Cancelled, result.Outcome);
    }

    [Theory]
    [InlineData(403, "0", null)]
    [InlineData(403, null, "60")]
    [InlineData(429, null, null)]
    public async Task ARateLimitIsExplainedAndNothingElseHappens(int status, string? remaining, string? retryAfter)
    {
        var response = Json("""{"message":"API rate limit exceeded"}""", status: (HttpStatusCode)status);
        if (remaining is not null) response.Headers.Add("x-ratelimit-remaining", remaining);
        if (retryAfter is not null) response.Headers.Add("Retry-After", retryAfter);
        var result = await CheckAsync(Replying(response));
        Assert.Equal(UpdateOutcome.RateLimited, result.Outcome);
        Assert.StartsWith("GitHub's request limit was reached.", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("exceeded", result.Message, StringComparison.Ordinal);       // the server's own text is never shown
    }

    [Fact]
    public async Task ARateLimitResetTimeIsShownOnlyWhenPlausible()
    {
        var now = new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        async Task<string> Message(string reset)
        {
            var response = Json("{}", status: HttpStatusCode.Forbidden);
            response.Headers.Add("x-ratelimit-remaining", "0"); response.Headers.Add("x-ratelimit-reset", reset);
            using var checker = new GitHubUpdateChecker(Replying(response), time: clock);
            return (await checker.CheckAsync(Installed, false, default)).Message;
        }
        Assert.Contains("can be tried again after", await Message(now.AddMinutes(20).ToUnixTimeSeconds().ToString()), StringComparison.Ordinal);
        Assert.DoesNotContain("again after", await Message(now.AddDays(30).ToUnixTimeSeconds().ToString()), StringComparison.Ordinal);
        Assert.DoesNotContain("again after", await Message(now.AddMinutes(-5).ToUnixTimeSeconds().ToString()), StringComparison.Ordinal);
        Assert.DoesNotContain("again after", await Message("<script>"), StringComparison.Ordinal);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task AServerErrorIsReportedWithoutItsBody(int status)
    {
        var result = await CheckAsync(Replying(Json("SECRET stack trace", status: (HttpStatusCode)status)));
        Assert.Equal(UpdateOutcome.ServiceUnavailable, result.Outcome);
        Assert.DoesNotContain("SECRET", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(418)]
    [InlineData(204)]
    [InlineData(304)]
    public async Task AnyOtherStatusIsNotTrustedAsAnAnswer(int status)
    {
        var result = await CheckAsync(Replying(Json("[]", status: (HttpStatusCode)status)));
        Assert.Equal(UpdateOutcome.InvalidResponse, result.Outcome);
    }

    [Theory]
    [InlineData("http://github.com/vdesktopninja/RDA")]
    [InlineData("https://evil.example/vdesktopninja/RDA")]
    [InlineData("https://github.com/vdesktopninja")]
    [InlineData("https://github.com/vdesktopninja/RDA/extra")]
    [InlineData("https://github.com/../etc")]
    [InlineData("not a url")]
    public void TheCheckerOnlyAcceptsAGitHubRepositoryOverHttps(string repository) =>
        Assert.Throws<ArgumentException>(() => new GitHubUpdateChecker(Replying("[]"), repository));

    [Fact]
    public void TheProductionHandlerFollowsNoRedirectsAndKeepsNoCookies()
    {
        var source = Source("src/EucLens.Core/Updates/GitHubUpdateChecker.cs");
        foreach (var expected in new[] { "AllowAutoRedirect = false", "UseCookies = false", "UseProxy = true", "ConnectTimeout", "MaxResponseHeadersLength", "MaximumResponseBytes = 1 << 20", "DefaultTimeout { get; } = TimeSpan.FromSeconds(15)" })
            Assert.Contains(expected, source, StringComparison.Ordinal);
        Assert.Equal(15, GitHubUpdateChecker.DefaultTimeout.TotalSeconds);
        Assert.Equal(3, GitHubUpdateChecker.MaximumRedirects);
    }

    [Fact]
    public async Task UpdateCheckingNeverTouchesMonitoringOrRecording()
    {
        var recorder = new SessionRecorder();
        var vm = new MainViewModel(new MockMetricsProvider(), new DiagnosticIntelligenceEngine(DiagnosticRuleSet.Default()), recorder, new AboutDesktop(), NullLogger<MainViewModel>.Instance,
            updateChecker: new FaultingChecker());
        var statusBefore = vm.Status;
        await vm.About.CheckForUpdatesCommand.ExecuteAsync();
        Assert.False(recorder.IsRecording);
        Assert.Equal(statusBefore, vm.Status);
        Assert.Equal("The update check could not be completed.", vm.About.UpdateStatus);
    }

    private sealed class FaultingChecker : IUpdateChecker
    {
        public Task<UpdateCheckResult> CheckAsync(ReleaseVersion installed, bool includePrereleases, CancellationToken cancellationToken) => Task.FromException<UpdateCheckResult>(new HttpRequestException("SECRET"));
    }
}

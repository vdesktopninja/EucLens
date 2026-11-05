# Application identity, About and update checking (0.5.1)

Presentation and one opt-in feature. Nothing here changes monitoring, diagnostic rules, thresholds, Session History, chart navigation or the recording format.

## 1. Identity: one source

`Directory.Build.props` is the only place the identity is written. Every assembly carries it, Windows reads it from the executable, and the application reads the same
values back (`AppIdentity`), so the About page cannot disagree with Explorer.

| Field | Value | Where it appears |
|---|---|---|
| Product | EUC Lens | Properties → Details → Product name |
| Description | Remote Session Performance Diagnostics | Comments; the file description reads "EUC Lens - Remote Session Performance Diagnostics" (the name Task Manager shows) |
| Version | 0.5.1 (`<Version>`; file version 0.5.1.0) | Product version, File version, About, navigation footer, recordings |
| Maintainer / publisher | vdesktopninja | Company name. **Pending the owner's confirmation of the public identity.** No company or legal entity is named. |
| Copyright | Copyright (c) 2026 EUC Lens contributors | The first copyright line of `LICENSE`, unchanged |
| License | MIT (`LICENSE`) | About, assembly metadata |
| Repository | https://github.com/vdesktopninja/RDA | About, update check |
| Commit | the build's commit (`-p:EucLensCommit=<sha>`; the Windows workflow passes `github.sha`) | About ("Build"), as an abbreviated hexadecimal id. A local build says "not recorded (local build)". It is deliberately **not** appended to the product version, so Explorer shows exactly 0.5.1. |

The version is not typed in any view; a test fails if a XAML file or the About code contains a version literal. `app.manifest` keeps its own, unrelated assembly identity.

CI verifies the published executable's product name, file description, comments, company, copyright, product version (exactly `0.5.1`) and file version (`0.5.1.0`),
and prints them; the smoke test reads the same fields from the running process and compares them with the About page's source.

## 2. The About page

Navigation → **About** (last item). It shows the logo, EUC Lens, the description, version, build, platform ("Windows 10 (version 2004) or later and Windows 11, x64" — the
architecture is the running process's), maintainer, copyright and MIT license, a **Copy version information** button (product, version, build, platform, .NET runtime and Windows
version; no computer name, user name or address), **Open GitHub repository**, the update area (section 3), and three disclosures: the release notes of this version (the
`CHANGELOG.md` section of exactly this version), the MIT license text and the open-source acknowledgments (`THIRD_PARTY_NOTICES.md`). The legal texts are the repository's own files embedded
into the executable; a test compares them with the files and with the restored package list (every restored package must be acknowledged).

The navigation footer shows "Version 0.5.1" discreetly. No release-channel label is shown: this build is an unpublished release candidate, so it is not called Stable.
The page uses the shared typography tokens and both themes; the Windows smoke test checks it at every size, scale and theme, with all disclosures open, and writes screenshots.

## 3. Update checking

**Check for updates** is the only way a check starts. Opening About or Settings, changing an option, startup and the background make no request (a test counts requests; the
smoke test asserts that none was made before the button was pressed). There is no automatic check and no preference for one in this version.

What a check does:

* One HTTPS GET to `https://api.github.com/repos/vdesktopninja/RDA/releases?per_page=30`. Headers: `Accept`, `X-GitHub-Api-Version` and `User-Agent: EucLens/<version>` (the same for everyone). No credentials, cookies, identifiers, machine or user names; nothing about the session or recordings.
* **Redirects** are followed by hand, at most three, and only to the same API host (`api.github.com`) over HTTPS on the default port, without user information, under `/repos/` or `/repositories/`. Anything else ends the check ("unsafe redirect") before the target is contacted.
* **Bounds**: 15 s overall timeout, 8 s connect timeout, 1 MiB response (counted after decompression, also when no length is announced), JSON depth 16, at most 50 releases read, bounded text lengths.
* **Trust**: the operating system's certificate store and the system proxy (with the user's own Windows credentials for the proxy, as a browser does). A certificate error ends the check; it is never ignored. Nothing bypasses a corporate proxy, firewall or inspection policy.
* **Parsing is defensive.** The reply is data. Only `tag_name`, `name`, `draft`, `prerelease`, `published_at`, `body` and the asset names are read, each validated. Drafts are skipped. A tag must be a strict semantic version (optional leading `v`); anything else is ignored. The release **page address is built from the validated tag**
  (`https://github.com/vdesktopninja/RDA/releases/tag/<tag>`), never copied from the reply. The title and summary are reduced to plain text: markup and links removed, control, zero-width and direction-override characters removed, at most 4 lines / 320 characters.
  A date that is not plausible (before 2008 or in the future) is left out.
* **Semantic versions are compared by number**, never as text: 0.10.0 is newer than 0.9.0; 1.0.0-rc.1 is older than 1.0.0. The newest release is chosen by version, not by list order or date.
* **Pre-releases** (the flag or a pre-release tag) are never offered unless the user ticks *Include pre-releases (for testing)* on that visit; the choice is not saved.
* **Windows builds only.** A release counts only if it has an asset named like the portable executable (`EucLens…exe`, or `EucLens…win….zip`). Tags and the source archives GitHub generates are not binaries and are never offered. A newer release without such an asset
  is reported as "listed on GitHub but has no Windows download attached yet".
* **Results are ordinary messages**: update available; latest version; no public release (also for a private repository, which answers 404 anonymously); newer release without a Windows build; offline/proxy/firewall; secure connection could not be verified; timeout; rate limit (with a reset time only when plausible); GitHub unavailable;
  unreadable answer; unsafe redirect; cancelled. A message never quotes the response or an exception. The UI thread is never blocked (the request is awaited, the button is disabled and the page says it is checking), and monitoring and recording are unaffected whatever happens.

When a newer compatible release exists, the page shows the installed version, the latest version, the release date (UTC), a short plain summary, **View release notes** and **Download update**. **Download update opens the official release page in the system browser**
(`SafeLinks` allows only pages of this repository on github.com over HTTPS; the same check runs again in the desktop service). EUC Lens does not download, save, open or run anything, and does not replace itself.

## 4. Privacy and enterprise behavior

EUC Lens still works fully offline. The only network access in the application is `GitHubUpdateChecker`, which exists in one file, is created only when the button is first pressed, and is only constructed by the About page's view model; a source test enforces both. No telemetry, no identifiers, no administrator rights.
An administrator who does not want even the manual check can block `api.github.com` for the executable: the button then reports "GitHub could not be reached" and nothing else changes.

## 5. Testing

* `AboutAndUpdateTests`: identity and metadata, version single source, About bindings, legal texts and acknowledgments, no request without consent, no network code outside the checker, link allowlist, semantic-version ordering and rejection, every outcome with mocked HTTP replies (valid, none, newer, older/identical, pre-release, draft, no build, malformed and malicious JSON,
  hostile text, oversize, redirects, offline, certificate, timeout, cancellation, rate limit, server errors), responsiveness while a check is pending. No test contacts GitHub, so CI does not depend on its availability.
* Windows smoke test (`AboutValidation`): the executable's metadata, no request before consent, the real controls (the button is pressed through UI Automation) with a fake checker for update available, no release, offline and a pending check (responsiveness), the legal text open with the typography checks, screenshots in both themes.
* Not tested: a real request to GitHub's API (by design), real corporate proxies and TLS inspection, a real browser opening, a screen reader, and Windows 10 versus 11 font fallback (the CI runner has Segoe UI Variable).

See [SELF_UPDATE_DESIGN.md](SELF_UPDATE_DESIGN.md) for the design of an optional self-update in a later version (not implemented).

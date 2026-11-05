# Optional self-update — design for a later version (not implemented in 0.5.1)

Status: proposal. 0.5.1 can only *tell* the user that a newer release exists and open its page ([ABOUT_AND_UPDATES.md](ABOUT_AND_UPDATES.md)). Nothing in 0.5.1 downloads or replaces an executable.
This document records what a self-update would have to guarantee, so that it can be reviewed before any code is written.

## Principles

1. **Never mandatory, never silent.** The user starts it and sees what will happen. No forced version, no automatic restart, no background download without consent.
2. **Administrator control first.** A machine-wide policy (a registry value under HKLM and an `updates.disabled` file next to the executable) turns the feature off; when off, the button is absent, not just disabled. Default for a new feature: off in managed environments, opt-in elsewhere.
3. **No weaker than a manual download.** The update path must not be a way to run code that a careful administrator would not have accepted by downloading the file by hand.
4. **Nothing is lost.** A running recording is finalized (or the update is deferred) before the executable is replaced.

## Architecture

* **Check** (exists in 0.5.1): the release list over HTTPS from one fixed host, with bounded replies and strict parsing.
* **Verify before use** (new): the chosen release's Windows asset is downloaded to a private temporary folder of the user, over HTTPS from `github.com`/`objects.githubusercontent.com` only (redirect allowlist as in the checker), with a size cap taken from the release metadata and a hard limit; resumable downloads are not used (an interrupted download is discarded and restarted).
  Before anything is launched it must pass **all** of:
  * **Integrity**: the SHA-256 of the file equals the digest published for that asset (the release's `digest` field or a `SHA256SUMS` asset), compared in constant time. A mismatch deletes the file and reports an error.
  * **Provenance**: the digest must come from the same release, and the release must belong to the official repository over the API (not from a URL in the reply). Preferably also a build attestation (GitHub artifact attestation / SLSA provenance) verified against the repository and workflow identity.
  * **Authenticode**: when the project signs releases, the file must carry a valid signature chaining to the pinned publisher certificate (thumbprint or subject plus issuer pinned in the application), timestamped. Until a code-signing certificate exists, signature verification is skipped *and the user is told so*; unsigned self-update must stay opt-in and off in managed environments.
  * **Version policy**: the new version is strictly newer by semantic version (no downgrade, no pre-release unless the user chose that channel), and the file's product name and file version match the release.
* **Replace** (new), designed for a portable single-file executable that may live where the user cannot write:
  * Writable location (same folder, user-owned): download next to the executable as `EucLens.exe.new`, then a small helper step performs an **atomic rename sequence** (`EucLens.exe` → `EucLens.exe.old`, `.new` → `EucLens.exe`, using `File.Replace` / `MoveFileEx`), starts the new executable and removes `.old` after the new one has started successfully once. On any failure the helper renames `.old` back (**rollback**) and the user keeps the old version with an explanation.
  * Read-only or protected location (Program Files, a read-only share, an application-virtualization layer): **no replacement is attempted** and no elevation is requested. EUC Lens saves the verified file to the user's Downloads folder and shows its location and hash; installing it is the user's decision.
  * A running executable cannot be overwritten on Windows; the helper is therefore a second copy of the *new* verified executable started with a `--finish-update <old-path> <pid>` argument that waits for the old process to exit, performs the rename sequence and relaunches. It accepts only paths it was given by the old process through a per-update random token file in the user's temp folder, never from the command line alone.
* **Shutdown safety**: the update only proceeds after the existing exit path finalizes the recording (`Exit` already writes the recording before the process ends). With a recording running the user is asked (stop and update, update later); there is no unattended update during a recording.
* **Several instances and sessions**: the existing single-instance guard is per user and Windows session. An update waits until no other EUC Lens process of the same executable path runs (checked by process image path); other sessions on a shared (RDS/VDI) host that run the same file block replacement of a shared copy, and the user is told which process holds it. Per-user copies update independently.
* **Remote Desktop and VDI**: no update is offered when the executable path is on a redirected drive, a UNC path or a read-only/non-persistent volume (a non-persistent VDI image loses the replacement at the next logon, so the user is told to update the image instead). The download uses the same proxy and certificate settings as the check.

## Failure handling

| Case | Behaviour |
|---|---|
| Interrupted or truncated download | Temporary file deleted; nothing replaced; the user may try again. |
| Hash mismatch / bad signature / unexpected version | Temporary file deleted; clear error; the release is not offered again until the list changes. |
| Disk full, quota, antivirus quarantine | Reported; old executable untouched. |
| New executable fails to start | Rollback to `.old`; the failure is recorded in the local log. |
| Power loss between renames | On next start, if `EucLens.exe` is missing and `EucLens.exe.old` exists, the helper-less launcher path restores it; a leftover `.new` is deleted. |
| User cancels | Everything temporary is removed. |

## Security assumptions and limits

* TLS to GitHub and the operating system's trust store are trusted; a TLS-inspecting proxy that the machine trusts can see (not alter, thanks to the digest and signature) the download.
* The release author's GitHub account and CI are trusted; **a compromised release account is the main risk**. Mitigations: digest from a different channel than the file where possible, signed commits and attestations, a signing key not stored in the repository, release approval by a second person.
* The checksum alone does not protect against a compromised release (the attacker can change both). Authenticode with a pinned certificate or an attestation is what raises the bar; without them the feature is only as safe as the account.
* The update mechanism must not add telemetry, identifiers or an always-on connection; there is no background service or scheduled task.
* Enterprise distribution (software-distribution tools, a managed share) stays the recommended path; self-update is a convenience for unmanaged portable use.

## Decisions needed from the owner before implementation

1. Whether to obtain a code-signing certificate (and which publisher name); until then self-update is not recommended beyond testing.
2. Asset naming and a `SHA256SUMS` convention for releases (the 0.5.1 check already requires an `EucLens…exe` or `EucLens…win….zip` asset).
3. Default state of the feature (off unless enabled in Settings is recommended) and whether an administrator policy is required for it to exist at all.
4. Whether the optional *automatic check* preference (default off, disclosed in Settings) is wanted at all, separately from self-update.

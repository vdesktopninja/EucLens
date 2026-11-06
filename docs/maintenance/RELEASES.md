# Releases and provenance

For maintainers. Users should read the [README](../../README.md) and the release page.

## Naming convention (the update check depends on it)

The in-application *Check for updates* reads GitHub's public release list for this repository and offers a release only if:

- the tag is a semantic version: `vMAJOR.MINOR.PATCH` for a stable release (for example `v0.5.1`), or with a suffix such as `v0.6.0-rc.1` for a pre-release. A suffixed tag counts as a pre-release even if the release is not flagged, so publish it as a pre-release; users see it only if they opt in. Any other tag form is ignored;
- the release is not a draft, and a stable release is not marked *pre-release*;
- it has an asset named **exactly `EucLens-win-x64.exe`**. Tags, source archives, checksum files and every other name are never offered.

Each release also carries `SHA256SUMS.txt` (`<hash>  EucLens-win-x64.exe`). The application never downloads or runs anything: *Download update* opens the release page in the browser. Details: [About and updates](../ABOUT_AND_UPDATES.md).

## Releasing

1. Build and test the exact commit with the Windows CI workflow (`workflow_dispatch` with *upload_portable*), and keep the produced `EucLens.exe` byte-for-byte. Builds are not reproducible, so never rebuild after approval and expect a different hash from every build.
2. Verify the CI result: tests, smoke test, launch, credential scan, and the executable's version, publisher and SHA-256.
3. Create an annotated tag `vX.Y.Z` on that commit (the commit that was built; it is embedded in the About page). Do not squash or rebase between the build and the tag.
4. Create the release with the executable renamed to `EucLens-win-x64.exe` and a `SHA256SUMS.txt` that names the renamed file. The checksum file CI produces (`EucLens.exe.sha256`) names `EucLens.exe`, so write the release's file yourself (`<hash>  EucLens-win-x64.exe`; the hash is the same) and verify it with `sha256sum -c`. Compare GitHub's stored digest of the uploaded asset with the CI hash.
5. After publishing, check as an anonymous user: the release list API shows the release and the asset, the downloaded file hashes to the published value, and *Check for updates* in the previous build offers it.

The executable is **not code-signed**. A checksum shows that a file is the published one; it does not prove who published it.

## Provenance of v0.5.1

- The release executable (`EucLens-win-x64.exe`, SHA-256 `13d44936e37ac30ee645902e8a5cc8d1447dc9f471f71d56d311d486112b826a`) was built by Windows CI from the stable line's final commit, in the maintainers' private development repository (run 42206177123, 1,069 of 1,069 tests). This repository's [CI run 42213774685](https://github.com/vdesktopninja/EucLens/actions/runs/42213774685) tested the same source tree and produced a different binary, as expected.
- **Commit hashes.** The About page of the v0.5.1 executable shows the build commit `fe010d2`. On 2026-11-06, after the release, the history of this repository was rewritten once to remove trailer lines from commit messages. Only messages changed: every commit keeps its file contents, author, committer and dates. As a result commit hashes changed from the first rewritten commit on, and `fe010d2` is now commit `0454bc0b97ceb2393875006e7abd8f8956040e18`. The tag `v0.5.1` points to it. To check that nothing else changed, compare the trees: `git rev-parse 0454bc0^{tree}` prints `9a00f1f16350d6ad62607f82dfa892ff3dcf4286`, the tree of the commit that was built.

| Build commit shown in the application | Commit in this repository |
|---|---|
| `fe010d2` (v0.5.1) | `0454bc0` |
| `6e3ded2` (v0.5.0 source) | `a278909` |

## CI artifacts

Workflow runs keep the portable executable for 3 days and the validation results for 1 day. Release assets are the permanent copies.

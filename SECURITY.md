# Security policy

## Reporting a vulnerability

Please report a suspected vulnerability **privately**, not in a public issue:

1. Open the repository's [Security tab](https://github.com/vdesktopninja/EucLens/security/advisories/new) and choose **Report a vulnerability**.
2. Describe what you found, the EUC Lens version (shown on the About page), the Windows version and how to reproduce it. Do not include recordings or logs that contain data you do not want to share.

This is an independent open-source project maintained by one person, so there is no guaranteed response time. Reports are read and answered on a best-effort basis.

## Supported versions

Only the latest stable release receives fixes. The current release is listed on the [releases page](https://github.com/vdesktopninja/EucLens/releases/latest).

## What EUC Lens does and does not do

EUC Lens runs as a standard user, has no server component and no telemetry, and makes a network request only when you press **Check for updates** (see [Privacy and security](README.md#privacy-and-security)). The executable is not code-signed, so verify its SHA-256 against the release page before running it.

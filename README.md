# DevBrowser

A developer-focused browser for Windows.

## Features

- Web browsing
- Built in and integrated REST client
- Network and storage inspection
- HAR capture and inspection
- Bookmarks and history

## Requirements

- Windows 10 version 2004 or newer
- .NET 9 SDK (for building)

## Install

1. Download `DevBrowser.cer` and `DevBrowser.appinstaller` from the latest GitHub release.
2. Open `DevBrowser.cer` and select **Install Certificate**.
3. Select **Local Machine**.
4. Choose **Place all certificates in the following store**.
5. Select **Trusted People** and complete the import.
6. Open `DevBrowser.appinstaller` and select **Install**.

Only trust the certificate when it was downloaded from the official DevBrowser GitHub repository.

## Build

```powershell
dotnet build DeveloperBrowser.slnx
```

## Create a release

Install Git and [GitHub CLI](https://cli.github.com/), then sign in with `gh auth login`.
Commit your changes before releasing. The GitHub repository needs Actions enabled and
the `SIGNING_CERTIFICATE_BASE64` and `SIGNING_CERTIFICATE_PASSWORD` secrets configured.

Edit the defaults at the top of `scripts/Create-Release.ps1`, or pass a version/tag:

```powershell
# Preview without creating or pushing a tag (Git required; no GitHub access needed).
.\scripts\Create-Release.ps1 -Version '1.2.3' -WhatIf

# Stable release from HEAD using origin.
.\scripts\Create-Release.ps1 -Version '1.2.3'

# Beta release, optionally selecting another commit or remote.
.\scripts\Create-Release.ps1 -Tag 'v1.2.4-beta.1' -Target 'HEAD' -Remote 'origin'
```

An explicit `Tag` takes precedence over the default `Version`; if both are passed,
they must match. The script creates an annotated tag and pushes only that tag.
The existing `release-msix.yml` workflow builds and signs the installer, generates
release notes, and publishes the GitHub release. Beta tags become prereleases.
Stable MSIX versions use revision `65535`; beta numbers must be `1` through `65534`.
No version files need to be edited: the workflow derives versions from the tag.

The script prints the Actions and release links; a successful push means the build
has started, not that publication has succeeded. If the push fails, rerun with the
same tag and target to reuse the local tag. If a remote tag already exists, inspect
or rerun its failed workflow in GitHub Actions. Existing tags are never overwritten.
Preview skips authentication, cleanliness, and remote tag checks.

## Diagnostics and privacy

DevBrowser writes rolling local diagnostic logs to `%LOCALAPPDATA%\DevBrowser\Logs`. Use **Open log folder** in the application menu to view them.

Anonymous diagnostics are disabled until the user makes a choice on first launch. They can be enabled or disabled later from the application menu. When enabled, DevBrowser sends one `first_launch` event for an approximate installation count. If a failure occurs, it also sends the exception type and a sanitized stack trace. Events include application/platform version information and go to the project's EU-hosted Sentry endpoint. They do not include URLs, headers, cookies, request or response bodies, tokens, environment values, or browser storage.

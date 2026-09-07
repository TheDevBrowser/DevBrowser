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

## Diagnostics and privacy

DevBrowser writes rolling local diagnostic logs to `%LOCALAPPDATA%\DevBrowser\Logs`. Use **Open log folder** in the application menu to view them.

Anonymous diagnostics are disabled until the user makes a choice on first launch. They can be enabled or disabled later from the application menu. When enabled, DevBrowser sends one `first_launch` event for an approximate installation count. If a failure occurs, it also sends the exception type and a sanitized stack trace. Events include application/platform version information and go to the project's EU-hosted Sentry endpoint. They do not include URLs, headers, cookies, request or response bodies, tokens, environment values, or browser storage.

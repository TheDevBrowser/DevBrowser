# Releasing DevBrowser

DevBrowser is distributed as a signed x64 MSIX package through GitHub Releases.

## One-time GitHub configuration

Create one long-lived self-signed code-signing certificate and keep its `.pfx` file private. Reuse this certificate for every release so existing installations can update.

Add these GitHub repository secrets:

- `SIGNING_CERTIFICATE_BASE64` — the Base64-encoded contents of the `.pfx` file.
- `SIGNING_CERTIFICATE_PASSWORD` — the password protecting the `.pfx` file.

Never commit the `.pfx` file or its password. The workflow reads the publisher identity directly from the certificate, signs the package, and deletes the imported private certificate from the runner afterward.

## Publish a release

1. Update DevBrowser normally and verify the Release build locally.
2. Create and push a semantic version tag, such as `v1.0.0`, or a beta tag such as `v1.0.0-beta.1`.
3. The release workflow builds, signs, and publishes these files to GitHub Releases:
   - `DevBrowser.appinstaller` — users install this file first and it remains their update source.
   - `DevBrowser.msix` — the self-signed application package.
   - `DevBrowser.cer` — the public certificate users must trust before installation.

Use the stable GitHub Releases download link for installation:

`https://github.com/TheDevBrowser/DevBrowser/releases/latest/download/DevBrowser.appinstaller`

Beta releases use tag-specific installer URLs because GitHub's `latest` release endpoint excludes pre-releases. Install each newer beta manually from its release page. Stable releases use the `latest` URL and retain automatic updates.

The App Installer manifest checks for updates every six hours. DevBrowser also checks at startup and every six hours while it is open, then offers **Relaunch to update** from its status bar.

The package deliberately keeps DevBrowser's existing `%LocalAppData%\DevBrowser` location unvirtualized. Bookmarks, history, collections, environments, and protected local secrets therefore remain in place when a user switches from the unpackaged build to the MSIX release.

## Versioning

`Directory.Build.props` supplies the development version (`1.0.0-dev`) shown in DevBrowser's **About** menu. A release tag is the source of truth for a public build. The `v1.2.3-beta.1` tag produces app version `1.2.3-beta.1` and MSIX version `1.2.3.1`; the stable `v1.2.3` tag produces app version `1.2.3` and MSIX version `1.2.3.65535`, ensuring that Windows considers the stable package newer than its beta packages. Do not manually edit the generated MSIX version values.

## Testing before a public release

Before installing, import `DevBrowser.cer` into **Local Machine > Trusted People**. Windows requires this because the certificate is self-signed. Verify each release on a clean x64 Windows machine with App Installer and the Evergreen WebView2 Runtime available.

### Build a local test installer

In **Visual Studio Installer**, select **Modify** for your Visual Studio installation and install the **Universal Windows Platform development** workload. This is compatible with Windows 11 and supplies the MSIX packaging project tooling and Windows SDK. Alternatively, the **MSIX Packaging Tools** optional component appears under the **.NET desktop development** workload in some Visual Studio versions.

Then open PowerShell **as Administrator**, move to the repository root, and run:

```powershell
.\scripts\Create-LocalInstaller.ps1 -Version 1.0.0.0
```

The script creates a local development certificate, trusts it on this machine, signs the package, and writes `artifacts\local-installer\DevBrowser-1.0.0.0.msix`. This local certificate is separate from the persistent certificate used by GitHub releases.

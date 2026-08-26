# Developer Browser

A Windows developer browser MVP foundation: WPF on .NET 9, Chromium through WebView2, a CDP-ready browser boundary, an `HttpClient` REST-client abstraction, and local SQLite persistence.

## Layout

- `src/DeveloperBrowser.App` — WPF shell and WebView2 host.
- `src/DeveloperBrowser.Core` — feature contracts and domain types.
- `src/DeveloperBrowser.Infrastructure` — EF Core/SQLite, HTTP REST transport, and Windows DPAPI-backed secret storage.

Monaco is intentionally not bundled yet. The WebView2 host keeps the UI ready to add a Monaco-based REST body/editor surface without changing the application boundary.

## Build

```powershell
dotnet build DeveloperBrowser.slnx
```

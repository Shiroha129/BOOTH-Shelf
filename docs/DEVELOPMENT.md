# Build and verification

## Requirements

- Windows 10/11 x64 and .NET 8 SDK.
- Node.js 24 LTS for DOM tests.
- Microsoft Edge WebView2 Runtime for the desktop UI.

The EXE bundles .NET 8.0.33 and uses Microsoft.Web.WebView2 1.0.4314.35. The SDK restores NuGet packages into `.packages/`; neither that cache nor runtime user data belongs in Git.

## Build

```powershell
npm ci
npm test
powershell -NoProfile -ExecutionPolicy Bypass -File desktop/build.ps1 -OutputDirectory dist/publish
powershell -NoProfile -ExecutionPolicy Bypass -File desktop/test.ps1 -Executable dist/publish/BOOTH-Shelf.exe
powershell -NoProfile -ExecutionPolicy Bypass -File desktop/package.ps1 -PublishDirectory dist/publish
```

The last step creates a versioned `dist/BOOTH-Shelf-v*-win-x64.zip` and `dist/SHA256SUMS.txt`. The ZIP contains the EXE, usage instructions, the project license and required runtime notices. Packaging refuses to reuse an existing staging directory to avoid carrying stale files into a release. Choose a fresh output directory with `-OutputDirectory` when packaging again.

## Tests and preview

`npm test` runs offline jsdom tests for library parsing, notifications, local-asset menus, deletion confirmation, and translations. The C# core test covers synchronization, caching, ownership, purchases, currencies, notifications, download selection, local-folder deletion boundaries, and runtime installer control flow using synthetic fixtures in an isolated directory. It does not log in to BOOTH, delete personal assets, or execute a real runtime installer. Native Recycle Bin interaction and installation on a Windows machine without WebView2 require manual verification.

```powershell
npm run preview
```

Open `http://127.0.0.1:5185/?tests=1` for the synthetic browser regression fixture, or `http://127.0.0.1:5185/?scroll=1` for scrolling checks. The server binds only to localhost and serves an explicit file allowlist. Preview downloads are mocked and do not contact a real account.

For documentation screenshots, open `http://127.0.0.1:5185/?showcase=1`, adding `&lang=ja` or `&lang=en` for another language. This runs the actual front end with synthetic library data and original SVG illustrations from `tests/showcase/`. Screenshots in `docs/images/` use the desktop app's default 1320 × 900 window size; the library view is captured beyond the first fold and cropped after its first row. Demo scripts and illustrations are not embedded in the desktop EXE.

The first-run runtime setup uses Microsoft's Evergreen Bootstrapper URL and the documented `/silent /install` arguments. It validates the downloaded file with Windows Authenticode trust and a Microsoft Corporation publisher check before execution. The package does not bundle a browser runtime; offline users can use Microsoft's Evergreen Standalone Installer. Reference: [Microsoft WebView2 distribution guide](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution).

For manual desktop checks, launch the EXE with an isolated directory, for example `--data-dir test-data-manual`. Keep all authentication, cache, export and download files out of commits. The deprecated account-inspection tools and real-account research samples are not included in this repository.

## Structure

Automated BOOTH page reads, metadata, update metadata, authentication probes and download entry requests share one `BoothRequestPolicy`: at most four active requests, with starts at least 250 ms apart. Browser subresources and user interactions in the official login window are not counted as separate scheduler operations. HTTP 403/429 and recognized verification pages pause new work until the user resumes; `Retry-After` is respected, with minimum cooldowns of 10 minutes for 429 and 5 minutes for access denial/verification. Pause state survives restarts. These are engineering defaults, not BOOTH-approved rate limits. See [BOOTH's scraping guidelines](https://booth.pm/guidelines).

Notification eligibility comes from files still present in the local library. The incremental scan may read other recent library entries to locate its time boundary, but only downloaded assets can produce notifications. With no downloaded assets, the monitor sends no requests. Default interval: 60 minutes; settings allow 30, 60, 180, 360 or 1440 minutes. Offline tests cover shared pacing, cross-service suspension, restart persistence, manual recovery and downloaded-only notification scope.

- `desktop/`: WinForms host, WebView2 session, sync, download, local storage and background services.
- `index.html`, `styles.css`, `app.js`, `features.js`: desktop UI.
- `parser.js`, `page-reader.js`, `account-reader.js`: BOOTH page parsing and reads.
- `library-filters.js`, `filter-presets.js`, `notifications.js`: filtering, saved presets and notifications.
- `i18n.js`, `locales.json`: five UI languages and native messages.
- `tests/`: synthetic fixtures and DOM/browser checks.

The GitHub Actions workflow builds and tests on Windows and attaches the packaged ZIP as a workflow artifact. Public releases are published explicitly by the maintainer after reviewing the checks. For a version update, keep the project version, sidebar version, native window title, HTTP user agent and package version consistent.

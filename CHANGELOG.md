# Changelog

## 0.5.11 — 2026-11-10

- Detect WebView2 before startup. When missing, offer an explicit install button that downloads Microsoft's official bootstrapper, verifies its Authenticode signature and publisher, installs the runtime and checks availability again before opening the app.
- Keep download cancellation, retry and a manual download link available; existing installations skip setup entirely.
- Add concise Chinese, Japanese and English introductions with screenshots of the locally running UI using synthetic data and original demo illustrations.

## 0.5.10 — 2026-11-10

First public preview release.

- Add a local asset context menu to open its BOOTH product page or remove its entire asset folder through the Recycle Bin, including extracted files.
- Preview all affected directories and guard against deleting a download root, another asset's files, linked folders, changed directory selections, or an active download.
- Give window controls an opaque fixed background so scrolled content cannot show through or receive input there.
- Include purchased, gifted and free library synchronization, tag filtering and presets, shop views, downloads, local files, spending estimates, currencies, tray mode, session persistence and five UI languages.
- Include incremental file update notifications for synced assets without requiring a download.
- Replace account research samples with synthetic fixtures, document building, and include runtime license notices in distribution packages.

This release is a preview. The unsigned Windows x64 EXE requires Microsoft Edge WebView2 Runtime. DOM and offline core tests do not replace live-site and native UI testing.

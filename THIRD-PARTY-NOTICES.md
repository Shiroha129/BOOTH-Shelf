# Third-party components

BOOTH Shelf's own code is provided under the MIT License in `LICENSE`.

The Windows binary includes these components, whose original notices are in the accompanying `licenses/` folder:

| Component | Version | Notice files |
| --- | --- | --- |
| Microsoft.Web.WebView2 SDK / loader | 1.0.4314.35 | `WEBVIEW2-LICENSE.txt`, `WEBVIEW2-NOTICE.txt` |
| Microsoft.NETCore.App runtime | 8.0.33 | `DOTNET-LICENSE.txt`, `DOTNET-THIRD-PARTY-NOTICES.txt` |
| Microsoft.WindowsDesktop.App runtime | 8.0.33 | `WINDOWSDESKTOP-LICENSE.txt` |

The Microsoft Edge WebView2 browser runtime is installed separately and is not included in this ZIP. Microsoft distributes it under its own terms.

Development tests use jsdom 29.1.1 and its dependencies. They are not embedded in the EXE. Versions are locked in `package-lock.json`; their license files are included with their npm distributions.

BOOTH product artwork, downloadable assets, store names and trademarks remain the property of their respective owners. They are not included in this source repository or release package.

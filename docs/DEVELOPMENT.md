# Development

Requires Windows x64 and the .NET 10 SDK. Packaging also requires Visual Studio C++ x64 build tools and the Windows SDK.

~~~powershell
.\scripts\build.ps1
.\dist\widget\Usage.exe

# Exit the widget before checking its UI.
Start-Process .\dist\widget\Usage.exe -ArgumentList '--check' -Wait
Start-Process .\dist\widget\Usage.exe -ArgumentList '--check-widget' -Wait

# Build the installer and checksum. No portable or script-installer release.
.\scripts\package.ps1
~~~

## Layout

| Folder | Contents |
| --- | --- |
| src/HowsMyUsage | WinForms app, UI, settings and quota providers |
| installer | Native installer, uninstaller and shared shortcut integration |
| runtime | Startup launcher |
| assets | Provider and application icons |
| scripts | Build and packaging |
| tools | Asset generation tools |
| docs | Screenshots and licenses |
| .build, artifacts, dist | Ignored caches, validation output and releases |

Packaging uses the publish manifest, bundles the native uninstaller and runtime licenses, and excludes local settings, sessions, logs and diagnostics. Installation scratch files stay on the selected drive and are removed after setup.

The native uninstaller removes only manifest-listed application files and matching shortcuts/registry entries. Its confirmation offers a choice to keep preferences and the encrypted Claude connection. Unknown files in the installation folder are preserved.

## Sources

- Codex: account rate limits from the local app-server.
- Gemini: Antigravity quota summary, including its independent Claude/GPT pool in the tooltip.
- Claude: local Desktop session and history. Cookie locks, encryption changes or provider web protection can prevent live retrieval. Cached readings retain their original capture time.

Claude data discovery is automatic, including modern Claude-Data profiles. There is no manual profile override. Do not attach authentication files to issues.

## Assets

Node is needed only to regenerate the checked-in icons:

~~~powershell
npm ci --prefix tools --cache .build/npm
node tools/render-icons.cjs
node tools/render-app-icon.cjs
~~~

# HowsMyUsage

A tiny native Windows desktop widget for your AI subscription limits.

![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![MIT](https://img.shields.io/badge/license-MIT-green)

![Desktop widget](docs/widget.png)

Just icons and remaining percentages. Drag anywhere; hover for details. Stays behind your apps, with a system tray icon and no taskbar button. Built with C# / WinForms. No Electron, Tauri, WebView or runtime Node dependency.

## Install

1. Download **HowsMyUsage-1.0.0-win-x64.zip** from [Releases](https://github.com/DarlanSchwartz/HowsMyUsage/releases/latest).
2. Extract the entire ZIP to a writable folder on a drive other than C:.
3. Double-click **Install.cmd** and enter a destination such as **D:\Apps\HowsMyUsage**.
4. The installer copies the application and opens it. No administrator access or separate .NET installation required.

For a Desktop shortcut and automatic startup, open PowerShell in the extracted folder:

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -InstallDir 'D:\Apps\HowsMyUsage' -DesktopShortcut -StartWithWindows
~~~

The shortcut option explicitly permits writing to the Windows Desktop, which may be on C:. App files, settings and app-owned temporary files remain beside the executable. The app deliberately refuses to run from C:.

The installer is unsigned; Windows may show its standard download warning. Download only from this repository's releases. SHA-256 checksum files accompany releases.

**Portable:** run **app\Usage.exe** directly from the extracted folder. Keep the complete app folder together.

## Providers

| Provider | Required source | Display |
| --- | --- | --- |
| Codex | Installed, signed-in Codex CLI/app-server | Lowest remaining account quota |
| Gemini | Running, signed-in Antigravity | Lowest remaining Gemini model quota |
| Claude | Signed-in Claude Desktop | Weekly all-model quota remaining |

Percentages mean **remaining**, not consumed. Checked every two minutes. OpenCode, Go, Zen and OpenRouter are not currently supported.

![Tooltip with details and last check time](docs/tooltip.png)

Screenshots are rendered from the application; values are snapshots, not live repository data.

## Controls

- Drag anywhere to move.
- Hover an icon or percentage for limits, reset times and last check time.
- Left-click the tray icon to show or hide.
- Right-click for Refresh, Start with Windows, and Exit.

Startup uses the current user's **UsageWidget** Run registry entry and a hidden PowerShell launcher. It waits 15 seconds for the desktop, retries failed starts and writes **startup.log** beside the app. Keep the installation drive available at sign-in. No service or scheduled task is installed.

## Setup and limitations

**Codex:** sign in to Codex. The executable must be on PATH or in the supported OpenAI/Codex local installation folder. Existing CODEX_HOME is respected.

**Gemini:** open Antigravity and sign in. The widget reads the local language server's status.

**Claude:** the widget attempts to read Desktop's existing session in read-only mode and query usage. Desktop may lock its cookie database; unsupported cookie encryption can also prevent connection. If convenient, fully exit Claude Desktop, refresh the widget, then reopen Claude. Otherwise the widget may use a cached weekly reading from local history. The tooltip identifies cached readings and their original time. **Last checked is the query time, not proof that cached data is fresh.** Live Claude retrieval has not been validated on every Desktop version.

A dash means no usable quota. Gray values can indicate a cached reading or source error. Provider endpoints and local storage formats may change. This unofficial tool is not affiliated with OpenAI, Google or Anthropic.

## Privacy

No application analytics or telemetry is implemented. Authentication is read locally; you do not paste credentials into the widget. Claude session values are held in memory and sent only to claude.ai. Secrets are not intentionally written to settings or diagnostics. Queries retrieve status rather than generate model responses. Provider applications retain their own storage and logging behavior.

Never attach cookies, authentication files or unredacted account responses to issues.

## Update / uninstall

**Update:** exit from the tray and run the new release's installer with the same destination. Existing widget-settings.json is preserved.

**Uninstall:** disable Start with Windows, exit, delete the installation folder and remove the Desktop shortcut if created.

## Build

Requires Windows x64 and the .NET 10 SDK. Clone to a drive other than C:.

~~~powershell
git clone https://github.com/DarlanSchwartz/HowsMyUsage.git D:\Projects\HowsMyUsage
cd D:\Projects\HowsMyUsage
.\build.ps1
.\dist\widget\Usage.exe
~~~

Build caches and temporary files are redirected to .build inside the repo. Self-contained output is in dist/widget.

~~~powershell
# Parser checks plus real source queries; no model generation:
Start-Process .\dist\widget\Usage.exe -ArgumentList '--check' -Wait
# Exit the running widget first; render previews and verify desktop hosting:
Start-Process .\dist\widget\Usage.exe -ArgumentList '--check-widget' -Wait
# Package ZIP and SHA-256 checksum:
.\package.ps1 -Version 1.0.0
~~~

Checks write to artifacts. Source unavailability is separate from parser checks. Packaging uses the publish manifest, excludes debug symbols and does not package local settings, credentials, caches or diagnostics.

Assets are checked in. Node is needed only to regenerate them:

~~~powershell
npm ci --prefix tools --cache .build/npm
node tools/render-icons.cjs
node tools/render-app-icon.cjs
~~~

## License and credits

Original code: [MIT](LICENSE), copyright 2026 Darlan Schwartz.
Provider logos: [SVGL](https://svgl.app/). Trademarks belong to their respective owners.
See [third-party notices](THIRD-PARTY-NOTICES.md) and [licenses](docs/licenses/) for bundled runtime and build-tool licenses.

[Report a bug](https://github.com/DarlanSchwartz/HowsMyUsage/issues) with Windows version, provider and a redacted screenshot.

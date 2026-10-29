# Development

Requires Windows x64 and the .NET 10 SDK. Packaging also requires Visual Studio C++ x64 build tools and the Windows SDK.

~~~powershell
.\scripts\build.ps1
.\dist\widget\Usage.exe

# Exit the widget before checking its UI.
Start-Process .\dist\widget\Usage.exe -ArgumentList '--check' -Wait
Start-Process .\dist\widget\Usage.exe -ArgumentList '--check-startup' -Wait
Start-Process .\dist\widget\Usage.exe -ArgumentList '--check-widget' -Wait

# Build the installer and checksum. No portable or script-installer release.
.\scripts\package.ps1
~~~

## Layout

| Folder | Contents |
| --- | --- |
| src/HowsMyUsage | WinForms app, UI, settings and quota providers |
| installer | Native installer, uninstaller and shared shortcut integration |
| assets | Provider and application icons |
| scripts | Build and packaging |
| tools | Asset generation tools |
| docs | Screenshots and licenses |
| .build, artifacts, dist | Ignored caches, validation output and releases |

Packaging uses the publish manifest, bundles the native uninstaller and runtime licenses, and excludes local settings, sessions, logs and diagnostics. Installation scratch files stay on the selected drive and are removed after setup.

The native uninstaller removes only manifest-listed application files and matching shortcuts/registry entries. Its confirmation offers a choice to keep preferences and the encrypted Claude connection. Unknown files in the installation folder are preserved.

Startup uses a Task Scheduler logon trigger for the current interactive user, with no saved password or elevation. It launches Usage.exe --startup, allows battery operation, and retries failures. Enabling startup removes the legacy Run entry. The app waits up to 60 seconds for the desktop shell and records startup success or failure in startup.log. --check-startup exercises registration and restores the previous task and registry values.

The widget is an independent window, never owned or parented to Explorer. A one-second visibility check restores hidden/minimized windows, constrains their position to connected monitors, and reapplies native topmost state without activation when enabled. Shell restart, display/work-area and power notifications trigger recovery too. Tray clicks restore visibility; only Exit closes the app. Windows lock/secure desktops and exclusive fullscreen remain controlled by Windows.

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

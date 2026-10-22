# MalodyV Rework Overlay

This repository contains the standalone MalodyV BepInEx plugin, its companion
window, and the original `nonebot_plugin_rework` directory vendored from
`MCPsDE/nonebot-plugin-for-osumania4k`. The card is served directly from
`nonebot_plugin_rework/tosu_plugin`; the legacy `Card` directory is not used.

Current release: `1.3.17`.

## Build

From PowerShell 7 (`pwsh`) with .NET 8 and .NET 6 SDKs installed:

```powershell
pwsh -NoProfile -File .\Build-Release.ps1
```

The output is `release\MalodyVReworkOverlay-1.3.17`. Its plugin DLL is at the package
root so BepInEx can discover it; the companion host and complete
`nonebot_plugin_rework` directory are under `MalodyVReworkOverlay\`. Copy the
package contents into `MalodyV\BepInEx\plugins` after BepInEx has generated the
IL2CPP interop assemblies once. The runtime installer is maintained in the
parent Bot repository at `malodyv_tools/installer`.

The companion is published self-contained and includes a portable Python 3.10
runtime, NumPy, and the reference toolkit. Users do not need to install Python
or configure algorithm paths. Reference metrics run in the companion process
only after chart or analysis-mod changes; the game plugin has no frame or timer
polling loop.

When built inside the Bot repository, `Build-Release.ps1` first mirrors its
current Bot plugin directory into this repository. A standalone clone keeps
using its committed copy. The management window defaults to Chinese and offers
English switching, card visibility and hotkey controls, drag/resize, opacity,
and hide-during-play behavior.

The plugin reads `PlayInfo.Local` mod flags, Turbo audio speed, play mode, and
random seed from both the song-selection screen and online-room configuration,
filtering unavailable mods through MalodyV's own mode rules.
Refresh callbacks are coalesced with one-shot timers and marshalled through
Unity's captured synchronization context. The game state is never polled each
frame or on an interval. Turbo music speed comes from the game's `audioSpeed`
field (`szr` in the current IL2CPP runtime), never Catch scroll speed (`oef`).
Gameplay visibility is restored when `ScenePlay` is destroyed, including
canceled plays. The editor hook targets
the loaded `CommandManager.JustDoIt(CommandBase, bool)` method and refreshes only
for note commands.
The selected rate and chart-changing Key mods are applied before the card
analysis: lane mappings, Less filtering, Thumb reduction, Taps conversion, and
Holds conversion. Changes to the active mod signature invalidate the card's
analysis cache and update the selected chart without requiring a map switch.

At startup, the host checks the Bot directory against
`bepinex-bot-manifest.json`. It tries GitHub Raw and a jsDelivr mirror using
normal HTTPS certificate validation. If those checks are unavailable, the
host verifies the bundled Bot directory against its packaged manifest and
continues offline when they match. On mismatch it downloads only the fixed
GitHub archive, rejects unsafe ZIP paths, links, oversized archives, and
untrusted redirects, verifies the extracted directory SHA-256, stages it, then
replaces only `nonebot_plugin_rework`. A failed download or verification
leaves the bundled directory in place.

Starting with 1.3.17, release updates are automatic and have no confirmation
dialog or disable setting. At startup the host checks the fixed GitHub manifest,
with a jsDelivr fallback, and compares the version plus the BepInEx DLL, host EXE,
and managed host DLL hashes. Older remote versions cannot downgrade the install.
If an update or same-version corruption is detected, it waits for the installed
game process to exit using an OS process notification. It then downloads the
versioned GitHub Release ZIP, validates its SHA-256 and contents, and starts the
verified staged host in installer mode. No periodic game-state or update polling
is added, and large downloads and extraction wait until the game has exited.
The staged installer uses a distinct process name so the game's stale-companion
cleanup cannot mistake the windowless updater for a hung control window.

The installer closes the old companion, replaces only this plugin's host directory,
plugin DLL and release manifest, and restarts the companion. Backups and staging
live outside `plugins` so BepInEx cannot discover duplicate plugin DLLs. Failed
replacement rolls back the previous files; the recovery launch does not retry
immediately, preventing restart loops. Offline use keeps the current files. User
settings in LocalAppData and other BepInEx plugins are preserved. Installation
failures are recorded in `%LOCALAPPDATA%\MalodyVReworkOverlay\updates\last-update.json`.

The controls use shared Segoe UI / Microsoft YaHei UI typography, content-based
minimum widths, themed checkboxes, and a matching opacity slider. The shadow
is rendered on a separate background layer instead of rasterizing all text.

To build a distribution ZIP and finalize its hash in the published manifest:

```powershell
pwsh -NoProfile -File .\scripts\Pack-Release.ps1 -Version 1.3.17
```

Upload the ZIP and SHA256SUMS to GitHub Release `v1.3.17` before publishing the
final `manifest.json`, so a new manifest never points to a missing package.
The ZIP contains a manifest without its own package hash; the external release
manifest records that hash after packing, avoiding a self-referential checksum.

Regression and WPF layout checks:

```powershell
dotnet run --project .\tests\OverlayChecks.csproj --configuration Release -- $PWD
```

The checks cover update rollback, corruption detection, archive path validation,
Chinese/English controls, enlarged text, and synthetic 100/125/150/200% DPI
rendering. Synthetic captures do not substitute for physical monitor testing.

The hashes detect corruption and ensure the downloaded directory matches the
published manifest. Because the manifest and archive are served by the same
GitHub account, this is not a signed supply-chain guarantee: a compromised
repository or account could publish matching malicious content. No downloaded
Python code is executed by the host; the card's JavaScript is loaded in the
WebView as part of normal card rendering.

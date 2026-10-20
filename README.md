# MalodyV Rework Overlay

This repository contains the standalone MalodyV BepInEx plugin, its companion
window, and the original `nonebot_plugin_rework` directory vendored from
`MCPsDE/nonebot-plugin-for-osumania4k`. The card is served directly from
`nonebot_plugin_rework/tosu_plugin`; the legacy `Card` directory is not used.

Current release: `1.3.11`.

## Build

From PowerShell 7 (`pwsh`) with .NET 8 and .NET 6 SDKs installed:

```powershell
pwsh -NoProfile -File .\Build-Release.ps1
```

The output is `release\MalodyVReworkOverlay`. Its plugin DLL is at the package
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
`bepinex-bot-manifest.json`. On mismatch it downloads only the fixed GitHub
archive, rejects unsafe ZIP paths, links, oversized archives, and untrusted
redirects, verifies the extracted directory SHA-256, stages it, then replaces
only `nonebot_plugin_rework`. A failed download or verification leaves the
bundled directory in place. The game plugin independently computes its own DLL
SHA-256 and compares it with the published `manifest.json`; a mismatch is
reported in a dismissible prompt and never blocks play.

The hashes detect corruption and ensure the downloaded directory matches the
published manifest. Because the manifest and archive are served by the same
GitHub account, this is not a signed supply-chain guarantee: a compromised
repository or account could publish matching malicious content. No downloaded
Python code is executed by the host; the card's JavaScript is loaded in the
WebView as part of normal card rendering.

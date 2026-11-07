# Yandex Music · Stream Deck + (Windows)

A Yandex Music player plugin for Stream Deck: live window preview, current track cover
and artist, transport buttons, and audio-session volume control.
No Voicemeeter, no audio recording, no administrator rights, no network server.

> Since 2026-11-07 the code lives in its own repository. The Windows mixer was split out
> into [`streamdeck-app-mixer`](https://github.com/). The original monolithic repo
> `D:\git\streamdeck` now hosts MiniFuse Desk research only.

## Layout

- `src/` — Node.js logic: `plugin.mjs` (entry), `player-control.mjs` (transport & info keys),
  `window-capture.mjs` (window preview and dial roles), `audio.mjs` (Yandex Music audio
  session via the bridge), `bridge.mjs` (JSON-protocol client), `render.mjs` (SVG helpers).
- `native/AudioBridge/` — self-contained .NET bridge to the Windows Audio Sessions API
  (volume/mute of the `Y.Music.exe` / `YandexMusic.exe` session).
- `native/WindowCapture/` — self-contained .NET window capture (PrintWindow) plus media
  session metadata and UI Automation for the legacy Yandex Music UWP.
- `plugin/` — manifest, generated icons, encoder layout, property-inspector UI, licenses.
- `scripts/` — build, profile generation, checks.
- `tests/` — unit tests (`node --test`).

## Build & install

1. Backup: `pwsh -File scripts/backup.ps1`. Copies `%APPDATA%\Elgato\StreamDeck` data and
   the HKCU StreamDeck registry branch; profile files are verified with SHA-256. The backup
   may contain private settings — do not publish it.
2. `npm ci`, `npm test`, `npm run build` (.NET SDK 10, Node.js 20+).
3. `npx streamdeck validate dist/local.tux.yamusic.sdPlugin`.
4. `npx streamdeck pack dist/local.tux.yamusic.sdPlugin -o dist`, open
   `dist/local.tux.yamusic.streamDeckPlugin` and confirm the plugin install.
5. Open `dist/YandexMusic.streamDeckProfile` and import the **new** profile
   **Yandex Music**: six control keys and four preview sections. The package has its own
   UUIDs; existing profiles are never modified.

## Actions

| Action | Purpose |
|---|---|
| App window (encoder) | Live window snapshots (~2 fps), "artist — track" marquee, track timer. For a full dashboard — place on all four encoders |
| Music: track cover | Cover art filling the key (in-memory cache) |
| Music: artist | Artist name in large text |
| Music: previous / play-pause / next | Transport via UI Automation of a single Yandex Music window |
| Music: repeat / shuffle / like | Toggles; gold frame marks the active mode |

Dial roles (defaults, configurable in "App window" settings):
**first** encoder — seek ±5 s per step, press — play/pause; **fourth** — Yandex Music
session volume ±2%, press — mute. The others do nothing.

## Windows specifics

- Commands invoke only known UI elements of **one** Yandex Music window via UI Automation:
  no global media keys, no focus changes. If the window/controls are unavailable or
  several player windows are found, the command is rejected with a warning.
- Volume/mute affect only the single `Y.Music.exe` / `YandexMusic.exe` session found;
  there is no fallback to master volume or a browser.
- Snapshots are processed in memory, never written to disk or sent over the network.
  The window must not be minimized; PrintWindow is not supported by all apps (possible
  black frames). A manually selected window is validated by HWND/PID; re-select it after
  restarting the app.

## Checks

```powershell
npm test
pwsh -File scripts/check-music-profile.ps1
./dist/local.tux.yamusic.sdPlugin/bin/native/AudioBridge.exe --self-test
```

The self-test creates its own **silent** audio session and exercises volume/mute/unmute
only inside it; user apps and master volume are untouched.

On-device acceptance (Yandex Music running): add four identical "App window" actions —
expect a seamless bottom strip of the window, the marquee and the track time; pause/track
change stops/updates the timer; top keys change only Yandex Music; rotating the first
encoder seeks ±5 s without leaving the track bounds, the fourth changes session volume by ±2%.

## Technical debt

1. **Mandatory migration to `@elgato/streamdeck` v2+.** The Stream Deck WebSocket
   protocol is currently implemented manually with the `ws` package (`src/plugin.mjs`).
   This is a hard Elgato requirement for DRM and Marketplace publishing: Node.js plugins
   must use the official SDK. Bump `SDKVersion` to **3** and `Software.MinimumVersion`
   to **6.9+** as well (currently 2 / 6.6).
2. **UUID with the `local.` prefix** (`local.tux.yamusic`). Move to a vendor namespace
   (e.g. `com.tux.yamusic`) before store submission. Changing the UUID is a new plugin
   identity. Decide before the first Maker Console submission.
3. **Code duplication** with the `streamdeck-app-mixer` repo (`bridge.mjs`, render
   helpers, bridge protocol). Consider a shared library once both plugins stabilize.
4. **DRM file requirements**: distributed files must be immutable at runtime and the
   manifest must not be read at runtime. Verify before submission.
5. **UI Automation against Yandex Music** is fragile across app updates: UI elements may
   be renamed and commands will start failing. There is no automatic re-discovery; manual
   acceptance is required after every player update.
6. **PrintWindow** can produce black frames for some windows; the legacy UWP timer is
   read from known UI elements and breaks on redesigns.
7. **Volume/mute are bound to the full EXE path.** An app update that changes the path
   means the old path finds no session.
8. **Placeholder icon** ("♪", generated by `scripts/build-icons.ps1`). Proper artwork and
   gallery screenshots are needed for the store listing.
9. **No CI.** Tests are run manually; add GitHub Actions (at least `npm test`).
10. **Localization**: UI and action texts are Russian-only — the store needs an English
    version.

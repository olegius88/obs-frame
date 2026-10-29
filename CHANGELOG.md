# Changelog

## [Unreleased]

## 1.1.0 - 2026-10-29
### Changed
- The UI is in English by default and in Russian when Windows UI is Russian; `OBSFRAME_LANG=en|ru` overrides it.
- Documentation is in English by default; the Russian version is in [README.ru.md](README.ru.md).

## 1.0.1 - 2026-10-29
### Fixed
- The obs-websocket connection no longer depends on the system proxy (`ClientWebSocket.Options.Proxy = null`); reconnect attempts every second.

### Added
- Connection errors are written to `ObsFrame.log` next to the program (up to 256 KB).

## 1.0.0 - 2026-10-29
### Added
- "OBS recordings" window ("☰ Videos" button): videos from the OBS recording folder with date, duration and size; play, show in folder, rename (F2), move to Recycle Bin (Del), auto-refresh.
- `--with-obs` switch: also starts OBS if it is not running.
- "✕" button on the bar to close the frame.

## 0.1.0 - 2026-10-29
### Added
- Frame around the OBS recording area, coloured by state: idle, recording, paused ([ObsFrame.cs](ObsFrame.cs)).
- Control bar: timer, file size, Record/Stop, Pause/Resume, recordings folder.
- Moving and resizing the area with the mouse, applied in OBS via obs-websocket.
- Overlay windows are excluded from screen capture.

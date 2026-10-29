# ObsFrame

[Русская версия](README.ru.md)

A Bandicam-style on-screen frame and recording control bar for [OBS Studio](https://obsproject.com/) on Windows. You always see which part of the screen is being recorded, and you can start, pause and stop recording from the bar above the frame.

## Features
- **Frame** around the recorded area (the crop of the "Display Capture" source in the current scene). Its colour shows the state: blue — idle, red — recording, yellow — paused. Clicks pass through it.
- **Control bar** above the frame: indicator, timer and file size, buttons Record/Stop, Pause/Resume, "☰ Videos", "📁", the current area size and "✕".
- **Move and resize the area with the mouse** while not recording: drag the bar by its label to move the area, drag the coloured grip at the bottom-right corner to resize it. The crop and the canvas size are applied in OBS immediately.
- **Recordings list** ("☰ Videos") from the OBS recording folder: date, duration, size. Double-click or Enter to play, F2 to rename, Del to move to the Recycle Bin. The list refreshes itself.
- **Nothing from the overlay gets into the recording**: all its windows are marked `WDA_EXCLUDEFROMCAPTURE`, so they are also invisible on screenshots and in remote-desktop tools.
- **Language**: Russian when Windows UI is Russian, English otherwise. Override with the environment variable `OBSFRAME_LANG=en` or `ru`.

## Requirements
- Windows 10 2004 or newer, or Windows 11.
- OBS Studio 28+ with the built-in obs-websocket. Enable it: **Tools → WebSocket Server Settings → Enable WebSocket server**. ObsFrame reads the port and password from `%APPDATA%\obs-studio\plugin_config\obs-websocket\config.json`.
- A "Display Capture" (`monitor_capture`) source in the current OBS scene.

## Installation
1. Download `ObsFrame.exe` from [Releases](../../releases) or build it yourself (see below).
2. Put it in any folder and run it as:
   - `ObsFrame.exe --with-obs` — starts OBS if it is not running yet, plus the frame (handy for a desktop shortcut);
   - `ObsFrame.exe` — the frame only; it waits for OBS and is shown while OBS is open (handy for autostart).

A second launch never creates a second copy. To close the frame, click "✕" on the bar or right-click the bar → "Exit frame".

## Building
Only the .NET Framework compiler that ships with Windows is needed:
```
build.cmd
```

## Files
| File | Purpose |
|---|---|
| `ObsFrame.cs` | all the code: WinForms overlay and a minimal obs-websocket v5 client (C# 5) |
| `build.cmd` | builds `ObsFrame.exe` with `csc.exe` from `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319` |
| `CHANGELOG.md` | change history |

## Notes
- Coordinates are physical pixels (per-monitor DPI v2), the same as OBS uses.
- obs-websocket limits the canvas to 4096 px; a larger area is scaled down proportionally.
- The area width is rounded down to a multiple of 4 and the height to an even number.
- Connection errors are written to `ObsFrame.log` next to the program.

## License
[MIT](LICENSE)

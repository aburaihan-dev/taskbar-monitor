# Taskbar-only network monitor: design (as built)

Date: 2026-10-25. Rewrite of the taskbar window of `zhongyang219/TrafficMonitor` (C++/MFC, Anti-996 licence) as a small .NET app. **No source was copied** from that project; only Windows facts (window class names, geometry) were reused. If code is ever copied, add its LICENSE notice.

## Intent
One tiny executable that shows live Upload/Download speed, CPU % and memory % inside the Windows 11 taskbar, left of the system tray, and nothing else.

Non-goals (v1): temperatures/hardware monitor, GPU/disk/frequency, today-traffic history, plugins, skins, floating window, update checker, translations, Windows 10, secondary monitors, left/right/top taskbars.

## Builder's calls (the language was delegated to me)
| Call | Choice | Why |
|---|---|---|
| Language | C# on .NET 10 | SDK installed; Go also installed but clumsier for Win32/GDI; Rust not installed |
| UI | Raw Win32 via CsWin32 (build-time source generator), GDI+ text, `UpdateLayeredWindow` | No WinForms/WPF cost. `UseWindowsForms` is set only to get `System.Drawing` from the installed shared runtime |
| Items | Up, Down, CPU, Memory | Core of the original |
| Settings | `%APPDATA%\TaskbarMonitor\config.json` + right-click menu | No dialogs |
| Defaults | 12 px Segoe UI; labels `↑ ↓ CPU MEM`; text white on dark theme, `#1B1B1B` on light; B/s with 1024 steps, bits with 1000 steps | Builder's call, all editable in config/menu |
| Packaging | Framework-dependent single file, ~210 KB | .NET 10 desktop runtime is installed |

## How it works
- **Embedding**: a layered `WS_CHILD` window of `Shell_TrayWnd`, drawn with per-pixel alpha. Position: `x = TrayNotifyWnd.left - width + 2 + offset`, `y` centred on the taskbar; if the tray rect is unavailable, `taskbarWidth - 88px` (same fallback idea as the original).
- **Z-order (key finding of the spike)**: Windows 11 paints the taskbar in a full-width child `Windows.UI.Composition.DesktopWindowContentBridge`, which is the topmost child. A widget left at the bottom of the z-order is invisible. The widget is placed at `HWND_TOP` and re-raised on any tick where it is not the first child.
- **Hit-testing**: background alpha is 1 (not 0), otherwise clicks on gaps fall through and only the glyphs get the right-click.
- **Explorer restart**: a *thread* timer (no window) ticks every second. Each tick checks `IsWindow(widget)` and `GetParent(widget) == Shell_TrayWnd`, and re-creates/re-parents when needed. A window timer would die with the widget.
- **Network**: sums adapters that are Up, not loopback/tunnel, not obviously virtual, **and have a default gateway**. Windows also lists filter layers (`Ethernet-WFP…`, `…-QoS Packet Scheduler`) and WAN miniports with mirrored counters; summing everything counted the traffic about 4 times. Adapters are picked every 15 s (listing costs 35-70 ms on a machine with many filter layers) and counters are read every second. `Calc.NetDelta` ignores new adapters and treats a counter reset as 0.
- **CPU** `GetSystemTimes` delta; **memory** `GlobalMemoryStatusEx.dwMemoryLoad`.
- **Menu**: right-click opens a popup owned by a hidden top-level window so it closes when you click elsewhere.

## Verified on this machine
Windows 11 build 10.0.26300, 1920x1080 at 100%, bottom taskbar, dark theme.
- `dotnet run -p:Selftest=true -- --selftest` prints PASS (formatting, delta maths, adapter cache).
- Widget visible, transparent, left of the tray; follows the tray when its width changes.
- Speed matches an independent counter of the same adapter (3.33 MB/s vs ~3.4 MB/s during a rate-limited transfer; ~7.7 KB/s vs 7.5 KB/s idle).
- `taskkill /f /im explorer.exe`, shell restarted: the same widget process re-attached to the new taskbar on top.
- Menu: opens at the widget and closes on Escape; "Show speed in bits" and "Start with Windows" were selected and worked (Run key added then removed, `config.json` written), both reverted afterwards.
- Second launch exits immediately (mutex). Published exe is a GUI-subsystem build.
- Idle cost after tuning (cached font/layout, redraw skipped when nothing changed, working set trimmed every 20 s, no background GC, no tiered PGO): about 0.3% of one core, ~11 MB working set, ~16 MB private. Before tuning: 0.5% and ~44 MB.

## Refresh rate and layout
Menu "Refresh rate": 0.5 / 1 / 2 / 5 s (`RefreshMs` in config, default 1 s). Measured idle: 0.7% of one core at 0.5 s, 0.31% at 1 s. Temperatures are laid out like CPU/MEM, two rows per column (TEMP, GPU, DISK fill columns in that order).

## Temperatures
- **TEMP** (always, no admin): hottest ACPI thermal zone via the PDH counter `Thermal Zone Information(*)\Temperature`. Read 60-72 °C here, tracking the counter. It is the system/package zone, not per-core. Hidden if the machine has no zone.
- **GPU / DISK** (opt-in, admin): menu "GPU/disk temperatures". Turning it on relaunches the app elevated (UAC), then `LibreHardwareMonitorLib` reads GPU and storage sensors every 3 s. CPU/mainboard are deliberately not enabled (they need a kernel driver). The library loads only in that mode; normal mode costs the same as before (~0.3% CPU, ~19 MB). The exe is now ~3.4 MB plus two helper DLLs.
- The elevated path is **not verified** (needs a UAC approval). This machine has only an Intel UHD GPU, which may expose no temperature. Autostart starts the normal, non-admin copy.

## Not verified
The Upload/Download/CPU/Memory toggles, Font size and Position menu items were not clicked (same code path as the ones that were). Scaling at 125%/150% (code uses `GetDpiForWindow`, not exercised), light theme, taskbar auto-hide, multiple monitors, Windows 10, VPN adapters that have a gateway (may double count; `ponytail:` note in `Stats.cs`), overlap with the Widgets button (use Position > Move).

## Build and run
```
dotnet build -c Release
dotnet publish -c Release -o publish          # publish\TaskbarMonitor.exe
dotnet run -c Release -p:Selftest=true -- --selftest
```
The self-check build writes to `bin\selftest` / `obj\selftest` so its console exe cannot leak into a publish. If a publish ever shows a console window, check the exe's PE subsystem (2 = GUI) and rebuild with `dotnet build --no-incremental`, then `dotnet publish --no-build`.

## Pitfalls found while building
- A versioned target (`net10.0-windows10.0.19041.0`) adds a 24 MB WinRT projection; use plain `net10.0-windows`.
- `Marshal.GetHINSTANCE` returns -1 in single-file apps; use `GetModuleHandle(null)`.
- `FILETIME` in `GetSystemTimes` is `System.Runtime.InteropServices.ComTypes.FILETIME`; `TrackPopupMenuEx` flags are plain `uint`.
- Screen captures of layered windows need `BitBlt` with `CAPTUREBLT`; `Graphics.CopyFromScreen` misses them.

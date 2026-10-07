# PCStatus — Plan

A tiny Windows 11 tray app that shows live CPU / GPU / RAM usage as mini bars in the tray icon, with a click-open detail panel.

## Decisions (from Q&A)

| Topic | Choice |
|---|---|
| Tray look | One tray icon showing mini vertical bars: CPU, one bar per GPU, RAM |
| GPU source | Each GPU shown separately (Intel Arc 140V + RTX 5090). On a machine with only one GPU, only that one is shown |
| Extras | VRAM usage, CPU + GPU temperatures, 60 s history sparklines |
| Details UI | Left-click opens a popup panel above the tray; hover shows a text tooltip |
| Refresh | 1 second |
| Elevation | Runs as admin, so CPU temp can be read |
| Packaging | Single self-contained `.exe`, with an "Start with Windows" toggle |

## Tech stack

- **C# / .NET 10 (LTS), WinForms**: `NotifyIcon` for the tray, plus a borderless form for the popup. The app is small, native, and starts fast.
- **LibreHardwareMonitorLib** (NuGet) for CPU temp, CPU load, GPU temps, and NVIDIA VRAM. It needs admin rights and loads its driver (PawnIO) at runtime.
- **Windows PDH counters** (`GPU Engine\Utilization Percentage`) for GPU load on *both* GPUs. These counters match Task Manager, which LHM's per-GPU load doesn't always do for the Intel iGPU.
- **`GlobalMemoryStatusEx`** (P/Invoke) for RAM.
- Prerequisite: **only the .NET runtime is installed, not the SDK.** Install it with `winget install Microsoft.DotNet.SDK.10`.

## Architecture

```
PCStatus/
  PCStatus.csproj          net10.0-windows, WinForms, app.manifest (requireAdministrator)
  Program.cs               single-instance mutex, start TrayApp
  TrayApp.cs               ApplicationContext: NotifyIcon, context menu, 1 s timer
  Sensors/
    SensorSnapshot.cs      record: CpuPct, CpuTempC, RamPct, RamUsedGB, RamTotalGB, Gpus: List<GpuReading>
    GpuReading.cs          record: Name, ShortName ("Arc"/"RTX"), Luid, LoadPct, TempC?, MemUsedGB, MemTotalGB?, IsDedicated
    SensorService.cs       owns LHM Computer + PDH counters, returns a SensorSnapshot each tick
    GpuEnumerator.cs       finds the physical GPUs at startup (DXGI adapters, skipping "Microsoft Basic Render Driver"),
                           maps each adapter LUID to a name, and matches it to its LHM hardware entry
    GpuCounters.cs         PDH: per adapter LUID, take the busiest engine ("3D", "Compute", "Copy", "VideoDecode"...),
                           which is the same value Task Manager shows
  UI/
    TrayIconRenderer.cs    draws N bars (CPU, GPU1..GPUn, RAM) into the icon bitmap (DPI-aware, handles light/dark taskbar)
    DetailPanel.cs         dark rounded popup: rows with value + sparkline; positioned above the tray
    Sparkline.cs           draws the 60-sample ring buffer
  History.cs               ring buffers (60 samples) per metric
  Autostart.cs             create/delete a Task Scheduler task ("run with highest privileges", at logon)
```

## Behaviour details

- **Tray icon:** one bar per metric, left to right CPU, Arc, RTX, RAM (4 bars here; 3 on a single-GPU machine). The GPU bars are ordered integrated first, then dedicated. Each bar is green below 60%, yellow from 60 to 85%, and red above 85%. At 100% display scaling the icon is 16 px wide, so 4 bars come out about 3 px wide with 1 px gaps. The icon handle is regenerated each tick and the old one is destroyed (`DestroyIcon`) so GDI handles don't leak.
- **Tooltip:** `CPU 23% 54° | Arc 12% | RTX 71% 66° | RAM 48%`. Tooltips are limited to 127 characters.
- **Popup panel** (left-click toggles it; it closes when focus is lost):
  - CPU: %, temp, 60 s sparkline
  - One row per GPU, each showing its name, %, temp (if available), memory, and sparkline:
    - Intel Arc: shared GPU memory used
    - RTX 5090: dedicated VRAM used / total GB
  - RAM: %, used / total GB, sparkline
- **GPU detection:** GPUs are listed once at startup. If an adapter disappears (e.g. an eGPU is unplugged) or its counters stop responding, its row and bar are dropped instead of the app crashing.
- **Right-click menu:** Start with Windows ✓ · Exit.
- **Autostart:** an admin app can't use the `Run` registry key without a UAC prompt at every logon, so the app uses a **Task Scheduler** logon task with highest privileges instead.
- **Taskbar theme:** the bar background follows the light/dark mode in `HKCU\...\Themes\Personalize\SystemUsesLightTheme`.
- **Overhead target:** under 1% CPU and under 60 MB RAM. Sensors are polled on the timer only, with no busy loops.

## Milestones

1. **Scaffold:** install the SDK, create the WinForms project and manifest, and get a tray icon with an Exit menu working.
2. **Sensors:** CPU, RAM, and per-GPU load via PDH, with GPU enumeration. Check the values against Task Manager's per-GPU tabs.
3. **Tray icon rendering:** live mini bars plus the tooltip.
4. **LHM integration:** CPU and GPU temps and VRAM. Handle missing sensors gracefully by showing "—".
5. **Detail panel:** popup, positioning, and sparklines.
6. **Autostart toggle** through Task Scheduler.
7. **Publish:** `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` produces `PCStatus.exe`.

## Status (2026-10-07)

All milestones are implemented, and the build is `publish\PCStatus.exe` (51 MB, self-contained).

- Measured: ~46 MB private memory, about 3.5% of one core.
- LHM's CPU and GPU readers run as two separate `Computer` instances, because one combined instance used about 190 MB.
- CPU temp works now that the PawnIO driver is installed (`winget install namazso.PawnIO`).
- When the RTX goes idle, its last temperature stays on screen for 30 s before switching to "idle".
- `PCStatus.exe --selftest <file.txt>` writes sensor samples to the file and saves icon and panel PNGs next to it.

## Installer

To build, run `installer\build.ps1`. It needs the .NET 10 SDK and Inno Setup 6 (`winget install JRSoftware.InnoSetup`), and produces `dist\PCStatusSetup-<version>.exe`.

- **What's included:** self-contained x64 and ARM64 exes. Setup installs whichever matches the PC, so .NET doesn't need to be installed there.
- **Supported systems:** 64-bit Windows 10 1809+ and Windows 11.
- **Optional tasks:** autostart (on by default), and the PawnIO driver (on by default, offered only when PawnIO isn't already installed).
  - PawnIO is downloaded at install time and checked against its SHA-256, not bundled, because its redistribution terms are unclear.
  - If the download fails, setup continues without it.
- **Autostart:** a scheduled task that runs at any user's logon, as that user (BUILTIN\Users, HighestAvailable). Admins get CPU temp without a UAC prompt. Standard users run unelevated without CPU temp.
- **Uninstall:** stops the app and removes the task. PawnIO stays installed, because it's a shared system driver.
- **Hardware fallbacks:**
  - Zero, one, or several GPUs are all supported; the bars adapt to the count.
  - If the PDH counters are missing, CPU % falls back to `GetSystemTimes`.
  - Missing LHM sensors show "—".

## Risks / open points

- **CPU temp on Lunar Lake (Core Ultra 200V):** LHM support for this platform is fairly recent. If LHM can't read the package temperature, the panel shows "—".
- **Antivirus:** older LHM builds used the WinRing0 driver, which Defender flags. Current LHM uses PawnIO, so I'll pin a recent version.
- **Hybrid graphics:** when the RTX 5090 is idle and powered down, NVML/LHM may report no temperature. The panel will show "idle" in that case and won't wake the GPU.

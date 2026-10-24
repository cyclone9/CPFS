[English](README.md) · [فارسی](README.fa.md) · [العربية](README.ar.md) · [Türkçe](README.tr.md)

# CPFS - CyClone Power File Share

<p align="center"><picture><source media="(prefers-color-scheme: dark)" srcset="assets/brand/Logo/cpfs-lockup-on-dark.svg"><img src="assets/brand/Logo/cpfs-lockup-on-light.svg" alt="CPFS" width="348"></picture></p>

**Tagline:** *Your PC's folder, within reach of any device.*  
**Motto:** *Only the folder you chose.*

| | |
|---|---|
| Version | 0.7.0 (in development) |
| Platform | Windows 10 and 11 (Home or Pro) |
| Requires | .NET Framework 4.8 (built into Windows 10 1903 and later and Windows 11), Administrator rights |
| Homepage | https://cyclone9.ir/cpfs |
| Licence | MIT (code only; see [Licence](#licence)) |

<p align="center"><img src="assets/brand/cpfs-video-en.jpg" alt="CPFS" width="720"></p>

CPFS is being rebuilt as a Windows app. There is no release yet, so there is nothing to download, and everything below describes what the app is meant to do.

## What CPFS does

CPFS shares one folder on a Windows 10 or 11 PC with Windows, macOS, Linux, Android and iOS devices. They connect by the PC's name (ComputerName.local), by its Wi-Fi or LAN address, or by a VPN address, and a phone can connect with a QR code. Sharing is limited to that folder, with a separate account and hardened Windows settings, and the uninstall puts the original settings back.

## What the app will have

- Pages for Setup, Monitor, Connect and Uninstall.
- A Windows 11 look with System, Light and Dark themes.
- English first, then Persian, Arabic and Turkish.
- Setup can give the shared folder a drive letter, a desktop shortcut and an entry in File Explorer's navigation pane. You choose the letter and the names, and all three carry the CPFS icon. On the drive letter, Explorer may show the disk's own name instead of yours. The navigation pane entry is the "Show in File Explorer" switch, on by default.
- A connect guide with QR codes for Windows, macOS, Linux, Android and iOS, over the `.local` name, the Wi-Fi or LAN IP address, or the VPN address.
- Monitor shows the connected devices and their traffic, the large transfers being copied now ("Being copied now"), Recent activity and failed sign-ins. Recent activity lists every file added, changed, renamed or deleted in the shared folder, whether from any device or from this PC. CPFS can't tell which device made a change.
- An optional "Save to a log file" switch for Recent activity, off by default.
- A "Check for updates" button in About, and an optional check when CPFS starts, off by default. It asks GitHub only and downloads nothing.
- Uninstall, which restores the original Windows settings.
- No deleting of your files. CPFS never deletes, moves or overwrites anything in the shared folder on its own.
- Existing installs made with the earlier CPFS scripts are picked up.

## Uninstall

Uninstall puts back the Windows settings Setup changed and removes what CPFS created. The shared folder and the files in it stay. For the items that give the folder quick access:

- The File Explorer entry is removed when it still opens the shared folder. It consists of the keys `HKCU\Software\Classes\CLSID\{07C33AB4-B371-467B-9DC1-1AA530E251C6}` and `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{07C33AB4-B371-467B-9DC1-1AA530E251C6}`, and a `{07C33AB4-B371-467B-9DC1-1AA530E251C6}` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel`, all in the hive of the user who ran Setup.
- The drive icon, `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\<letter>\DefaultIcon`, is removed, or the icon the letter had before is put back. An icon CPFS didn't set stays.
- `C:\ProgramData\CPFS\cpfs.ico` goes with the rest of `C:\ProgramData\CPFS`.

## Where CPFS stores things

- `C:\ProgramData\CPFS`: `config.json` with your answers and the backup of the original settings, and `cpfs.ico`, the CPFS icon used by the drive, the shortcut and the File Explorer entry.
- `%LOCALAPPDATA%\CPFS`, in the profile of the user who runs CPFS:
  - `preferences.json`: your choices in the app, such as theme, language and font.
  - `devices.json`: the names you gave connected devices.
  - `cpfs-log.txt`: the app's log.
  - `activity-log.txt` and `activity-log.old.txt`: Recent activity, only while "Save to a log file" is on. At 5 MB the file becomes `activity-log.old.txt`. It holds file names, so Copy report never includes it. Uninstall doesn't remove it, because it is your own log.

Uninstall removes nothing in `%LOCALAPPDATA%\CPFS`.

## Licence

The code is under the MIT licence (see `LICENSE`). The CPFS name, logo, symbols, wordmark and app icon are not covered by it and may not be used without permission. The files in `assets/brand/` are the CPFS brand files and are not covered by the MIT licence. The Vazirmatn font in `assets/fonts/` is under the SIL Open Font License 1.1 (see `OFL.txt` there).

© CyClone · https://cyclone9.ir/

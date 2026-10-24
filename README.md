[English](README.md) · [فارسی](README.fa.md) · [العربية](README.ar.md) · [Türkçe](README.tr.md)

# CPFS - CyClone Power File Share

<p align="center"><picture><source media="(prefers-color-scheme: dark)" srcset="assets/brand/Logo/cpfs-lockup-on-dark.svg"><img src="assets/brand/Logo/cpfs-lockup-on-light.svg" alt="CPFS" width="348"></picture></p>

**Tagline:** *Your PC's folder, within reach of any device.*  
**Motto:** *Only the folder you chose.*

| | |
|---|---|
| Version | 0.6.0 (in development) |
| Platform | Windows 10 and 11 (Home or Pro) |
| Requires | .NET Framework 4.8 (built into Windows 10 1903 and later and Windows 11), Administrator rights |
| Homepage | https://cyclone9.ir/cpfs |
| Licence | MIT (code only; see [Licence](#licence)) |

<p align="center"><img src="assets/brand/cpfs-video-en.jpg" alt="CPFS" width="720"></p>

CPFS is being rebuilt as a Windows app. There is no release yet, so there is nothing to download, and everything below describes what the app is meant to do.

## What CPFS does

CPFS shares one folder on a Windows 10 or 11 PC with Windows, macOS, Linux, Android and iOS devices. They connect by the PC's name (ComputerName.local), by its Wi-Fi or LAN address, or by a VPN address, and a phone can connect with a QR code. Sharing is limited to that folder, with a separate account and hardened Windows settings, and the uninstall puts the original settings back. If you run a Linux server, CPFS can also sync one subfolder with it over an encrypted channel. A file deleted or replaced on one side is kept in a versions folder on the other.

## What the app will have

- Pages for Setup, Monitor, Sync, Connect and Uninstall.
- A Windows 11 look with System, Light and Dark themes.
- English first, then Persian, Arabic and Turkish.
- Your own choice of drive letter, drive name and shortcut name.
- A connect guide with QR codes for Windows, macOS, Linux, Android and iOS, over the `.local` name, the Wi-Fi or LAN IP address, or the VPN address.
- Optional sync of one subfolder of the share with a Linux server, over an encrypted channel.
- Uninstall, which restores the original Windows settings.
- No deleting of your files. CPFS never deletes, moves or overwrites anything in the shared folder on its own. With sync turned on, a file deleted or replaced on one side is kept in a versions folder on the other, not removed.
- Existing installs made with the earlier CPFS scripts are picked up.

## Licence

The code is under the MIT licence (see `LICENSE`). The CPFS name, logo, symbols, wordmark and app icon are not covered by it and may not be used without permission. The files in `assets/brand/` are the CPFS brand files and are not covered by the MIT licence. The Vazirmatn font in `assets/fonts/` is under the SIL Open Font License 1.1 (see `OFL.txt` there).

© CyClone · https://cyclone9.ir/

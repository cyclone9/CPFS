[English](README.md) · [فارسی](README.fa.md) · [العربية](README.ar.md) · [Türkçe](README.tr.md)

# CPFS - CyClone Power File Share

| | |
|---|---|
| Version | 0.3.0 (in development) |
| Platform | Windows 10 and 11 (Home or Pro) |
| Requires | .NET Framework 4.8 (built into Windows 10 1903 and later and Windows 11), Administrator rights |
| Homepage | https://cyclone9.ir/cpfs.html |
| Licence | MIT (code only; see [Licence](#licence)) |

CPFS is being rebuilt as a Windows app. There is no release yet, so there is nothing to download, and everything below describes what the app is meant to do.

## What CPFS does

CPFS shares one folder on a Windows PC so that phones and other devices can reach it by `ComputerName.local`. Devices sign in with a separate SMB account made for the purpose, not with your Windows account, and the share is set up with hardened Windows settings.

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

The code is under the MIT licence (see `LICENSE`). The CPFS name, logo, symbols, wordmark and app icon are not covered by it and may not be used without permission.

© CyClone · https://cyclone9.ir/

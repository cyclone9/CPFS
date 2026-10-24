# Changelog

This project uses [semantic versioning](https://semver.org/).

## Unreleased

## 0.8.0 - 2026-10-24

First public release. CPFS shares one folder on a Windows PC with your phones and other devices, through its own SMB account.

- One exe for Windows 10 and 11 (Home or Pro). There is no installer. The file is not signed, so Windows SmartScreen asks you to confirm the first run; the README explains the step and how to check the SHA-256.
- Setup is one page. You pick the folder and a password for the SMB account, and CPFS shows a plan of what it will change before it changes anything. It creates a System Restore point first, unless you untick it.
- Setup hardens the Windows settings it touches and never makes one weaker than Windows' default. The firewall rules open port 445 only on Private networks and on the VPN adapter.
- Quick access: a drive letter, a desktop shortcut and a File Explorer entry for the shared folder, all with the CPFS icon. You choose the letter and the names.
- Optional Auto-Private marks new networks as Private. It is off by default. The computer name can also be changed, which takes effect after a restart.
- Connect has a guide for Windows, macOS, Linux, Android and iPhone/iPad, for every address of the PC (`.local`, LAN and VPN). Phones get QR codes. No password appears anywhere in the guide or the codes.
- Monitor shows connected devices and their traffic, files being copied now, recent activity (with an optional log file) and failed sign-ins. Disconnect closes a device's session.
- Uninstall puts back exactly what Setup changed and never deletes your files. SMB 1, the Guest account and anonymous access stay off afterwards.
- English, Persian, Arabic and Turkish, with right-to-left layout and a light, dark or system theme.
- An update check, from a button in About or optionally at start. It only asks GitHub and downloads nothing.
- Installs made with the earlier CPFS scripts (0.2.x) are read and continued.

# Changelog

This project uses [semantic versioning](https://semver.org/).

## Unreleased

- The READMEs show a new 30-second video of 1.0.1, in English and Persian, as an animated preview that opens the video.
- The READMEs link the VirusTotal report for the 1.0.1 exe.

## 1.0.1 - 2026-11-18

- In Persian and Arabic, addresses, dates, versions and paths inside sentences keep their left-to-right order, as the window title already did.

## 1.0.0 - 2026-11-18

This release adds a page for the shared folders your PC has opened on other computers, a tray icon, an in-app updater and new layouts for Setup, Monitor and Connect.

- The CyClone homepage moved to cyclone9.dev. The README, the licence and About link there. The READMEs have a new video and cover image, in English and Persian.
- New page, Shared folders on other PCs. It lists the shared folders this PC is connected to on other computers, grouped by server, with the drive letter, account, state and whether the drive reconnects at sign-in. Devices connected to this PC's own share stay on Monitor.
- Shared folders on other PCs can disconnect one connection or every connection to a server, after you confirm. Windows is forced to close a connection only when it refused because files are open and you confirmed a second time. Disconnecting a drive also removes it from the next sign-in.
- Windows allows one user name per server at a time and refuses a second one with error 1219. The page explains the rule, lists what to try (including Microsoft's IP-address workaround) and opens Credential Manager. The Windows guide on Connect mentions the error.
- The page works only in a window opened without administrator rights. In a window with administrator rights it says why and offers Reopen without administrator rights.
- New look for Setup and Uninstall. Setup opens with a card that says whether sharing is on, with the share address, Copy and Open folder. Both pages keep the summary, Show details and the main button in a bar at the bottom, and Uninstall moved to the bottom of the menu.
- New look for Monitor: four numbers at the top, one list of connected devices, and Recent activity beside Being copied now. Recent activity also works without administrator rights.
- New look for Connect: the share address and username at the top, one tab per kind of device, numbered steps, and the QR code beside the iPhone and Android steps instead of in a dialog.
- Optional tray icon, off by default and switched on in Preferences. It shows whether sharing is on and whether this PC is on a Private or a Public network, and its menu opens CPFS or the shared folder. It never runs with administrator rights.
- Minimize to tray: while the tray icon runs, minimizing a window opened without administrator rights hides it from the taskbar, and the tray icon brings it back.
- Start with Windows: a switch in Preferences starts the tray icon when you sign in, through your own `Run` entry in the registry and never as administrator. Preferences tells you when the entry points to another copy or a copy that is gone, and offers Use this copy. Uninstall removes the entry when it is CPFS's own and closes the tray icon.
- Update now in About. After a check finds a newer release, CPFS can download it from GitHub, check it against the release's SHA-256, size and version, test-start it and replace the exe it runs from. It happens only when you press the button, and never in a window with administrator rights. A copy on a network drive or in a folder CPFS can't write to gets Save as instead.
- The updater keeps the previous exe until the new version has opened. If it can't read the new file back, it restores the previous version, or names the file to rename if that fails too.
- The SHA-256 check catches a damaged download. It does not protect against a compromised GitHub account, because the checksum comes from the same release, and there is no signature yet. The README says so.
- A restore point that Windows creates but lists a few seconds late is no longer reported as skipped. When Windows does skip one, CPFS names the newest and says where System Restore lists it.
- A first Setup starts with the File Explorer entry on and the drive letter and desktop shortcut off. An existing install keeps the choices it saved.
- Setup no longer replaces a newer Auto-Private background copy with an older CPFS without asking. It tells you which version that copy still runs after an update.
- Everything new is available in Persian, Arabic and Turkish.

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

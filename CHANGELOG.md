# Changelog

This project uses [semantic versioning](https://semver.org/).

## Unreleased

- The CyClone homepage moved to cyclone9.dev. The README, the licence and the About page now link there.
- New video and cover image in the READMEs, in English and Persian.
- New page, Connections from this PC: lists the shared folders this PC is connected to on other computers, grouped by server, with the drive letter, account, state and whether the drive reconnects at sign-in. It only reads, and it works only in a window opened without administrator rights.
- This PC's connections can now disconnect a connection or every connection to one server, after you confirm. Windows is never told to force it unless it refused because files are open and you confirmed a second time. Disconnecting a drive also removes it from the next sign-in. A new section explains Windows' one-user-name-per-server rule (error 1219) and opens Credential Manager, and the Windows guide on Connect mentions the error.
- New look for Setup and Uninstall: Setup opens with a card that says whether sharing is on, as far as CPFS could read it, with the share address and Copy and Open folder; its fields sit in three groups; both pages keep the summary, Show details and the main button in a bar at the bottom. Plan and progress states are shown as a dot with a word instead of coloured boxes, and Uninstall moved to the bottom of the menu.
- New look for Monitor: four numbers at the top (devices, data down and up, failed sign-ins in 24 hours), one list of connected devices with the naming actions under a "…" button, and Recent activity beside Being copied now. Failed sign-ins are listed only when there is one. Recent activity also works without administrator rights.
- New look for Connect: the share address and username at the top, one tab per kind of device (the last one is remembered), numbered steps, and the QR code shown beside the iPhone and Android steps instead of in a dialog.
- Optional tray icon, off by default and switched on in Preferences: it says whether sharing is on and whether this PC is on a Private or a Public network, and its menu opens CPFS or the shared folder. It runs as its own process and never with administrator rights, so it can't be switched from a window opened as administrator.
- Start with Windows: a switch in Preferences, under the tray icon, starts this copy's tray icon when you sign in, through your own Run entry in the registry and never as administrator. Preferences says when the entry starts another copy or a copy that is gone, offers Use this copy, and points to Startup apps when Windows has CPFS switched off there. Uninstall removes the entry when it is CPFS's own, leaves anything else of that name alone, and closes the tray icon, which no longer comes back by itself once CPFS is uninstalled.
- A window opened as administrator can now turn the tray icon off, but still not on.
- Minimize to tray: while the tray icon runs, minimizing a window opened without administrator rights hides it from the taskbar, and Open CPFS in the tray brings it back. A window opened as administrator always minimizes to the taskbar.
- Monitor's four numbers are cards in one row when the window is wide enough, with "since the service started" and the current rate as their captions. On Connect, the share address has its own row and wraps only after a backslash, the username has a Copy button, the device tabs show the chosen one with an accent underline, and the steps start at the top beside the QR code.
- Update now in About: after a check finds a newer release, CPFS can download it from GitHub, check it against the release's SHA-256 checksum, size and version, start it once as a test, and replace the exe it runs from, keeping the previous one until the new version has opened. Only when you press the button, never from a window opened as administrator, and never for the background copy in Program Files. A copy on a network drive, or in a folder CPFS can't write to, gets Save as instead.
- Setup no longer replaces a newer Auto-Private background copy with an older CPFS without asking, and tells you which version the copy still runs after an update. When that copy is the only change, the restore point starts unticked.
- The READMEs describe the tray icon and starting with Windows, This PC's connections and error 1219, and Updating with what its SHA-256 check does and doesn't protect against.

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

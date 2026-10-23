# Changelog

This project uses [semantic versioning](https://semver.org/).

## Unreleased

- README in Persian, Arabic and Turkish.
- The Windows app starts: one exe with Setup, Monitor, Sync, Connect and Uninstall pages, plus Preferences and About. Only Setup has content so far; the other pages say they come in a later version.
- Setup shows your existing CPFS install as found, read-only: the settings file, the SMB account, share, firewall rules, tasks, security settings, networks, drive letters, shortcut and data folder. It changes nothing on Windows.
- "Continue as administrator" restarts CPFS with administrator rights to read what needs them, and shows the drive letters in use in your own session.
- Theme (System, Light, Dark), language and a font per language are saved for your Windows account. English is complete; Persian, Arabic and Turkish can be picked but still show English.
- About shows the version, the log with Open log and Copy report, and the licences: MIT for the code, the brand files excluded, and the third-party notices.
- Settings files written by every earlier CPFS version are read, including single-item lists and files with or without a byte order mark.
- README: tagline, description and the licence of the brand files and fonts.
- Setup reads the SMB account in milliseconds instead of about 20 seconds, and each section appears as soon as it has been read. The log shows how long each one took.
- On Windows 11 the app uses Segoe UI Variable as intended: the Text size for body text and the Display size for page titles. Windows 10 keeps Segoe UI.
- Numbers and dates follow the app's language, not the Windows regional format. About shows the program file's size and date.
- Connect page: every address of the PC (`ComputerName.local`, each Wi-Fi or LAN address, the VPN address and MagicDNS name), each marked as same-network or VPN, with steps for Windows, macOS, Linux, Android and iPhone/iPad, a copy button for every value and QR codes drawn by the built-in encoder. No password appears anywhere. Works without administrator rights.
- Monitor page (administrator): connected devices, per-share and server traffic, open files and failed network sign-ins of the last 24 hours with their reason, refreshed every 2 seconds. Disconnect closes one device's session after you confirm it; it changes no setting.
- About labels the tagline and the motto.
- Monitor: Disconnect now confirms that the device's session has closed, so a successful disconnect no longer shows as failed on Windows builds where `ForceClose` returns no code.
- Preferences shows where your preferences are saved in a card of its own, with the path wrapping in a narrow window.
- README: the motto, with tagline and motto labelled in all four languages.
- Connect page: the client cards sit two to a row when the window is wide enough to read them, and one to a row when it is narrower.
- Setup page: each section folds open and closed, with its status and a one-line summary in the header; sections with an error or that need administrator rights open on their own, and Expand all and Collapse all work on every section at once.
- The log records each failed network sign-in the first time Monitor shows it (time, account name and reason code), and the result of every Disconnect.
- The app uses the final CPFS app icon.
- Setup: Expand all and Collapse all use a matching pair of icons.
- Monitor: Disconnect no longer ends in an error after the device was dropped, and any failure of Disconnect is shown on the page instead of the crash report.
- Monitor: a connected device shows its network name under its IP when Windows can look one up, and you can give a device your own name (Name this device, Rename, Forget name). Names are saved for your Windows account in `%LOCALAPPDATA%\CPFS\devices.json`, keyed by the device's MAC address, or by its IP when it has none (VPN).

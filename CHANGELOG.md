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

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

## What CPFS does

CPFS shares one folder on a Windows 10 or 11 PC with Windows, macOS, Linux, Android and iOS devices. They connect by the PC's name (ComputerName.local), by its Wi-Fi or LAN address, or by a VPN address, and a phone can connect with a QR code. Sharing is limited to that folder, with a separate account and hardened Windows settings, and the uninstall puts the original settings back.

## Download and first run

CPFS is one file, `Cpfs-0.8.0.exe`, on the [releases page](https://github.com/cyclone9/CPFS/releases) on GitHub. There is no installer: save the file anywhere and run it. CPFS asks for administrator rights only when Setup, Monitor or Uninstall need them. Connect, Preferences and About work without.

The file is not signed, so the first time you run it Windows SmartScreen shows "Windows protected your PC". Select More info, then Run anyway.

To check that the file is the one that was published, compute its SHA-256 in PowerShell and compare it with the value on the release page:

```powershell
Get-FileHash .\Cpfs-0.8.0.exe -Algorithm SHA256
```

CPFS needs Windows 10 or 11 and .NET Framework 4.8, which both include. It has been tested on Windows 11. Windows 10 is supported but has not been tested yet.

## Features

- Pages for Setup, Monitor, Connect and Uninstall.
- A Windows 11 look with System, Light and Dark themes.
- English, Persian, Arabic and Turkish. CPFS follows the Windows display language when it is one of these, and you can choose another in Preferences.
- Setup can give the shared folder a drive letter, a desktop shortcut and an entry in File Explorer's navigation pane. You choose the letter and the names, and all three carry the CPFS icon. On the drive letter, Explorer may show the disk's own name instead of yours. The navigation pane entry is the "Show in File Explorer" switch, on by default.
- Before Setup or Uninstall changes Windows, CPFS creates a restore point unless you untick it. Windows skips a new one within 24 hours of the last, and CPFS tells you when that happens.
- A connect guide for Windows, macOS, Linux, Android and iOS, with QR codes for phones, over the `.local` name, the Wi-Fi or LAN IP address, or the VPN address.
- Monitor shows the connected devices and their traffic, the large transfers being copied now ("Being copied now"), Recent activity and failed sign-ins. Recent activity lists every file added, changed, renamed or deleted in the shared folder, whether from any device or from this PC. CPFS can't tell which device made a change.
- An optional "Save to a log file" switch for Recent activity, off by default.
- An optional Auto-Private switch and an optional change of the computer name, both described below.
- A "Check for updates" button in About, and an optional check when CPFS starts, off by default. It asks GitHub only and downloads nothing.
- Uninstall, which restores the original Windows settings.
- No deleting of your files. CPFS never deletes, moves or overwrites anything in the shared folder on its own.
- Existing installs made with the earlier CPFS scripts are picked up.

## Auto-Private

On the local network, your devices connect only over networks that Windows calls Private (see Security hardening). Auto-Private is a switch under More options in Setup. It marks every network this PC joins for the first time as Private, so your devices can connect without running Setup again. It is off by default, and off is safer.

That includes café, hotel and airport Wi-Fi. CPFS can't tell a safe network from an unsafe one, and strangers on a network marked Private could try to guess your SMB password.

- When a network connects, a scheduled task named `CPFS-AutoPrivate` marks it Private if it is Public and CPFS has not seen it before. Each network is switched once.
- A network you left Public in Setup, and a network on a VPN adapter, are never changed.
- The task runs as SYSTEM from a copy of CPFS in `C:\Program Files\CPFS\Cpfs.exe`, which only SYSTEM and Administrators can change. It refuses to run if that folder or `C:\ProgramData\CPFS` is open to other users.
- The names of the networks it switched are kept in `C:\ProgramData\CPFS\auto-private.log`, so Uninstall can set them back to Public.
- Turning the switch off in Setup removes the task and the copy.

## Computer name

Devices find this PC by its computer name followed by `.local`. The Computer name field under More options in Setup shows the current name, and nothing is renamed unless you change it. Use letters, digits and hyphens, up to 15 characters, and not only digits.

Windows takes a new name only after a restart. CPFS doesn't rename a PC that is joined to a domain. On a PC joined to Entra ID, which an organisation may manage, it renames only if you tick the item that asks. The original name is kept in the backup, and Uninstall offers it back, again after a restart.

## Security hardening

Setup changes the Windows settings in this section and no others. It never makes a setting weaker than Windows' default, and a setting that is already stricter than CPFS's value is kept as it is. Before the first change, Setup saves each original value in `config.json` under `Backup`. It never takes the backup again, so Uninstall restores the values from before CPFS.

Setup shows a plan before it changes anything. Using an existing account, replacing a share and switching a Public network to Private are done only if you tick them.

### SMB account

| Setting | What CPFS sets | Why |
|---|---|---|
| The account | A separate local account, named in Setup as the SMB username | Devices sign in with it, never with your Windows account |
| Groups | Member of Users, not of Administrators | A leaked file-sharing password can't change Windows |
| Password | The one you type, set through Windows and not saved by CPFS | Leave the box empty on a later run to keep the current one |
| Password expiry | Never expires | An expired password would lock every device out |
| Sign-in screen | Hidden, with the value `0` under `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList` | The account is for devices, not for people at this PC |
| An account of that name already exists | Used only if you tick it; its settings are saved first, and removing it from Administrators is offered | CPFS never takes over an account silently |

### Shared folder and share

| Setting | What CPFS sets | Why |
|---|---|---|
| Folder permission | The SMB account gets Modify on the shared folder and what is in it | Devices can add and change files |
| Share permission | The SMB account has full control of the share, the only entry CPFS sets; other entries are removed only if you tick them | Only that account reaches the share |
| Folder listing | Folders the account can't open stay hidden | Nothing outside its reach is shown |
| Offline files | Off for the share | No offline copies of your files |
| Strict mode (optional) | The share requires SMB 3 encryption | Devices that can't encrypt are refused |
| Administrative shares (`ADMIN$`, `C$` and so on) | `AutoShareWks` set to `0` under `HKLM\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters`; the existing ones stay until a restart unless you tick their removal | Windows stops sharing whole drives on the network |
| A share that CPFS didn't create | Replaced only if you tick it; what it was is recorded | CPFS never removes a share without asking |

### SMB server

| Setting | What CPFS sets | Why |
|---|---|---|
| Server service | Starts automatically | Sharing doesn't work without it |
| SMB 2 and SMB 3 | On | Every current device uses one of them |
| Reject unencrypted access | On | A device that asks for encryption can't then retry without it |
| SMB 1 protocol | Off | It is old and unsafe, and stays off |
| SMB 1 Windows feature | Removed if installed; this needs a restart and also removes the SMB 1 client for very old network drives | SMB 1 can't come back by accident |

### Sign-in security

The first seven are registry values under `HKLM\SYSTEM\CurrentControlSet\Control\Lsa`, except `RestrictNullSessAccess`, which is under `HKLM\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters`.

| Setting | What CPFS sets | Why |
|---|---|---|
| `forceguest` | `0` | Network sign-ins use the account they name, never Guest |
| `LmCompatibilityLevel` | `5` | Only NTLMv2 is accepted |
| `NoLMHash` | `1` | Windows doesn't store the weak LM hash of a password |
| `RestrictAnonymous` | `1` | Anonymous users can't list shares and account names |
| `RestrictAnonymousSAM` | `1` | Anonymous users can't list the accounts of this PC |
| `EveryoneIncludesAnonymous` | `0` | The Everyone group doesn't include anonymous users |
| `RestrictNullSessAccess` | `1` | Anonymous sessions can't reach shares |
| Guest account | Off, and stays off | No access without a password |
| Account lockout | An account locks after 10 wrong passwords. This is set only when lockout is off; a threshold you already have is kept | Guessing a password gets blocked |
| Audit of failed sign-ins | Failures are recorded in the Security log; successes stay as they were | Monitor shows them |

### Firewall and networks

| Setting | What CPFS sets | Why |
|---|---|---|
| `CPFS SMB (LAN)` | Inbound TCP 445, Private networks, local subnet only | Your devices on your network can connect |
| `CPFS mDNS (LAN)` | Inbound UDP 5353, Private networks | The `.local` name works |
| `CPFS SMB (VPN)` | Inbound TCP 445 from `100.64.0.0/10` and `fd7a:115c:a1e0::/48`, only through the Tailscale or Headscale adapter, and only when such an adapter is found | Your devices connect over the VPN. An adapter on a Public network is used only if you tick it, because some hotel and mobile networks hand out addresses in that range |
| New rules | Created disabled, read back, and enabled only if they match | A rule with the wrong scope never opens a port |
| Built-in File and Printer Sharing rules that open port 445 on Public networks | Turned off and recorded, so Uninstall can turn them on again | Port 445 never opens on a Public network |
| Network type | A Public network becomes Private only if you tick it | CPFS's rules apply only to networks you trust |

### Data folder and quick access

| Setting | What CPFS sets | Why |
|---|---|---|
| `C:\ProgramData\CPFS` | Owned by Administrators; only SYSTEM and Administrators can write | A task running as SYSTEM reads from this folder, so no other user may change it |
| Auto-Private (optional) | The `CPFS-AutoPrivate` task and the copy in `C:\Program Files\CPFS`; see Auto-Private | New networks are marked Private |
| Computer name (optional) | A new name after a restart; see Computer name | Devices find the PC by it |
| Drive letter (optional) | A permanent mapping under `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\DOS Devices`, also shown in your session at once; only a free letter | The shared folder as a drive |
| Drive name and icon (optional) | Values under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\<letter>`; a name or icon the letter already has is saved first | File Explorer shows your name and the CPFS icon |
| Desktop shortcut (optional) | A `.lnk` file on the Public Desktop; a file of that name that CPFS didn't create is never overwritten | A shortcut to the shared folder |
| File Explorer entry (optional) | The keys listed under Uninstall, in the hive of the user who ran Setup | The shared folder in the navigation pane |

CPFS refuses a data folder that is a link to another place, and doesn't trust a settings file that an administrator or SYSTEM didn't write.

## Uninstall

Uninstall puts back the Windows settings Setup changed and removes what CPFS created. The shared folder and the files in it stay: CPFS never deletes your files. Before anything changes, Uninstall creates a restore point unless you untick it, and saves a copy of `config.json`, with the backup of your original settings and no password, in your Documents folder as `CPFS-config-backup-<date>-<time>.json`.

| What | What Uninstall does |
|---|---|
| The share | Disconnects devices and removes it. A share of the same name that points to another folder stays unless you tick it. |
| Folder permission and ownership | Removes the permission Setup gave the SMB account. Files and folders the account owns, such as uploads, become owned by your Windows user. You can untick this. |
| The SMB account | Deleted only if CPFS created it. An account that existed before is kept unless you tick its deletion, and gets back the settings it had before Setup. The account's profile folder is deleted only if you tick it. |
| Firewall | Removes the CPFS rules and turns the built-in rules Setup turned off back on. |
| Security settings | Puts back the values saved in the backup: the Server service start, SMB 2 and SMB 3, rejecting unencrypted access, account lockout and the registry values in Security hardening. It never sets `RestrictAnonymousSAM`, `RestrictNullSessAccess`, `NoLMHash` or `EveryoneIncludesAnonymous` weaker than a fresh Windows install has them. SMB 1 stays off, the Guest account stays off, and failed sign-ins keep being recorded. A value the backup has no original for is left as it is. |
| An install without a backup | An early CPFS version made it, so nothing is restored. You can tick an item that sets the administrative shares, the NTLM level and anonymous access back to Windows' defaults. |
| Networks | The networks Setup switched to Private, and those Auto-Private switched, go back to Public. A network that isn't connected now is changed in the saved network list. |
| Auto-Private | Removes the `CPFS-AutoPrivate` task first, then the copy `C:\Program Files\CPFS\Cpfs.exe`, then the folder `C:\Program Files\CPFS` if CPFS created it and it is empty. A folder CPFS didn't create stays. |
| Computer name | Offers the original name back. It is ticked when the PC still has the name CPFS set, and unticked when someone renamed it since. Windows takes it after a restart. |
| Drive, drive name and icon | Removes the drive mapping and the name and icon CPFS set, or puts back the name and icon the letter had before. File Explorer may show the drive until the next restart. Anything CPFS didn't set stays. |
| Desktop shortcut | Removed while it still points to the shared folder. |
| File Explorer entry | Removed while it still opens the shared folder, as described below. |
| `C:\ProgramData\CPFS` | Removed last, with `config.json` and `cpfs.ico`. Settings files that Setup set aside (`config-ignored-<date>.json`) are first copied to your Documents folder as `CPFS-config-ignored-<date>.json`; if a copy fails, the folder stays. |

The File Explorer entry consists of the keys `HKCU\Software\Classes\CLSID\{07C33AB4-B371-467B-9DC1-1AA530E251C6}` and `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{07C33AB4-B371-467B-9DC1-1AA530E251C6}`, and a `{07C33AB4-B371-467B-9DC1-1AA530E251C6}` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel`, all in the hive of the user who ran Setup. The drive icon, `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\<letter>\DefaultIcon`, is removed, or the icon the letter had before is put back. An icon CPFS didn't set stays.

## Where CPFS stores things

- `C:\ProgramData\CPFS`: `config.json` with your answers and the backup of the original settings, `config-ignored-<date>.json` (a settings file CPFS couldn't trust, which Setup renames and no longer reads), `cpfs.ico`, the CPFS icon used by the drive, the shortcut and the File Explorer entry, and, with Auto-Private on, `auto-private.log` (the networks it switched) and `auto-private-run.log` (one line for each run of the task). Only SYSTEM and Administrators can write here.
- `C:\Program Files\CPFS\Cpfs.exe`: the copy the Auto-Private task runs, present only while Auto-Private is on. Only SYSTEM and Administrators can change it.
- `%LOCALAPPDATA%\CPFS`, in the profile of the user who runs CPFS:
  - `preferences.json`: your choices in the app, such as theme, language and font.
  - `devices.json`: the names you gave connected devices.
  - `cpfs-log.txt`: the app's log.
  - `activity-log.txt` and `activity-log.old.txt`: Recent activity, only while "Save to a log file" is on. At 5 MB the file becomes `activity-log.old.txt`. It holds file names, so Copy report never includes it. Uninstall doesn't remove it, because it is your own log.
- `Documents\CPFS-config-backup-<date>-<time>.json`: the copy of `config.json` that Uninstall saves before it changes anything. Uninstall leaves it in place.
- `Documents\CPFS-config-ignored-<date>.json`: the copy of a set-aside settings file that Uninstall saves before it removes the data folder. Uninstall leaves it in place.

Uninstall removes nothing in `%LOCALAPPDATA%\CPFS`.

## Licence

The code is under the MIT licence (see `LICENSE`). The CPFS name, logo, symbols, wordmark and app icon are not covered by it and may not be used without permission. The files in `assets/brand/` are the CPFS brand files and are not covered by the MIT licence. The Vazirmatn font in `assets/fonts/` is under the SIL Open Font License 1.1 (see `OFL.txt` there).

© CyClone · https://cyclone9.ir/

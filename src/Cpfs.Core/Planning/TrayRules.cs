namespace Cpfs.Core.Planning;

// When the window works with the tray icon. Nothing elevated starts the tray, hides in it or turns it on (design B2).
public static class TrayRules
{
    // An unelevated window brings the icon back by itself only while CPFS is set up on this PC, that is a config.json
    // CPFS can trust. After Uninstall (owner decision) the icon stays away although preferences.json keeps the switch
    // on; the Preferences switch still starts it on request.
    public static bool AtWindowStart(bool elevated, bool trayIcon, Reading<DataFolderInfo> folder) =>
        !elevated && trayIcon && ConfigTrust.Trusted(folder);

    // Minimizing hides the window only when the tray is there to bring it back; otherwise it goes to the taskbar.
    public static bool HideOnMinimize(bool elevated, bool trayIcon, bool trayRunning) => !elevated && trayIcon && trayRunning;

    // An elevated window may turn the tray off (its preferences file is then switched off) but never on.
    public static bool TraySwitchEnabled(bool elevated, bool trayIcon) => !elevated || trayIcon;

    // Start with Windows is changed only unelevated, with the tray on and a value CPFS may change; in an elevated window
    // it follows the tray switch when that is turned off.
    public static bool StartSwitchEnabled(bool elevated, bool trayIcon, bool readOk, bool foreign) => !elevated && trayIcon && readOk && !foreign;
}

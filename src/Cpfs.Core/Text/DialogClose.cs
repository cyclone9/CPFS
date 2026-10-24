namespace Cpfs.Core.Text;

// How a dialog was closed. Only the dialog's own primary button confirms a change to Windows; Esc, the close
// button, the title bar's X, Alt+F4 or a closed owner never do.
public enum DialogClose
{
    Window,
    PrimaryButton,
    SecondaryButton,
    CloseButton,
    Escape,
}

public static class DialogCloses
{
    public static bool Confirms(DialogClose close) => close == DialogClose.PrimaryButton;
}

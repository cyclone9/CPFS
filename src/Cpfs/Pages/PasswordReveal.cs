using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using PasswordBox = System.Windows.Controls.PasswordBox;
using TextBox = System.Windows.Controls.TextBox;

namespace Cpfs.Pages;

// The eye and clear buttons beside a password box. WPF-UI's own PasswordBox has a reveal button but keeps the password in a
// plain string property for its whole life; this keeps WPF's PasswordBox and puts the text in a managed string only
// while the user has asked to see it. The revealed box can't copy, cut, drag or undo, so the text can't reach the
// clipboard or linger in an undo stack.
internal sealed class PasswordReveal
{
    private readonly PasswordBox _box;
    private readonly TextBox _plain;
    private readonly Button _button;
    private bool _shown;

    public PasswordReveal(PasswordBox box, TextBox plain, Button button, Button clear)
    {
        _box = box;
        _plain = plain;
        _button = button;
        clear.Click += (_, _) => Clear();
        _plain.IsUndoEnabled = false;
        DataObject.AddCopyingHandler(_plain, (_, e) => e.CancelCommand());
        _plain.TextChanged += OnPlainChanged;
        _button.Click += (_, _) =>
        {
            if (_shown)
            {
                Hide();
                _box.Focus();
            }
            else
            {
                Reveal();
            }
        };
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Loc.Culture))
            {
                Label();
            }
        };
        Label();
    }

    public void Hide()
    {
        if (!_shown)
        {
            return;
        }

        _shown = false;
        _plain.Clear();
        _plain.Visibility = Visibility.Collapsed;
        _box.Visibility = Visibility.Visible;
        Label();
    }

    // Clearing while the text is shown empties the shown copy too, and the box follows it (OnPlainChanged).
    private void Clear()
    {
        if (_shown)
        {
            _plain.Clear();
            _plain.Focus();
        }
        else
        {
            _box.Clear();
            _box.Focus();
        }
    }

    private void Reveal()
    {
        _plain.Text = _box.Password;
        _shown = true;
        _box.Visibility = Visibility.Collapsed;
        _plain.Visibility = Visibility.Visible;
        _plain.CaretIndex = _plain.Text.Length;
        _plain.Focus();
        Label();
    }

    // Typing while the text is shown goes on in the password box, so validation and Apply read it from there.
    private void OnPlainChanged(object sender, TextChangedEventArgs e)
    {
        if (_shown)
        {
            _box.Password = _plain.Text;
        }
    }

    private void Label()
    {
        var name = Loc.Instance[_shown ? "Setup_Form_PasswordHide" : "Setup_Form_PasswordShow"];
        _button.Icon = new SymbolIcon { Symbol = _shown ? SymbolRegular.EyeOff24 : SymbolRegular.Eye24 };
        _button.ToolTip = name;
        System.Windows.Automation.AutomationProperties.SetName(_button, name);
    }
}

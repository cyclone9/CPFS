using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cpfs.Core.Text;
using Wpf.Ui.Controls;

namespace Cpfs.Pages;

// A WPF-UI message box whose buttons sit in the content, in a ButtonRowPanel. WPF-UI 4.3.0's own template puts its
// three buttons in equal columns of a fixed-width window with single-line labels, which cut "Continue without it"
// and "Open System Protection" in English already, and longer translations or larger text worse.
// It also records how it was closed (ClosedBy), set only by its own buttons and Esc, so a caller can refuse to act on
// anything but the primary button, and the log shows which way every dialog closed.
internal sealed class ChoiceDialog : Wpf.Ui.Controls.MessageBox
{
    private readonly ButtonRowPanel _buttons = new() { Margin = new Thickness(0, 8, 0, 0) };

    // Stays Window when the title bar's X, Alt+F4 or anything else closes it.
    public DialogClose ClosedBy { get; private set; } = DialogClose.Window;

    public ChoiceDialog(object message)
    {
        // An implicit style is looked up by the exact type, so the subclass asks for the base class's style.
        SetResourceReference(StyleProperty, typeof(Wpf.Ui.Controls.MessageBox));
        var content = new StackPanel();
        content.Children.Add(message as UIElement ?? new System.Windows.Controls.TextBlock { Text = message?.ToString(), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(_buttons);
        Content = content;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ClosedBy = DialogClose.Escape;
                OnButtonClick(Wpf.Ui.Controls.MessageBoxButton.Close);
            }
        };
    }

    // Shows the dialog and logs how it closed, on one line with what the caller adds (how its expander was used).
    public async System.Threading.Tasks.Task<DialogClose> ShowLoggedAsync(string area, string name, System.Func<string>? more = null)
    {
        var result = await ShowDialogAsync();
        AppLog.Write(area, "Dialog " + name + " closed by " + ClosedBy + " (result " + result + ")" + (more?.Invoke() ?? ""));
        return ClosedBy;
    }

    public Wpf.Ui.Controls.Button AddButton(string text, Wpf.Ui.Controls.MessageBoxButton result, ControlAppearance appearance, bool isDefault = false, bool isCancel = false)
    {
        var button = new Wpf.Ui.Controls.Button
        {
            Content = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            Appearance = appearance,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) =>
        {
            ClosedBy = result switch
            {
                Wpf.Ui.Controls.MessageBoxButton.Primary => DialogClose.PrimaryButton,
                Wpf.Ui.Controls.MessageBoxButton.Secondary => DialogClose.SecondaryButton,
                _ => DialogClose.CloseButton,
            };
            OnButtonClick(result);
        };
        _buttons.Children.Add(button);
        return button;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // The template's button bar is its row 2, next to the content presenter; it has no name of its own.
        if (GetTemplateChild("ContentPresenter") is FrameworkElement presenter && presenter.Parent is Grid grid)
        {
            foreach (UIElement child in grid.Children)
            {
                if (Grid.GetRow(child) == 2)
                {
                    child.Visibility = Visibility.Collapsed;
                }
            }
        }
    }
}

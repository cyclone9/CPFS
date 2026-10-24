using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Cpfs;

// Every copy button says what happened on itself: a checkmark and "Copied" for a moment, or an error mark with the
// reason. No toast, so nothing covers the page.
internal static class Feedback
{
    private static readonly TimeSpan SuccessTime = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ErrorTime = TimeSpan.FromSeconds(5);

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("CopyState", typeof(CopyState), typeof(Feedback));

    // Copies a value the user asked for (an address, user name, path or command; never a password).
    public static void Copy(object sender, string label, string value)
    {
        var loc = Loc.Instance;
        try
        {
            Clipboard.SetText(value);
            AppLog.Write("copy", "Copied " + label);
            Show(sender, true, loc["Common_Copied"]);
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            AppLog.Write("copy", "Copy failed: " + ex.Message);
            Show(sender, false, loc.Format("Common_CopyFailed", ex.Message));
        }
    }

    public static void Show(object sender, bool success, string text)
    {
        if (sender is not Button button)
        {
            return;
        }

        if (button.GetValue(StateProperty) is not CopyState state)
        {
            state = new CopyState(button);
            button.SetValue(StateProperty, state);
        }

        state.Show(success, text);
    }

    private sealed class CopyState
    {
        private readonly Button _button;
        private readonly DispatcherTimer _timer;
        private readonly ToolTip _tip;
        private readonly TextBlock _text;
        private IconElement? _icon;
        private bool _active;

        public CopyState(Button button)
        {
            _button = button;
            _timer = new DispatcherTimer();
            _timer.Tick += (_, _) => End();
            _text = new TextBlock();
            AutomationProperties.SetLiveSetting(_text, AutomationLiveSetting.Polite);
            _tip = new ToolTip { Content = _text, PlacementTarget = button, Placement = PlacementMode.Top };
            button.Unloaded += (_, _) => End();
        }

        public void Show(bool success, string text)
        {
            if (!_active)
            {
                _icon = _button.Icon;
                _active = true;

                // The button's own tooltip ("Copy version") would sit on top of the result.
                ToolTipService.SetIsEnabled(_button, false);
            }

            var icon = new SymbolIcon { Symbol = success ? SymbolRegular.Checkmark24 : SymbolRegular.ErrorCircle24 };
            icon.SetResourceReference(IconElement.ForegroundProperty, success ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");
            _button.Icon = icon;
            _text.Text = text;
            _tip.FlowDirection = _button.FlowDirection;
            _tip.IsOpen = true;
            if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            {
                UIElementAutomationPeer.CreatePeerForElement(_text)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }

            _timer.Stop();
            _timer.Interval = success ? SuccessTime : ErrorTime;
            _timer.Start();
        }

        private void End()
        {
            _timer.Stop();
            if (!_active)
            {
                return;
            }

            _active = false;
            _tip.IsOpen = false;
            _button.Icon = _icon;
            _button.ClearValue(ToolTipService.IsEnabledProperty);
        }
    }
}

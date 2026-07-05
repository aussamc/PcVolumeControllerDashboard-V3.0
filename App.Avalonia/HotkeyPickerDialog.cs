using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Modal dialog that captures a key combination for a global hotkey. Avalonia
/// counterpart of the WPF host's HotkeyPickerDialog. Returns the chosen
/// <see cref="HotkeyBinding"/> (an unassigned binding when cleared) or null when
/// cancelled.
/// </summary>
public sealed class HotkeyPickerDialog : Window
{
    private readonly TextBlock _comboText;
    private readonly Button _saveButton;

    private int _modifiers;
    private int _virtualKey;

    private HotkeyPickerDialog(string actionName, HotkeyBinding? current)
    {
        Title = "Set hotkey";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Focusable = true;

        // Nothing in the dialog's content (Buttons/TextBlocks) claims keyboard focus
        // on its own, so without this the window itself never becomes the focused
        // element and OnPreviewKeyDown's tunnel handler never sees a KeyDown.
        Opened += (_, _) => Focus();

        _comboText = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Text = "Press a key…",
        };

        _saveButton = new Button { Content = "Save", IsEnabled = false, MinWidth = 84, IsDefault = true };
        _saveButton.Click += (_, _) => Close(new HotkeyBinding { Enabled = true, Modifiers = _modifiers, VirtualKey = _virtualKey });

        var clearButton = new Button { Content = "Clear", MinWidth = 84 };
        clearButton.Click += (_, _) => Close(new HotkeyBinding { Enabled = false });

        var cancelButton = new Button { Content = "Cancel", MinWidth = 84, IsCancel = true };
        cancelButton.Click += (_, _) => Close(null);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { _saveButton, clearButton, cancelButton },
        };

        var combo = new Border
        {
            BorderThickness = new Avalonia.Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(12),
            Margin = new Avalonia.Thickness(0, 12, 0, 12),
            Child = _comboText,
        };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new TextBlock { Text = $"Set hotkey for: {actionName}", FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Press a key combination (hold Ctrl/Alt/Shift/Win). Press Esc to cancel.",
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(0, 4, 0, 0),
                },
                combo,
                buttons,
            },
        };

        if (current?.IsAssigned == true)
        {
            _modifiers = current.Modifiers;
            _virtualKey = current.VirtualKey;
            _comboText.Text = HotkeyDisplay.Format(_modifiers, _virtualKey);
            _saveButton.IsEnabled = true;
        }

        // Tunnel + handledEventsToo so navigation keys (Tab/arrows) are captured here
        // rather than being consumed by focus movement inside the dialog.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Shows the picker modally and returns the chosen binding, or null if cancelled.</summary>
    public static Task<HotkeyBinding?> ShowAsync(Window owner, string actionName, HotkeyBinding? current) =>
        new HotkeyPickerDialog(actionName, current).ShowDialog<HotkeyBinding?>(owner);

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        Key key = e.Key;

        if (HotkeyCapture.IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            e.Handled = true;
            Close(null);
            return;
        }

        int vk = HotkeyCapture.ToVirtualKey(key);
        if (vk == 0)
        {
            e.Handled = true; // unmapped key — ignore
            return;
        }

        _modifiers = HotkeyCapture.ToModifierFlags(e.KeyModifiers);
        _virtualKey = vk;
        _comboText.Text = HotkeyDisplay.Format(_modifiers, _virtualKey);
        _saveButton.IsEnabled = true;
        e.Handled = true;
    }
}

using System.Windows;
using System.Windows.Input;

namespace PcVolumeControllerDashboard;

public partial class HotkeyPickerDialog : Window
{
    public HotkeyBinding? Result { get; private set; }

    private int _capturedModifiers;
    private int _capturedVk;

    public HotkeyPickerDialog(string actionName, HotkeyBinding? current)
    {
        InitializeComponent();
        ActionNameTextBlock.Text = actionName;

        if (current?.IsAssigned == true)
        {
            _capturedModifiers = current.Modifiers;
            _capturedVk = current.VirtualKey;
            UpdateDisplay();
            OkButton.IsEnabled = true;
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Ignore modifier-only presses
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }

        ModifierKeys mods = Keyboard.Modifiers;
        int modFlags = 0;
        if ((mods & ModifierKeys.Alt)     != 0) modFlags |= 1;
        if ((mods & ModifierKeys.Control) != 0) modFlags |= 2;
        if ((mods & ModifierKeys.Shift)   != 0) modFlags |= 4;
        if ((mods & ModifierKeys.Windows) != 0) modFlags |= 8;

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            e.Handled = true;
            return;
        }

        _capturedModifiers = modFlags;
        _capturedVk = vk;
        UpdateDisplay();
        OkButton.IsEnabled = true;
        e.Handled = true;
    }

    private void UpdateDisplay()
    {
        var binding = new HotkeyBinding { Enabled = true, Modifiers = _capturedModifiers, VirtualKey = _capturedVk };
        PressedComboTextBlock.Text = binding.ToDisplayString();
        PressedComboTextBlock.Foreground = System.Windows.Media.Brushes.Black;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Result = new HotkeyBinding { Enabled = true, Modifiers = _capturedModifiers, VirtualKey = _capturedVk };
        DialogResult = true;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        Result = new HotkeyBinding { Enabled = false };
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PcVolumeControllerDashboard.App;

public enum DialogButtons { Ok, OkCancel, YesNo }

public enum DialogResult { Ok, Cancel, Yes, No }

/// <summary>
/// Minimal modal message dialog helper — Avalonia has no built-in MessageBox.
/// Builds a small owner-centred window in code (no AXAML) so any view can show an
/// info / confirm / error prompt and await the user's choice. Shared by the
/// destructive-action confirmations, and reused by later flows (errors, About,
/// the first-run wizard).
/// </summary>
public static class Dialogs
{
    /// <summary>Shows a modal dialog and resolves with the button the user chose.</summary>
    public static Task<DialogResult> ShowAsync(Window owner, string title, string message, DialogButtons buttons = DialogButtons.Ok)
    {
        var tcs = new TaskCompletionSource<DialogResult>();

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MinWidth = 320,
            MaxWidth = 560,
        };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        void AddButton(string text, DialogResult result, bool isDefault = false, bool isCancel = false)
        {
            var button = new Button { Content = text, MinWidth = 84, IsDefault = isDefault, IsCancel = isCancel };
            button.Click += (_, _) => { tcs.TrySetResult(result); dialog.Close(); };
            buttonRow.Children.Add(button);
        }

        // Default result when the dialog is dismissed via the window chrome (X / Esc).
        DialogResult dismissResult;
        switch (buttons)
        {
            case DialogButtons.OkCancel:
                dismissResult = DialogResult.Cancel;
                AddButton("Cancel", DialogResult.Cancel, isCancel: true);
                AddButton("OK", DialogResult.Ok, isDefault: true);
                break;
            case DialogButtons.YesNo:
                dismissResult = DialogResult.No;
                AddButton("No", DialogResult.No, isCancel: true);
                AddButton("Yes", DialogResult.Yes, isDefault: true);
                break;
            default:
                dismissResult = DialogResult.Ok;
                AddButton("OK", DialogResult.Ok, isDefault: true, isCancel: true);
                break;
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 16 },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 500 },
                buttonRow,
            },
        };

        dialog.Closed += (_, _) => tcs.TrySetResult(dismissResult); // no-op if a button already resolved it

        dialog.ShowDialog(owner); // modal; result delivered through tcs
        return tcs.Task;
    }

    /// <summary>Shows a Yes/No confirmation; resolves true only when the user confirms.</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message)
        => await ShowAsync(owner, title, message, DialogButtons.YesNo) == DialogResult.Yes;

    /// <summary>
    /// Shows an About dialog with selectable info text and a button that opens the
    /// project page in the default browser.
    /// </summary>
    public static Task ShowAboutAsync(Window owner, string heading, string info, string projectUrl)
    {
        var tcs = new TaskCompletionSource();

        var dialog = new Window
        {
            Title = "About",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MinWidth = 380,
            MaxWidth = 560,
        };

        var openButton = new Button { Content = "Open project page", MinWidth = 150 };
        openButton.Click += (_, _) => OpenUrl(projectUrl);

        var closeButton = new Button { Content = "Close", MinWidth = 84, IsDefault = true, IsCancel = true };
        closeButton.Click += (_, _) => { tcs.TrySetResult(); dialog.Close(); };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { openButton, closeButton },
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold, FontSize = 16 },
                new SelectableTextBlock { Text = info, TextWrapping = TextWrapping.Wrap, MaxWidth = 500 },
                buttonRow,
            },
        };

        dialog.Closed += (_, _) => tcs.TrySetResult();
        dialog.ShowDialog(owner);
        return tcs.Task;
    }

    /// <summary>Opens a URL in the user's default browser (best-effort; never throws).</summary>
    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* best-effort: no browser / blocked */ }
    }
}

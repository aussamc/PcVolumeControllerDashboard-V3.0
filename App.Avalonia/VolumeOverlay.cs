using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using PcVolumeControllerDashboard.App.Services;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Transient on-screen volume popup shown when a knob turns / a preset or mute
/// fires. A borderless, topmost, non-activating window that appears at the
/// configured screen corner with the channel name, a volume bar, and the
/// percentage, then auto-hides after a timeout. The Avalonia counterpart of the
/// WPF host's VolumeOverlayWindow.
/// </summary>
public sealed class VolumeOverlay : Window
{
    private const double OverlayWidth = 320;
    private const double OverlayHeight = 84;
    private const int ScreenMarginDip = 28;
    private const int FadeStepMs = 16;        // ~60 fps
    private const double FadeStep = 0.06;     // ~17 steps ≈ 270 ms fade-out

    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x1E, 0x1E));
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush FillBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

    private readonly TextBlock _label;
    private readonly TextBlock _value;
    private readonly Border _fill;
    private readonly Border _track;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _fadeTimer;

    public VolumeOverlay()
    {
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;     // never steal focus from the foreground app
        CanResize = false;
        Width = OverlayWidth;
        Height = OverlayHeight;

        _label = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White };
        _value = new TextBlock { FontSize = 15, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_label, 0);
        Grid.SetColumn(_value, 1);
        header.Children.Add(_label);
        header.Children.Add(_value);

        // Simple two-Border bar (track + proportional fill) — no ProgressBar theming needed.
        _fill = new Border { Background = FillBrush, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
        _track = new Border
        {
            Background = TrackBrush,
            CornerRadius = new CornerRadius(4),
            Height = 10,
            Margin = new Thickness(0, 10, 0, 0),
            Child = _fill,
        };

        Content = new Border
        {
            Background = PanelBrush,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 14),
            Child = new StackPanel { Children = { header, _track } },
        };

        // After the visible timeout, fade the window out rather than cutting it off.
        // Construct the fade timer before wiring the hide timer's Tick so the lambda
        // captures an already-assigned field (avoids a spurious nullable warning).
        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FadeStepMs) };
        _fadeTimer.Tick += OnFadeTick;

        _hideTimer = new DispatcherTimer();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); _fadeTimer.Start(); };
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        double next = Opacity - FadeStep;
        if (next <= 0)
        {
            _fadeTimer.Stop();
            Hide();
            Opacity = 1; // ready for the next show
            return;
        }
        Opacity = next;
    }

    /// <summary>Shows/refreshes the overlay for a change and (re)starts the auto-hide timer.</summary>
    public void ShowVolume(VolumeOverlayInfo info, string position, double timeoutSeconds)
    {
        _label.Text = info.Label;
        _value.Text = info.Muted ? "Muted" : $"{info.VolumePercent}%";
        _fill.Background = info.Muted ? MutedBrush : FillBrush;

        // Proportional fill width within the track's inner width.
        double trackWidth = OverlayWidth - 36; // panel padding (18 each side)
        _fill.Width = Math.Max(0, trackWidth * Math.Clamp(info.VolumePercent, 0, 100) / 100.0);
        _fill.Height = 10;

        // Cancel any in-flight fade-out and restore full opacity for this update.
        _fadeTimer.Stop();
        Opacity = 1;

        if (!IsVisible) Show();
        PositionOnScreen(position);

        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 0.5, 10));
        _hideTimer.Start();
    }

    private void PositionOnScreen(string position)
    {
        Screen? screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;

        PixelRect area = screen.WorkingArea;     // physical pixels
        double scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        int w = (int)(OverlayWidth * scale);
        int h = (int)(OverlayHeight * scale);
        int margin = (int)(ScreenMarginDip * scale);

        string pos = position ?? "BottomCenter";
        int x = pos.Contains("Left", StringComparison.OrdinalIgnoreCase) ? area.X + margin
              : pos.Contains("Right", StringComparison.OrdinalIgnoreCase) ? area.Right - w - margin
              : area.X + (area.Width - w) / 2;
        int y = pos.StartsWith("Top", StringComparison.OrdinalIgnoreCase) ? area.Y + margin
              : area.Bottom - h - margin;

        Position = new PixelPoint(x, y);
    }
}

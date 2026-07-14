using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using PcVolumeControllerDashboard.App.Services;
using PcVolumeControllerDashboard.Core;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Transient on-screen volume popup shown when a knob turns / a preset or mute
/// fires. A borderless, topmost, non-activating window that appears at the
/// configured screen corner with the channel name, a volume bar, and the
/// percentage, then auto-hides after a timeout. The Avalonia counterpart of the
/// WPF host's VolumeOverlayWindow.
///
/// A mute toggle switches to a distinct mute layout — a speaker glyph, large
/// "Muted"/"Unmuted" text, and the volume bar hidden — matching the WPF host's
/// ShowMuteOverlay. A plain volume change (even while the target is muted) keeps
/// the normal volume-bar view.
/// </summary>
public sealed class VolumeOverlay : Window
{
    // Base (unscaled) design size. The panel lays out in this fixed coordinate space
    // and a Viewbox scales the whole visual by the user's OverlayScale, so fonts, the
    // bar, and the mute glyph all scale together (O2).
    private const double OverlayWidth = 320;
    private const double OverlayHeight = 84;
    private const int ScreenMarginDip = 28;
    private const int FadeStepMs = 16;        // ~60 fps
    private const double FadeStep = 0.06;     // ~17 steps ≈ 270 ms fade-out

    // Speaker glyph geometries (24×24 space, scaled by a Viewbox). The cone body is
    // filled; the state mark (an X for muted, sound waves for unmuted) is stroked.
    private const string SpeakerBodyGeometry = "M3,9 L3,15 L7,15 L12,20 L12,4 L7,9 Z";
    private const string MutedMarkGeometry = "M15,9 L21,15 M21,9 L15,15";
    private const string UnmutedMarkGeometry = "M15,8 A5,5 0 0 1 15,16 M17.5,5.5 A8,8 0 0 1 17.5,18.5";

    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x1E, 0x1E));
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush FillBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

    private readonly TextBlock _label;
    private readonly TextBlock _value;
    private readonly Border _fill;
    private readonly Border _track;
    private readonly StackPanel _muteRow;
    private readonly TextBlock _muteStatus;
    private readonly ShapePath _mutedMark;
    private readonly ShapePath _unmutedMark;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _fadeTimer;

    // The opacity this show is targeting (user's OverlayOpacity). The fade-out animates
    // from here toward 0, and each show/hide resets to it rather than a hard-coded 1.
    private double _baseOpacity = 1.0;

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

        // Mute layout: speaker glyph + "Muted"/"Unmuted". Shown instead of the bar on
        // a mute toggle. The cone body is always drawn; the state mark toggles.
        var speakerBody = new ShapePath { Data = Geometry.Parse(SpeakerBodyGeometry), Fill = Brushes.White };
        _mutedMark = new ShapePath { Data = Geometry.Parse(MutedMarkGeometry), Stroke = Brushes.White, StrokeThickness = 2 };
        _unmutedMark = new ShapePath { Data = Geometry.Parse(UnmutedMarkGeometry), Stroke = Brushes.White, StrokeThickness = 2 };

        var speakerIcon = new Viewbox
        {
            Width = 26,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Canvas
            {
                Width = 24,
                Height = 24,
                Children = { speakerBody, _mutedMark, _unmutedMark },
            },
        };

        _muteStatus = new TextBlock
        {
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _muteRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(0, 6, 0, 0),
            IsVisible = false,
            Children = { speakerIcon, _muteStatus },
        };

        // Fixed-size design panel laid out in the base coordinate space; the Viewbox
        // scales it to fill the (scaled) window client area, so O2's scale factor
        // enlarges/shrinks everything uniformly without re-doing any layout math.
        var panel = new Border
        {
            Width = OverlayWidth,
            Height = OverlayHeight,
            Background = PanelBrush,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 14),
            Child = new StackPanel { Children = { header, _track, _muteRow } },
        };

        Content = new Viewbox { Stretch = Stretch.Fill, Child = panel };

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
            Opacity = _baseOpacity; // ready for the next show
            return;
        }
        Opacity = next;
    }

    /// <summary>
    /// Shows/refreshes the overlay for a change and (re)starts the auto-hide timer.
    /// <paramref name="opacity"/> (O1) and <paramref name="scale"/> (O2) are clamped
    /// here; <paramref name="screen"/> (O4) targets a specific monitor — null means the
    /// primary screen.
    /// </summary>
    public void ShowVolume(VolumeOverlayInfo info, string position, double timeoutSeconds,
                           double opacity, double scale, Screen? screen = null)
    {
        double s = OverlayLayout.ClampScale(scale);
        Width = OverlayWidth * s;
        Height = OverlayHeight * s;
        _baseOpacity = OverlayLayout.ClampOpacity(opacity);

        _label.Text = info.Label;

        if (info.MuteToggle)
        {
            // Mute mode — dedicated layout: speaker glyph + Muted/Unmuted, no bar.
            _value.IsVisible = false;
            _track.IsVisible = false;
            _muteRow.IsVisible = true;
            _mutedMark.IsVisible = info.Muted;
            _unmutedMark.IsVisible = !info.Muted;
            _muteStatus.Text = info.Muted ? "Muted" : "Unmuted";
        }
        else
        {
            // Volume mode — channel name, proportional bar, and percentage.
            _value.IsVisible = true;
            _track.IsVisible = true;
            _muteRow.IsVisible = false;

            _value.Text = info.Muted ? "Muted" : $"{info.VolumePercent}%";
            _fill.Background = info.Muted ? MutedBrush : FillBrush;

            // Proportional fill width within the track's inner width.
            double trackWidth = OverlayWidth - 36; // panel padding (18 each side)
            _fill.Width = Math.Max(0, trackWidth * Math.Clamp(info.VolumePercent, 0, 100) / 100.0);
            _fill.Height = 10;
        }

        // Cancel any in-flight fade-out and restore the target opacity for this update.
        _fadeTimer.Stop();
        Opacity = _baseOpacity;

        if (!IsVisible) Show();
        PositionOnScreen(position, screen);

        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 0.5, 10));
        _hideTimer.Start();
    }

    /// <summary>Immediately hides the overlay (no fade) and cancels its timers. Used to
    /// retire a per-monitor overlay when the screen set shrinks or all-screens is off.</summary>
    public void HideNow()
    {
        _hideTimer.Stop();
        _fadeTimer.Stop();
        if (IsVisible) Hide();
        Opacity = _baseOpacity;
    }

    private void PositionOnScreen(string position, Screen? screen)
    {
        Screen? target = screen ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (target is null) return;

        PixelRect area = target.WorkingArea;     // physical pixels
        double dpi = target.Scaling <= 0 ? 1.0 : target.Scaling;
        // Width/Height are the scaled DIP client size; convert to physical pixels.
        int w = (int)(Width * dpi);
        int h = (int)(Height * dpi);
        int margin = (int)(ScreenMarginDip * dpi);

        (int x, int y) = OverlayLayout.Position(area.X, area.Y, area.Width, area.Height, w, h, margin, position);
        Position = new PixelPoint(x, y);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App;

public partial class App : Application
{
    /// <summary>
    /// Root DI container. Composed in <see cref="OnFrameworkInitializationCompleted"/>
    /// and exposed so code-behind (the faithful Phase 1a port) can resolve Core
    /// services without a service-locator scattered through the UI.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = default!;

    // The single canonical main window. Held so tray "Show" reveals this exact
    // instance (rather than constructing a new one) and "Exit" can bypass the
    // minimise-to-tray close guard.
    private MainWindow? _mainWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Services = BuildServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The tray icon keeps the process alive when the window is hidden, so we
            // drive shutdown explicitly (tray "Exit" or a close while minimise-to-tray
            // is off). MainWindow.OnClosing decides hide-to-tray vs. real exit.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var settingsService = Services.GetRequiredService<Services.SettingsService>();
            var log = Services.GetRequiredService<Services.LogService>();
            DashboardSettings settings = settingsService.Settings;

            _mainWindow = Services.GetRequiredService<MainWindow>();

            // Keep the HKCU Run entry in sync with the saved preference each launch
            // (covers the exe being moved/reinstalled). Windows-only; no-op elsewhere.
            Platform.WindowsGlue.ApplyRunOnStartup(settings.StartWithWindows, log.Log);

            // Start hidden in the tray when requested; otherwise show the window.
            // Leaving desktop.MainWindow unset keeps the framework from auto-showing
            // it — the tray icon holds the app open and "Show Dashboard" reveals it.
            if (settings.StartMinimizedToTray && settings.MinimizeToTray)
                log.Log("Started minimized to tray.");
            else
                desktop.MainWindow = _mainWindow;

            // Activate the channel runtime and device-state push first so both are
            // subscribed to connection/device events before the connection starts
            // producing them, then auto-connect to the remembered controller.
            Services.GetRequiredService<Services.ChannelRuntime>();
            Services.GetRequiredService<Services.DeviceStateService>();
            Services.GetRequiredService<Services.SerialConnectionService>().AutoConnect();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Wires the platform-agnostic Core services into the container. Windows-only
    /// audio backends (WASAPI/VoiceMeeter) join later behind a neutral
    /// <c>IAudioBackend</c> registered per-OS (the deferred Phase 0.4 work).
    /// </summary>
    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // Core serial layer — single shared connection for the app lifetime.
        services.AddSingleton<global::PcVolumeControllerDashboard.Core.SerialService>();

        // Diagnostics log (file in the per-OS config dir's logs folder).
        services.AddSingleton<Services.LogService>();

        // Serial connection lifecycle (open + identity handshake + device events).
        services.AddSingleton<Services.SerialConnectionService>();

        // Channel runtime: maps device events (encoder/button) to audio operations.
        services.AddSingleton<Services.ChannelRuntime>();

        // Device state push: PC → ESP32 STATE/CHSTATE + OLED config so the
        // physical OLEDs/display reflect live audio.
        services.AddSingleton<Services.DeviceStateService>();

        // Settings: loaded once at startup, shared, persisted on change.
        services.AddSingleton<Services.SettingsService>(_ =>
        {
            var settings = new Services.SettingsService();
            settings.Load();
            return settings;
        });

        // Audio backend, selected per-OS (WASAPI/VoiceMeeter on Windows;
        // NullAudioBackend elsewhere until Linux/macOS layers land). Initialised
        // once for the app lifetime.
        services.AddSingleton<global::PcVolumeControllerDashboard.Core.Audio.IAudioBackend>(sp =>
        {
            var settings = sp.GetRequiredService<Services.SettingsService>();
            var log = sp.GetRequiredService<Services.LogService>();
            // Wrap in a switchable backend so WASAPI ↔ VoiceMeeter can change at
            // runtime without rebuilding the DI graph.
            var backend = new Audio.SwitchableAudioBackend(
                mode => Audio.AudioBackendFactory.Create(mode, log.Log),
                settings.Settings.AudioBackendMode,
                log.Log);
            backend.Initialise();
            return backend;
        });

        // The shell. Transient so a future "reopen window" can rebuild it.
        services.AddTransient<MainWindow>();

        return services.BuildServiceProvider();
    }

    // --- Tray icon handlers -------------------------------------------------

    private void TrayIcon_OnClicked(object? sender, System.EventArgs e) => ShowMainWindow();

    private void TrayShow_OnClick(object? sender, System.EventArgs e) => ShowMainWindow();

    private void TrayExit_OnClick(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        // Bypass the minimise-to-tray close guard so the app actually exits.
        _mainWindow?.AllowClose();
        desktop.Shutdown();
    }

    private void ShowMainWindow()
    {
        _mainWindow ??= Services.GetRequiredService<MainWindow>();
        _mainWindow.Show();
        _mainWindow.ShowInTaskbar = true;
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }
}

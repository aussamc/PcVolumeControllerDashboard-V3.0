using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace PcVolumeControllerDashboard.App;

public partial class App : Application
{
    /// <summary>
    /// Root DI container. Composed in <see cref="OnFrameworkInitializationCompleted"/>
    /// and exposed so code-behind (the faithful Phase 1a port) can resolve Core
    /// services without a service-locator scattered through the UI.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = default!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Services = BuildServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Keep the process alive when the main window is closed/hidden to the
            // tray; we exit explicitly from the tray "Exit" item. Minimise-to-tray
            // behaviour is wired up properly when the window chrome is ported.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = Services.GetRequiredService<MainWindow>();
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

        // Settings: loaded once at startup, shared, persisted on change.
        services.AddSingleton<Services.SettingsService>(_ =>
        {
            var settings = new Services.SettingsService();
            settings.Load();
            return settings;
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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var window = desktop.MainWindow ??= Services.GetRequiredService<MainWindow>();
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}

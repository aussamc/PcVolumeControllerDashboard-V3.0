using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PcVolumeControllerDashboard.App.Services;
using PcVolumeControllerDashboard.Core;
using PcVolumeControllerDashboard.Core.Audio;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// First-run setup wizard — a separate top-level window (not a tab) shown once on a
/// brand-new install (no settings file yet), and re-launchable later.
///
/// v3.18 overhaul: after Welcome the user picks a <see cref="WizardStream"/> — <b>Quick</b>
/// (connect → check displays → assign → a couple of preferences) or <b>Advanced</b> (adds
/// audio-backend, theme, and update-preference pages). The step sequence is built
/// dynamically per stream: the fixed panels (Welcome / stream chooser / Connect / Identify
/// / Assign / Done) are declared inline in XAML; the stream-specific pages are
/// self-contained <see cref="IWizardPage"/> UserControls inserted into the sequence here.
///
/// It drives the same shared runtime services as the dashboard (serial connection, audio
/// backend), so anything set up here is live immediately. On finish (or skip) it marks the
/// wizard complete and raises <see cref="Completed"/> so the app can open the dashboard.
/// </summary>
public partial class FirstRunWizard : Window
{
    private const int ChannelCount = 6;

    /// <summary>Which page set the user is walking through.</summary>
    private enum WizardStream { Quick, Advanced }

    /// <summary>One entry in the (stream-dependent) step sequence.</summary>
    private sealed class WizardStep
    {
        public required string Title { get; init; }
        public required Control Content { get; init; }
        /// <summary>Runs when the step becomes active (populate/refresh live data).</summary>
        public Action? OnShow { get; init; }
        /// <summary>Runs when navigating forward off the step (persist).</summary>
        public Action? OnLeave { get; init; }
    }

    private readonly SettingsService? _settings;
    private readonly IAudioBackend? _audio;
    private readonly SerialConnectionService? _connection;

    private readonly ComboBox[] _assignCombos = new ComboBox[ChannelCount];

    // Stream-specific pages, built once with the shared services. Both AppSetup variants
    // exist (Quick shows the condensed 2-toggle page; Advanced the full checkbox set); the
    // unused ones simply never appear in the active sequence.
    private AppSetupWizardPage? _appSetupCondensed;
    private AppSetupWizardPage? _appSetupFull;
    private ThemeWizardPage? _themePage;
    private AudioBackendWizardPage? _audioBackendPage;
    private UpdatePrefsWizardPage? _updatePage;

    private List<WizardStep> _steps = new();
    private WizardStream _stream = WizardStream.Quick;
    private int _step;
    private bool _completed;

    /// <summary>Raised once when the user finishes or skips the wizard.</summary>
    public event Action? Completed;

    // Parameterless ctor for the XAML runtime loader / designer.
    public FirstRunWizard()
    {
        InitializeComponent();
    }

    public FirstRunWizard(SettingsService settings, IAudioBackend audio, SerialConnectionService connection) : this()
    {
        _settings = settings;
        _audio = audio;
        _connection = connection;

        _connection.StateChanged += OnConnectionStateChanged;

        BuildStreamPages();

        _step = 0;
        RebuildSteps();
        UpdateStepUi();
    }

    // ── Stream page construction ──────────────────────────────────────────────

    /// <summary>
    /// Instantiates the stream-specific UserControl pages once and parks them (hidden) in
    /// the step host panel so they can be shown/hidden alongside the inline panels.
    /// </summary>
    private void BuildStreamPages()
    {
        if (_settings == null) return;

        var updater = App.Services.GetService<UpdateCheckService>();

        _appSetupCondensed = new AppSetupWizardPage(_settings, condensed: true);
        _appSetupFull = new AppSetupWizardPage(_settings, condensed: false);
        _themePage = new ThemeWizardPage(_settings);
        if (_audio != null)
            _audioBackendPage = new AudioBackendWizardPage(_settings, _audio);
        if (updater != null)
            _updatePage = new UpdatePrefsWizardPage(_settings, updater, AppInfo.Version);

        foreach (Control page in new Control?[]
                 {
                     _appSetupCondensed, _appSetupFull, _themePage, _audioBackendPage, _updatePage,
                 }.OfType<Control>())
        {
            page.IsVisible = false;
            StepHostPanel.Children.Add(page);
        }
    }

    /// <summary>
    /// Builds the ordered step sequence for the current <see cref="_stream"/>. The first
    /// two steps (Welcome, stream chooser) are identical in both streams so the current
    /// <see cref="_step"/> index stays valid across a rebuild triggered from the chooser.
    /// </summary>
    private void RebuildSteps()
    {
        var steps = new List<WizardStep>
        {
            Step("Welcome", WelcomePanel),
            Step("Choose your setup", StreamChooserPanel),
            Step("Connect your controller", ConnectPanel, onShow: UpdateConnectionUi),
            Step("Check the displays", IdentifyPanel, onShow: UpdateConnectionUi),
        };

        // Advanced surfaces the audio backend before the channel assignment.
        if (_stream == WizardStream.Advanced && _audioBackendPage != null)
            steps.Add(PageStep(_audioBackendPage));

        steps.Add(Step("Assign your channels", AssignPanel,
            onShow: PopulateAssignCombos, onLeave: ApplyAssignments));

        if (_stream == WizardStream.Advanced)
        {
            if (_appSetupFull != null) steps.Add(PageStep(_appSetupFull));
            if (_themePage != null) steps.Add(PageStep(_themePage));
            // Encoder-Feel "try it" page (item 13) is deferred to a follow-up.
            if (_updatePage != null) steps.Add(PageStep(_updatePage));
        }
        else if (_appSetupCondensed != null)
        {
            steps.Add(PageStep(_appSetupCondensed));
        }

        steps.Add(Step("All set", DonePanel, onShow: UpdateSummary));

        _steps = steps;
    }

    private WizardStep Step(string title, Control content, Action? onShow = null, Action? onLeave = null)
        => new() { Title = title, Content = content, OnShow = onShow, OnLeave = onLeave };

    private static WizardStep PageStep(IWizardPage page)
        => new() { Title = page.Title, Content = (Control)page, OnShow = page.OnShow, OnLeave = page.OnLeave };

    // ── Connection status (live) ──────────────────────────────────────────────

    private void OnConnectionStateChanged(SerialConnectionState _) =>
        Dispatcher.UIThread.Post(UpdateConnectionUi);

    private void UpdateConnectionUi()
    {
        SerialConnectionState state = _connection?.State ?? SerialConnectionState.Disconnected;
        bool connected = state == SerialConnectionState.Connected;

        WizardConnStatusText.Text = state switch
        {
            SerialConnectionState.Connected =>
                $"Connected — protocol {_connection!.Protocol}, chip {(_connection.ConnectedChipId is { Length: > 0 } c ? c : "(none)")}",
            SerialConnectionState.Identifying => "Identifying controller…",
            SerialConnectionState.Incompatible =>
                "Controller found, but its firmware is too old — update it to continue.",
            _ => "Searching for controller…",
        };

        // The identify step needs a live link; gate its button and swap the hint.
        WizardIdentifyButton.IsEnabled = connected;
        WizardIdentifyHintText.Text = connected
            ? "Note the physical position of each numbered display — that's the order the next step maps to apps."
            : "Connect the controller first (previous step) to test its displays.";
    }

    // ── Step navigation ───────────────────────────────────────────────────────

    private void UpdateStepUi()
    {
        WizardStep current = _steps[_step];

        StepIndicatorText.Text = $"Step {_step + 1} of {_steps.Count}";
        StepTitleText.Text = current.Title;

        // Only the active step's content is visible; hide every other hosted panel/page.
        foreach (Control child in StepHostPanel.Children.OfType<Control>())
            child.IsVisible = ReferenceEquals(child, current.Content);

        WizardBackButton.IsVisible = _step > 0;
        WizardSkipButton.IsVisible = _step != _steps.Count - 1;
        WizardNextButton.Content = _step == _steps.Count - 1 ? "Finish" : "Next";

        current.OnShow?.Invoke();
    }

    private void Next_Click(object? sender, RoutedEventArgs e)
    {
        bool leavingStreamChooser = ReferenceEquals(_steps[_step].Content, StreamChooserPanel);

        // Persist anything the current step batches before moving on.
        _steps[_step].OnLeave?.Invoke();

        if (leavingStreamChooser)
        {
            // Commit the stream choice and rebuild the sequence. Welcome + chooser occupy
            // the same indices in both streams, so _step stays valid before we advance.
            _stream = AdvancedStreamRadio.IsChecked == true ? WizardStream.Advanced : WizardStream.Quick;
            RebuildSteps();
        }

        if (_step == _steps.Count - 1)
        {
            Complete();
            return;
        }

        _step++;
        UpdateStepUi();
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        if (_step == 0) return;
        _step--;
        UpdateStepUi();
    }

    private void Skip_Click(object? sender, RoutedEventArgs e) => Complete();

    private void Complete()
    {
        // OnClosed performs the actual completion bookkeeping (marking the flag and
        // raising Completed), so closing by button or by the window's X behave the
        // same and the app is never left with only a tray icon.
        Close();
    }

    // ── Connect step ──────────────────────────────────────────────────────────

    private void Scan_Click(object? sender, RoutedEventArgs e) => _connection?.Reconnect();

    // ── Identify step ─────────────────────────────────────────────────────────

    private void Identify_Click(object? sender, RoutedEventArgs e)
    {
        // Ask every OLED to briefly show its channel number. The firmware draws the
        // identify screen and holds it; normal channel state resumes once the
        // dashboard's poll starts pushing CHSTATE after setup completes.
        _connection?.SendLine(ProtocolCommands.ShowIdent, log: true);
    }

    // ── Assign step ───────────────────────────────────────────────────────────

    private void RefreshApps_Click(object? sender, RoutedEventArgs e)
    {
        _audio?.InvalidateCache();
        PopulateAssignCombos();
    }

    /// <summary>
    /// Builds one labelled combo per channel (once) and fills each with the current
    /// audio targets plus an "(Unassigned)" option, preselecting the channel's saved
    /// target. An assigned-but-offline app is kept as its own entry so the mapping
    /// isn't silently dropped just because the app isn't playing right now.
    /// </summary>
    private void PopulateAssignCombos()
    {
        if (_settings == null) return;

        if (AssignRowsPanel.Children.Count == 0)
            BuildAssignRows();

        IReadOnlyList<AudioTarget> enumerated =
            _audio?.GetAvailableTargets() ?? (IReadOnlyList<AudioTarget>)Array.Empty<AudioTarget>();
        WizardAssignHintText.IsVisible = enumerated.Count == 0;

        ChannelSettings[] channels = _settings.Settings.Channels;
        for (int i = 0; i < ChannelCount && i < channels.Length; i++)
        {
            ChannelSettings ch = channels[i];
            var list = new List<AudioTarget> { Unassigned() };
            list.AddRange(enumerated);

            AudioTarget selected;
            string key = ch.TargetKey ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                selected = list[0]; // Unassigned
            }
            else
            {
                AudioTarget? match = list.FirstOrDefault(t => KeyEquals(t.Key, key));
                if (match == null)
                {
                    // Assigned app not currently listed (offline) — keep it selectable.
                    match = new AudioTarget
                    {
                        Key = key,
                        Label = string.IsNullOrWhiteSpace(ch.FriendlyName) ? key : ch.FriendlyName,
                    };
                    list.Add(match);
                }
                selected = match;
            }

            _assignCombos[i].ItemsSource = list;
            _assignCombos[i].SelectedItem = selected;
        }
    }

    private void BuildAssignRows()
    {
        AssignRowsPanel.Children.Clear();
        for (int i = 0; i < ChannelCount; i++)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*") };

            var label = new TextBlock
            {
                Text = $"Channel {i + 1}",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 0);

            var combo = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            Grid.SetColumn(combo, 1);

            grid.Children.Add(label);
            grid.Children.Add(combo);
            AssignRowsPanel.Children.Add(grid);
            _assignCombos[i] = combo;
        }
    }

    /// <summary>Writes the selected target for each channel back to settings and saves.</summary>
    private void ApplyAssignments()
    {
        if (_settings == null) return;

        ChannelSettings[] channels = _settings.Settings.Channels;
        for (int i = 0; i < ChannelCount && i < channels.Length; i++)
        {
            if (_assignCombos[i]?.SelectedItem is not AudioTarget target) continue;

            ChannelSettings ch = channels[i];
            if (string.IsNullOrEmpty(target.Key))
            {
                ch.TargetKey = string.Empty;
                ch.TargetKeys.Clear();
            }
            else
            {
                ch.TargetKey = target.Key;
                ch.TargetKeys.Clear(); // the wizard sets a single authoritative target
                if (string.IsNullOrWhiteSpace(ch.FriendlyName))
                    ch.FriendlyName = string.IsNullOrWhiteSpace(target.Label) ? target.Key : target.Label;
            }
        }

        _settings.Save();
    }

    // ── Done step ─────────────────────────────────────────────────────────────

    private void UpdateSummary()
    {
        if (_settings == null) return;

        ChannelSettings[] channels = _settings.Settings.Channels;
        int assigned = channels.Take(ChannelCount).Count(ch => !string.IsNullOrWhiteSpace(ch.TargetKey));
        WizardSummaryText.Text = assigned == 0
            ? "No channels are mapped yet — you can assign them any time from the Audio tab."
            : $"You've mapped {assigned} of {ChannelCount} channels.";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AudioTarget Unassigned() => new() { Key = string.Empty, Label = "(Unassigned)" };

    private static bool KeyEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    protected override void OnClosed(EventArgs e)
    {
        if (_connection != null)
            _connection.StateChanged -= OnConnectionStateChanged;

        // Closing the wizard by any route (Finish, Skip, or the window's X) counts as
        // finishing setup: persist the flag and let the app open the dashboard so the
        // user is never stranded with only a tray icon. Any assignments were already
        // applied when leaving the assign step.
        if (!_completed)
        {
            _completed = true;
            _settings?.MarkFirstRunComplete();
            Completed?.Invoke();
        }

        base.OnClosed(e);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PcVolumeControllerDashboard.App.Services;
using PcVolumeControllerDashboard.Core;
using PcVolumeControllerDashboard.Core.Audio;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// First-run setup wizard — a separate top-level window (not a tab) shown once on a
/// brand-new install (no settings file yet), and re-launchable later. Walks the user
/// through: welcome → connect/pair the controller → check the OLED displays → map
/// each knob to an app/device → done.
///
/// It drives the same shared runtime services as the dashboard (the serial
/// connection, the audio backend), so anything set up here is live immediately. On
/// finish (or skip) it marks the wizard complete and raises <see cref="Completed"/>
/// so the app can open the main dashboard window.
/// </summary>
public partial class FirstRunWizard : Window
{
    private const int StepWelcome = 0;
    private const int StepConnect = 1;
    private const int StepIdentify = 2;
    private const int StepAssign = 3;
    private const int StepDone = 4;
    private const int LastStep = StepDone;
    private const int ChannelCount = 6;

    private static readonly string[] StepTitles =
    {
        "Welcome",
        "Connect your controller",
        "Check the displays",
        "Assign your channels",
        "All set",
    };

    private readonly SettingsService? _settings;
    private readonly IAudioBackend? _audio;
    private readonly SerialConnectionService? _connection;

    private readonly StackPanel[] _panels;
    private readonly ComboBox[] _assignCombos = new ComboBox[ChannelCount];

    private int _step;
    private bool _completed;

    /// <summary>Raised once when the user finishes or skips the wizard.</summary>
    public event Action? Completed;

    // Parameterless ctor for the XAML runtime loader / designer.
    public FirstRunWizard()
    {
        InitializeComponent();
        _panels = new[] { WelcomePanel, ConnectPanel, IdentifyPanel, AssignPanel, DonePanel };
    }

    public FirstRunWizard(SettingsService settings, IAudioBackend audio, SerialConnectionService connection) : this()
    {
        _settings = settings;
        _audio = audio;
        _connection = connection;

        _connection.StateChanged += OnConnectionStateChanged;

        _step = StepWelcome;
        UpdateStepUi();
    }

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
        StepIndicatorText.Text = $"Step {_step + 1} of {LastStep + 1}";
        StepTitleText.Text = StepTitles[_step];

        for (int i = 0; i < _panels.Length; i++)
            _panels[i].IsVisible = i == _step;

        WizardBackButton.IsVisible = _step > 0;
        WizardSkipButton.IsVisible = _step != LastStep;
        WizardNextButton.Content = _step == LastStep ? "Finish" : "Next";

        switch (_step)
        {
            case StepConnect:
            case StepIdentify:
                UpdateConnectionUi();
                break;
            case StepAssign:
                PopulateAssignCombos();
                break;
            case StepDone:
                UpdateSummary();
                break;
        }
    }

    private void Next_Click(object? sender, RoutedEventArgs e)
    {
        // Persist the channel mapping as we leave the assign step.
        if (_step == StepAssign)
            ApplyAssignments();

        if (_step == LastStep)
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

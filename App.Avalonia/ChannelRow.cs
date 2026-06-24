using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Row view-model for the Audio tab's channel-mapping grid. The mutable fields
/// are refreshed by the Audio tab's state poll, so they raise change
/// notifications for the DataGrid to pick up live updates.
/// </summary>
public sealed class ChannelRow : INotifyPropertyChanged
{
    /// <summary>1-based channel number shown to the user (channels are 0-based internally).</summary>
    public int Channel { get; init; }

    private string _displayName = string.Empty;
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }

    private string _assignedLabel = "Unassigned";
    public string AssignedLabel { get => _assignedLabel; set => Set(ref _assignedLabel, value); }

    private string _volumeDisplay = "—";
    public string VolumeDisplay { get => _volumeDisplay; set => Set(ref _volumeDisplay, value); }

    private string _muteDisplay = "—";
    public string MuteDisplay { get => _muteDisplay; set => Set(ref _muteDisplay, value); }

    private string _status = string.Empty;
    public string Status { get => _status; set => Set(ref _status, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

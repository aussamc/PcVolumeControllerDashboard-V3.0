using System.IO;
using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>Unit tests for the arbitrary-path settings import/export in Core.</summary>
public sealed class SettingsImportExportTests
{
    private const int ChannelCount = 6;
    private const int MaxSensitivity = 500;

    private static string NewTempFile() =>
        Path.Combine(Path.GetTempPath(), "pcvc-set-" + Path.GetRandomFileName() + ".json");

    [Fact]
    public void ExportThenImport_RoundTripsKeyValues()
    {
        var settings = DashboardSettings.CreateDefault();
        settings.EncoderSensitivityPercent = 123;
        settings.OledBrightnessPercent = 42;
        settings.Channels[0].FriendlyName = "RoundTrip";

        string path = NewTempFile();
        try
        {
            SettingsRepository.ExportTo(settings, path);
            File.Exists(path).Should().BeTrue();

            DashboardSettings? imported = SettingsRepository.ImportFrom(path, ChannelCount, MaxSensitivity);

            imported.Should().NotBeNull();
            imported!.EncoderSensitivityPercent.Should().Be(123);
            imported.OledBrightnessPercent.Should().Be(42);
            imported.Channels[0].FriendlyName.Should().Be("RoundTrip");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ImportFrom_MissingFile_ReturnsNull()
    {
        SettingsRepository.ImportFrom(NewTempFile(), ChannelCount, MaxSensitivity).Should().BeNull();
    }

    [Fact]
    public void ImportFrom_GarbageFile_ReturnsNull()
    {
        string path = NewTempFile();
        try
        {
            File.WriteAllText(path, "this is not json {{{");
            SettingsRepository.ImportFrom(path, ChannelCount, MaxSensitivity).Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ImportFrom_NormalisesOutOfRangeValues()
    {
        // A hand-edited file with an absurd sensitivity should be clamped by Normalize.
        string path = NewTempFile();
        try
        {
            File.WriteAllText(path, "{\"EncoderSensitivityPercent\": 99999}");
            DashboardSettings? imported = SettingsRepository.ImportFrom(path, ChannelCount, MaxSensitivity);
            imported.Should().NotBeNull();
            imported!.EncoderSensitivityPercent.Should().BeLessThanOrEqualTo(MaxSensitivity);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>Unit tests for the Core diagnostics-zip builder.</summary>
public sealed class DiagnosticsExporterTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pcvc-diag-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Create_IncludesSettingsLogsAndInfo()
    {
        string root = NewTempDir();
        try
        {
            string settings = Path.Combine(root, "settings.json");
            File.WriteAllText(settings, "{\"x\":1}");

            string logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "a.log"), "line a");
            File.WriteAllText(Path.Combine(logs, "b.log"), "line b");

            string zipPath = Path.Combine(root, "out", "diag.zip");
            DiagnosticsExporter.Create(zipPath, settings, logs, "hello info");

            File.Exists(zipPath).Should().BeTrue();
            using ZipArchive zip = ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Select(e => e.FullName).ToList();

            names.Should().Contain("info.txt");
            names.Should().Contain("settings.json");
            names.Should().Contain("logs/a.log");
            names.Should().Contain("logs/b.log");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Create_WithMissingSourcesStillWritesInfo()
    {
        string root = NewTempDir();
        try
        {
            string zipPath = Path.Combine(root, "diag.zip");
            DiagnosticsExporter.Create(zipPath, settingsPath: null, logsDirectory: Path.Combine(root, "nope"), "info only");

            using ZipArchive zip = ZipFile.OpenRead(zipPath);
            zip.Entries.Select(e => e.FullName).Should().ContainSingle().Which.Should().Be("info.txt");

            using var reader = new StreamReader(zip.GetEntry("info.txt")!.Open());
            reader.ReadToEnd().Should().Be("info only");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Create_OverwritesExistingZip()
    {
        string root = NewTempDir();
        try
        {
            string zipPath = Path.Combine(root, "diag.zip");
            File.WriteAllText(zipPath, "not a zip");

            DiagnosticsExporter.Create(zipPath, null, null, "fresh");

            using ZipArchive zip = ZipFile.OpenRead(zipPath); // throws if not a valid zip
            zip.Entries.Should().NotBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for <see cref="SerialService"/> lifecycle behaviour.
/// These tests do not require a real COM port — they verify that the service
/// starts disconnected, handles double-close gracefully, and exposes port names.
/// </summary>
public sealed class SerialServiceTests
{
    // ── Initial state ─────────────────────────────────────────────────────────────

    [Fact]
    public void NewService_IsNotConnected()
    {
        using var svc = new SerialService();
        svc.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void NewService_PortNameIsNull()
    {
        using var svc = new SerialService();
        svc.PortName.Should().BeNull();
    }

    // ── Close ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Close_WhenAlreadyClosed_DoesNotThrow()
    {
        using var svc = new SerialService();
        Action act = () => svc.Close();
        act.Should().NotThrow("closing a service that was never opened must be a no-op");
    }

    [Fact]
    public void Close_CalledTwice_DoesNotThrow()
    {
        using var svc = new SerialService();
        svc.Close();
        Action act = () => svc.Close();
        act.Should().NotThrow("double-close must be safe");
    }

    [Fact]
    public void Dispose_WhenNotConnected_DoesNotThrow()
    {
        Action act = () =>
        {
            using var svc = new SerialService();
            // Dispose called by 'using' — nothing to do
        };
        act.Should().NotThrow();
    }

    // ── SendLine ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SendLine_WhenNotConnected_DoesNotThrowAndDoesNotFireErrorEvent()
    {
        using var svc = new SerialService();
        string? errorMessage = null;
        svc.ErrorOccurred += msg => errorMessage = msg;

        Action act = () => svc.SendLine("PING");

        act.Should().NotThrow("SendLine is a no-op when the port is not open");
        errorMessage.Should().BeNull("no write was attempted, so no error should fire");
    }

    // ── GetPortNames ─────────────────────────────────────────────────────────────

    [Fact]
    public void GetPortNames_ReturnsNonNullArray()
    {
        // May be empty on a machine with no COM ports — that is acceptable.
        string[] ports = SerialService.GetPortNames();
        ports.Should().NotBeNull();
    }

    [Fact]
    public void GetPortNames_DoesNotThrow()
    {
        Action act = () => SerialService.GetPortNames();
        act.Should().NotThrow();
    }

    // ── Open with non-existent port ───────────────────────────────────────────────

    [Fact]
    public void Open_WithNonExistentPort_Throws()
    {
        using var svc = new SerialService();

        // Open throws on an invalid port name — the caller is expected to handle this.
        Action act = () => svc.Open("COM999", 115200);
        act.Should().Throw<Exception>("opening a port that doesn't exist must propagate the error");
    }

    [Fact]
    public void Open_AfterFailedAttempt_ServiceRemainsNotConnected()
    {
        using var svc = new SerialService();

        try { svc.Open("COM999", 115200); } catch { /* expected */ }

        svc.IsConnected.Should().BeFalse();
    }
}

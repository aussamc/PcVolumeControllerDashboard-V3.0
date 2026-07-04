using FluentAssertions;
using Xunit;

namespace PcVolumeControllerDashboard.Tests;

/// <summary>
/// Unit tests for the cross-platform <see cref="HotkeyDisplay"/> combo formatter
/// (the Avalonia replacement for the WPF host's WinForms-Keys ToDisplayString).
/// </summary>
public sealed class HotkeyDisplayTests
{
    // Modifier flags: ALT=1, CTRL=2, SHIFT=4, WIN=8.

    [Theory]
    [InlineData(0, 0x41, "A")]                       // no modifiers
    [InlineData(2, 0x41, "Ctrl+A")]                  // Ctrl+A
    [InlineData(2 | 4, 0x4B, "Ctrl+Shift+K")]        // Ctrl+Shift+K
    [InlineData(1, 0x77, "Alt+F8")]                  // Alt+F8
    [InlineData(8, 0x20, "Win+Space")]               // Win+Space
    [InlineData(1 | 2 | 4 | 8, 0x31, "Win+Ctrl+Shift+Alt+1")] // full stack, stable order
    public void Format_ProducesExpectedString(int modifiers, int vk, string expected)
    {
        HotkeyDisplay.Format(modifiers, vk).Should().Be(expected);
    }

    [Fact]
    public void Format_ZeroVirtualKey_IsUnassigned()
    {
        HotkeyDisplay.Format(2, 0).Should().Be("(unassigned)");
    }

    [Theory]
    [InlineData(0x41, "A")]
    [InlineData(0x5A, "Z")]
    [InlineData(0x30, "0")]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    [InlineData(0x25, "Left")]
    [InlineData(0x1B, "Esc")]
    [InlineData(0x60, "Num0")]
    public void KeyName_MapsCommonKeys(int vk, string expected)
    {
        HotkeyDisplay.KeyName(vk).Should().Be(expected);
    }

    [Fact]
    public void KeyName_UnknownKey_FallsBackToHex()
    {
        HotkeyDisplay.KeyName(0x07).Should().Be("Key(0x07)");
    }

    [Fact]
    public void Describe_UnassignedBinding_IsUnassigned()
    {
        var binding = new HotkeyBinding { Enabled = false, VirtualKey = 0x41 };
        HotkeyDisplay.Describe(binding).Should().Be("(unassigned)");
    }

    [Fact]
    public void Describe_AssignedBinding_FormatsCombo()
    {
        var binding = new HotkeyBinding { Enabled = true, Modifiers = 2, VirtualKey = 0x4D };
        HotkeyDisplay.Describe(binding).Should().Be("Ctrl+M");
    }
}

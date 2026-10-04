using WindowAgentMcp.Input;
using Xunit;

namespace WindowAgentMcp.Tests.Input;

public sealed class InputKeyCatalogTests
{
    [Theory]
    [InlineData("a", 0x41)]
    [InlineData("  arrow left  ", 0x25)]
    [InlineData("f24", 0x87)]
    [InlineData("numpad7", 0x67)]
    [InlineData("return", 0x0D)]
    public void TryParseKeyAcceptsNamedAliases(string name, ushort expectedVirtualKey)
    {
        var found = InputKeyCatalog.TryParseKey(name, out var virtualKey);

        Assert.True(found);
        Assert.Equal(expectedVirtualKey, virtualKey);
    }

    [Theory]
    [InlineData("CTRL", 0x11)]
    [InlineData("control", 0x11)]
    [InlineData("shift", 0x10)]
    [InlineData("ALT", 0x12)]
    public void TryParseModifierAcceptsOnlySupportedModifiers(string name, ushort expectedVirtualKey)
    {
        var found = InputKeyCatalog.TryParseModifier(name, out var virtualKey);

        Assert.True(found);
        Assert.Equal(expectedVirtualKey, virtualKey);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("LWIN")]
    [InlineData("WINDOWS")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseModifierRejectsNonModifierAndSystemKeys(string? name)
    {
        var found = InputKeyCatalog.TryParseModifier(name, out var virtualKey);

        Assert.False(found);
        Assert.Equal((ushort)0, virtualKey);
    }

    [Theory]
    [InlineData("LWIN")]
    [InlineData("RWIN")]
    [InlineData("WINDOWS")]
    [InlineData("UNKNOWN")]
    public void TryParseKeyRejectsUnsupportedSystemAndUnknownKeys(string name)
    {
        var found = InputKeyCatalog.TryParseKey(name, out var virtualKey);

        Assert.False(found);
        Assert.Equal((ushort)0, virtualKey);
    }
}

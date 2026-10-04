using WindowAgentMcp.Input;
using Xunit;

namespace WindowAgentMcp.Tests.Input;

public sealed class HeldInputRegistryTests
{
    [Fact]
    public void TakeExpiredReleasesKeysAndButtonsAtTimeoutBoundary()
    {
        var registry = new HeldInputRegistry();
        var key = new KeyIdentity(0x41, 0, false);
        registry.PressKey(key);
        registry.PressButton(MouseButton.Right);

        var beforeDeadline = registry.TakeExpired(DateTimeOffset.UtcNow + HeldInputRegistry.HoldTimeout - TimeSpan.FromMilliseconds(1));
        Assert.Empty(beforeDeadline.Keys);
        Assert.Empty(beforeDeadline.MouseButtons);
        Assert.True(registry.ContainsKey(key));
        Assert.True(registry.ContainsButton(MouseButton.Right));

        var expired = registry.TakeExpired(DateTimeOffset.UtcNow + HeldInputRegistry.HoldTimeout + TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { key }, expired.Keys);
        Assert.Equal(new[] { MouseButton.Right }, expired.MouseButtons);
        Assert.False(registry.ContainsKey(key));
        Assert.False(registry.ContainsButton(MouseButton.Right));
    }

    [Fact]
    public void ExplicitReleaseRemovesHeldKeyAndButton()
    {
        var registry = new HeldInputRegistry();
        var key = new KeyIdentity(0x11, 0, false);
        registry.PressKey(key);
        registry.PressButton(MouseButton.Left);

        registry.ReleaseKey(key);
        registry.ReleaseButton(MouseButton.Left);

        Assert.False(registry.ContainsKey(key));
        Assert.False(registry.ContainsButton(MouseButton.Left));
    }

    [Fact]
    public void TakeAllReturnsAndClearsHeldState()
    {
        var registry = new HeldInputRegistry();
        var key = new KeyIdentity(0x12, 0, false);
        registry.PressKey(key);
        registry.PressButton(MouseButton.Left);

        var snapshot = registry.TakeAll();

        Assert.Equal(new[] { key }, snapshot.Keys);
        Assert.Equal(new[] { MouseButton.Left }, snapshot.MouseButtons);
        Assert.Empty(registry.Snapshot().Keys);
        Assert.Empty(registry.Snapshot().MouseButtons);
    }
}

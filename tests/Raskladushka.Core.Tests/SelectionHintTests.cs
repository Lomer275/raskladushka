using Raskladushka.Core.Typing;

namespace Raskladushka.Core.Tests;

public class SelectionHintTests
{
    private static SelectionHint Create(bool likely = false)
    {
        var hint = new SelectionHint(new(4, 4, 4, 4, 500));
        if (likely) hint.OnKey(new(0x41, 0, false, true), new(false, true, false, false, false));
        return hint;
    }

    [Theory]
    [InlineData(0x10)] [InlineData(0x11)] [InlineData(0x12)]
    [InlineData(0xA0)] [InlineData(0xA1)] [InlineData(0xA2)]
    [InlineData(0xA3)] [InlineData(0xA4)] [InlineData(0xA5)]
    [InlineData(0x5B)] [InlineData(0x5C)]
    [InlineData(0x14)] [InlineData(0x90)] [InlineData(0x91)]
    public void ModifiersAndTogglesPreserveHint(int vk)
    {
        foreach (var initial in new[] { false, true })
        {
            var hint = Create(initial);
            hint.OnKey(new(vk, 0, false, true), default);
            Assert.Equal(initial, hint.Likely);
        }
    }

    [Theory]
    [InlineData(0x21)] [InlineData(0x22)] [InlineData(0x23)] [InlineData(0x24)]
    [InlineData(0x25)] [InlineData(0x26)] [InlineData(0x27)] [InlineData(0x28)]
    public void ShiftMovementSelectsWithOrWithoutControlAndOnRepeat(int vk)
    {
        foreach (var ctrl in new[] { false, true })
        foreach (var repeat in new[] { false, true })
        {
            var hint = Create();
            hint.OnKey(new(vk, 0, false, true, repeat), new(true, ctrl, false, false, false));
            Assert.True(hint.Likely);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ControlASelectsIncludingRepeat(bool repeat)
    {
        var hint = Create();
        hint.OnKey(new(0x41, 0, false, true, repeat), new(false, true, false, false, false));
        Assert.True(hint.Likely);
    }

    [Theory]
    [InlineData(0x43)] [InlineData(0x2D)]
    public void CopyPreservesHint(int vk)
    {
        foreach (var initial in new[] { false, true })
        {
            var hint = Create(initial);
            hint.OnKey(new(vk, 0, false, true), new(false, true, false, false, false));
            Assert.Equal(initial, hint.Likely);
        }
    }

    [Theory]
    [InlineData(0x41, false, false, false, false)]
    [InlineData(0x41, true, true, false, false)]
    [InlineData(0x41, false, true, true, false)]
    [InlineData(0x41, false, true, false, true)]
    [InlineData(0x43, true, true, false, false)]
    [InlineData(0x43, false, true, true, false)]
    [InlineData(0x43, false, true, false, true)]
    [InlineData(0x2D, true, true, false, false)]
    [InlineData(0x2D, false, true, true, false)]
    [InlineData(0x2D, false, true, false, true)]
    [InlineData(0x24, false, false, false, false)]
    [InlineData(0x24, true, false, true, false)]
    [InlineData(0x24, true, false, false, true)]
    [InlineData(0x1B, false, false, false, false)]
    public void OtherKeysClearHint(int vk, bool shift, bool ctrl, bool alt, bool win)
    {
        var hint = Create(true);
        hint.OnKey(new(vk, 0, false, true), new(shift, ctrl, alt, win, false));
        Assert.False(hint.Likely);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void KeyUpDoesNotChangeHint(bool initial)
    {
        var hint = Create(initial);
        hint.OnKey(new(0x41, 0, false, false), new(false, true, false, false, false));
        Assert.Equal(initial, hint.Likely);
    }

    [Fact]
    public void ShiftClickSelects()
    {
        var hint = Create();
        hint.OnMouse(new(MouseButton.Left, true, 10, 10, 100, true));
        hint.OnMouse(new(MouseButton.Left, false, 10, 10, 101, false));
        Assert.True(hint.Likely);
    }

    [Fact]
    public void SingleClickClearsHint()
    {
        var hint = Create(true);
        hint.OnMouse(new(MouseButton.Left, true, 10, 10, 100, false));
        Assert.False(hint.Likely);
    }

    [Theory]
    [InlineData(4, 0, false)] [InlineData(0, -4, false)]
    [InlineData(5, 0, true)] [InlineData(0, -5, true)]
    public void DragUsesStrictThreshold(int dx, int dy, bool expected)
    {
        var hint = Create();
        hint.OnMouse(new(MouseButton.Left, true, 10, 10, 100, false));
        hint.OnMouse(new(MouseButton.Left, false, 10 + dx, 10 + dy, 101, false));
        Assert.Equal(expected, hint.Likely);
    }

    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(500, 2, -2, true)]
    [InlineData(501, 0, 0, false)]
    [InlineData(-1, 0, 0, false)]
    [InlineData(100, 3, 0, false)]
    [InlineData(100, 0, -3, false)]
    public void DoubleClickUsesTimeAndRectangle(long elapsed, int dx, int dy, bool expected)
    {
        var hint = Create();
        hint.OnMouse(new(MouseButton.Left, true, 10, 10, 100, false));
        hint.OnMouse(new(MouseButton.Left, false, 10, 10, 100, false));
        hint.OnMouse(new(MouseButton.Left, true, 10 + dx, 10 + dy, 100 + elapsed, false));
        Assert.Equal(expected, hint.Likely);
    }

    [Fact]
    public void TripleClickUsesMostRecentDown()
    {
        var hint = Create();
        hint.OnMouse(new(MouseButton.Left, true, 10, 10, 100, false));
        hint.OnMouse(new(MouseButton.Left, true, 12, 12, 600, false));
        Assert.True(hint.Likely);
        hint.OnMouse(new(MouseButton.Left, true, 14, 14, 1100, false));
        Assert.True(hint.Likely);
    }

    [Theory]
    [InlineData(MouseButton.Right)] [InlineData(MouseButton.Middle)] [InlineData(MouseButton.X)]
    public void OtherButtonsClearOnlyOnDown(MouseButton button)
    {
        var hint = Create(true);
        hint.OnMouse(new(button, false, 0, 0, 0, false));
        Assert.True(hint.Likely);
        hint.OnMouse(new(button, true, 0, 0, 0, false));
        Assert.False(hint.Likely);
    }

    [Fact]
    public void ReleaseWithoutPressDoesNotInventDrag()
    {
        var hint = Create();
        hint.OnMouse(new(MouseButton.Left, false, 100, 100, 100, false));
        Assert.False(hint.Likely);
    }

    [Fact]
    public void ClearResetsHint()
    {
        var hint = Create(true);
        hint.Clear();
        Assert.False(hint.Likely);
    }
}

namespace Raskladushka.Core.Typing;

public sealed class SelectionHint(SelectionHintOptions options)
{
    private readonly SelectionHintOptions _options = options;
    private MouseInput? _lastLeftDown;
    public bool Likely { get; private set; }

    public void OnKey(KeyEvent e, Modifiers mods)
    {
        if (!e.IsDown || e.Vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 or
            0x5B or 0x5C or 0x14 or 0x90 or 0x91) return;
        if (!mods.Alt && !mods.Win)
        {
            if (mods.Shift && e.Vk is >= 0x21 and <= 0x28)
            {
                Likely = true;
                return;
            }
            if (mods.Ctrl && !mods.Shift)
            {
                if (e.Vk is 0x43 or 0x2D) return;
                if (e.Vk == 0x41)
                {
                    Likely = true;
                    return;
                }
            }
        }
        Likely = false;
    }

    public void OnMouse(MouseInput m)
    {
        if (m.Button != MouseButton.Left)
        {
            if (m.IsDown) Likely = false;
            return;
        }
        if (m.IsDown)
        {
            Likely = m.Shift || (_lastLeftDown is { } previous &&
                m.TimeMs - previous.TimeMs >= 0 && m.TimeMs - previous.TimeMs <= _options.DoubleClickMs &&
                Math.Abs((long)m.X - previous.X) <= _options.DoubleClickX / 2 &&
                Math.Abs((long)m.Y - previous.Y) <= _options.DoubleClickY / 2);
            _lastLeftDown = m;
        }
        else if (_lastLeftDown is { } start &&
            (Math.Abs((long)m.X - start.X) > _options.DragX || Math.Abs((long)m.Y - start.Y) > _options.DragY))
            Likely = true;
    }

    public void Clear() => Likely = false;
}

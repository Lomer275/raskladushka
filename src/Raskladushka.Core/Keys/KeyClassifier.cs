namespace Raskladushka.Core.Keys;

public static class KeyClassifier
{
    public static KeyClass Classify(int vkCode, int scanCode, bool extended)
    {
        var kind = vkCode switch
        {
            0x08 => KeyKind.Backspace,
            0x10 or 0xA0 or 0xA1 => KeyKind.Shift,
            0x14 => KeyKind.CapsLock,
            0x11 or 0xA2 or 0xA3 or 0x12 or 0xA4 or 0xA5 or 0x5B or 0x5C => KeyKind.Modifier,
            0x0D or 0x09 or 0x1B or >= 0x21 and <= 0x28 or 0x2D or 0x2E
                or >= 0x60 and <= 0x6F => KeyKind.Reset,
            _ => KeyKind.Ignore
        };
        if (kind != KeyKind.Ignore)
            return new KeyClass(kind);

        if (extended)
            return new KeyClass(scanCode == 0x35 ? KeyKind.Reset : KeyKind.Ignore);

        PhysKey? key = scanCode switch
        {
            0x29 => PhysKey.Backquote,
            >= 0x02 and <= 0x0B => (PhysKey)((int)PhysKey.D1 + scanCode - 0x02),
            0x0C => PhysKey.Minus,
            0x0D => PhysKey.Equals,
            >= 0x10 and <= 0x19 => (PhysKey)((int)PhysKey.Q + scanCode - 0x10),
            0x1A => PhysKey.LBracket,
            0x1B => PhysKey.RBracket,
            0x2B => PhysKey.Backslash,
            >= 0x1E and <= 0x26 => (PhysKey)((int)PhysKey.A + scanCode - 0x1E),
            0x27 => PhysKey.Semicolon,
            0x28 => PhysKey.Quote,
            >= 0x2C and <= 0x32 => (PhysKey)((int)PhysKey.Z + scanCode - 0x2C),
            0x33 => PhysKey.Comma,
            0x34 => PhysKey.Period,
            0x35 => PhysKey.Slash,
            0x39 => PhysKey.Space,
            _ => null
        };
        return key is { } characterKey
            ? new KeyClass(KeyKind.Char, characterKey)
            : new KeyClass(KeyKind.Ignore);
    }
}

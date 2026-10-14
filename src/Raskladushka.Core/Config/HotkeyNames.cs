namespace Raskladushka.Core.Config;

public static class HotkeyNames
{
    private static readonly Dictionary<string, int> _keys = CreateKeys();

    public static bool TryGetVk(string name, out int vk)
    {
        vk = 0;
        return name is not null && _keys.TryGetValue(name, out vk);
    }

    public static bool IsAltTap(string name) => string.Equals(name, "Alt", StringComparison.OrdinalIgnoreCase);

    public static bool IsValidFixHotkey(string name) => IsAltTap(name) || TryGetVk(name, out _);

    private static Dictionary<string, int> CreateKeys()
    {
        var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["RControl"] = 0xA3, ["RAlt"] = 0xA5, ["RShift"] = 0xA1,
            ["Pause"] = 0x13, ["ScrollLock"] = 0x91
        };
        for (var number = 13; number <= 24; number++) keys[$"F{number}"] = 0x7C + number - 13;
        return keys;
    }
}

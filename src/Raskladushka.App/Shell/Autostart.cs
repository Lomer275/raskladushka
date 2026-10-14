using Microsoft.Win32;

namespace Raskladushka.App.Shell;

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Raskladushka") is string value && !string.IsNullOrWhiteSpace(value);
        }
    }

    public static void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue("Raskladushka", $"\"{exePath}\"", RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue("Raskladushka", throwOnMissingValue: false);
    }
}

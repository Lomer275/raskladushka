using Raskladushka.Core.Config;

namespace Raskladushka.App.Engine;

public enum GateReason { None, Paused, Checking, Password, Fullscreen, Excluded }

public sealed class ActivityGate
{
    private readonly FocusTracker _focus;
    private readonly Func<Settings> _settings;
    private int _paused;
    public event Action? Changed;

    public ActivityGate(FocusTracker focus, Func<Settings> settings)
    {
        _focus = focus;
        _settings = settings;
    }

    public bool Paused
    {
        get => Volatile.Read(ref _paused) != 0;
        set { if (Interlocked.Exchange(ref _paused, value ? 1 : 0) != (value ? 1 : 0)) Changed?.Invoke(); }
    }
    public bool TypingAllowed => Reason == GateReason.None;
    public GateReason Reason
    {
        get
        {
            var info = _focus.Current;
            var settings = _settings();
            if (Paused) return GateReason.Paused;
            if (info.Checking) return GateReason.Checking;
            if (info.IsPassword) return GateReason.Password;
            if (settings.PauseInFullscreen && info.IsFullscreen) return GateReason.Fullscreen;
            if (settings.Exclusions.Processes.Any(process => string.Equals(NormalizeProcessName(process),
                    NormalizeProcessName(info.ProcessName), StringComparison.OrdinalIgnoreCase)))
                return GateReason.Excluded;
            return GateReason.None;
        }
    }
    public bool AutoSwitchAllowed => TypingAllowed && _focus.Current.WindowClass != "ConsoleWindowClass";
    public bool DictationAllowed => TypingAllowed;

    private static string NormalizeProcessName(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}

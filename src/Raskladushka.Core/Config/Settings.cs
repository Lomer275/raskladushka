namespace Raskladushka.Core.Config;

public sealed record DictationSettings
{
    public string Hotkey { get; init; } = "RControl";
    public string Language { get; init; } = "ru";
    public string Prompt { get; init; } = "";
    public double MinSeconds { get; init; } = 0.4;
    public int IdleUnloadMinutes { get; init; } = 5;
    public string Microphone { get; init; } = "";
}

public sealed record LayoutSettings
{
    public bool AutoSwitch { get; init; } = true;
    public string FixHotkey { get; init; } = "Alt";
    public int MinWordLength { get; init; } = 3;
}

public sealed record ExclusionSettings
{
    public IReadOnlyList<string> Processes { get; init; } = [];
}

public sealed record WhisperSettings
{
    public string Dir { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raskladushka", "whisper");
    public string Model { get; init; } = "ggml-large-v3-turbo-q5_0.bin";
    public string CpuModel { get; init; } = "ggml-small.bin";
    public string GpuBuild { get; init; } = "cuda";
    public string CpuBuild { get; init; } = "cpu";
    public int CpuThreads { get; init; } = 0;
}

public sealed record DiagnosticsSettings
{
    public bool LogWords { get; init; } = false;
}

public sealed record UpdateSettings
{
    public bool Notify { get; init; } = true;
}

public sealed record Settings
{
    public DictationSettings Dictation { get; init; } = new();
    public LayoutSettings Layout { get; init; } = new();
    public bool PauseInFullscreen { get; init; } = true;
    public ExclusionSettings Exclusions { get; init; } = new();
    public WhisperSettings Whisper { get; init; } = new();
    public DiagnosticsSettings Diagnostics { get; init; } = new();
    public UpdateSettings Updates { get; init; } = new();
}

public sealed record LoadResult(Settings Settings, string? Error);

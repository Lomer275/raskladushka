using Raskladushka.Core.Config;

namespace Raskladushka.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "nested", "settings.json");

    private LoadResult Load(string json, Settings? previous = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, json);
        return SettingsStore.Load(SettingsPath, previous);
    }

    [Fact]
    public void CreatesDefaultsAndParentDirectory()
    {
        var result = SettingsStore.Load(SettingsPath);
        Assert.Null(result.Error);
        Assert.Equal(SettingsStore.ToJson(new Settings()), File.ReadAllText(SettingsPath));
        Assert.Equal(SettingsStore.ToJson(new Settings()), SettingsStore.ToJson(result.Settings));
    }

    [Fact]
    public void SupportsPartialSettingsCommentsAndTrailingCommas()
    {
        var result = Load("{ /* comment */ \"dictation\": {\"language\": \"auto\",}, \"unknown\": true,}");
        Assert.Null(result.Error);
        Assert.Equal("auto", result.Settings.Dictation.Language);
        Assert.Equal(SettingsStore.ToJson(new Settings { Dictation = new() { Language = "auto" } }), SettingsStore.ToJson(result.Settings));
    }

    [Theory]
    [InlineData("{\"dictation\":")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("{\"dictation\":{\"minSeconds\":\"private-content\"}}")]
    [InlineData("null")]
    [InlineData("{\"dictation\":null}")]
    public void InvalidJsonKeepsPreviousOrDefaults(string json)
    {
        var previous = new Settings { PauseInFullscreen = false };
        var result = Load(json, previous);
        Assert.Same(previous, result.Settings);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("private-content", result.Error);
        result = Load(json);
        Assert.NotNull(result.Error);
        Assert.Equal(SettingsStore.ToJson(new Settings()), SettingsStore.ToJson(result.Settings));
    }

    [Theory]
    [InlineData(-1, 0, 99, 0.1, 1, 10)]
    [InlineData(99, 999, 0, 5, 120, 2)]
    public void ClampsBounds(double seconds, int minutes, int length, double expectedSeconds, int expectedMinutes, int expectedLength)
    {
        var result = Load(SettingsStore.ToJson(new Settings { Dictation = new() { MinSeconds = seconds, IdleUnloadMinutes = minutes }, Layout = new() { MinWordLength = length } }));
        Assert.Equal(expectedSeconds, result.Settings.Dictation.MinSeconds);
        Assert.Equal(expectedMinutes, result.Settings.Dictation.IdleUnloadMinutes);
        Assert.Equal(expectedLength, result.Settings.Layout.MinWordLength);
    }

    [Fact]
    public void InvalidKeysUseDefaultsWithoutExposingInput()
    {
        var result = Load("{\"dictation\":{\"hotkey\":\"Banana\"},\"layout\":{\"fixHotkey\":\"secret\"}}");
        Assert.Equal("RControl", result.Settings.Dictation.Hotkey);
        Assert.Equal("Alt", result.Settings.Layout.FixHotkey);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("Banana", result.Error);
        Assert.DoesNotContain("secret", result.Error);
    }

    [Theory]
    [InlineData("Alt")]
    [InlineData("alt")]
    [InlineData("ALT")]
    public void AltIsValidOnlyForFixHotkey(string name)
    {
        Assert.True(HotkeyNames.IsValidFixHotkey(name));
        Assert.False(HotkeyNames.TryGetVk(name, out _));
        var result = Load(SettingsStore.ToJson(new Settings { Layout = new() { FixHotkey = name } }));
        Assert.Null(result.Error);
        Assert.Equal(name, result.Settings.Layout.FixHotkey);
        result = Load(SettingsStore.ToJson(new Settings { Dictation = new() { Hotkey = name } }));
        Assert.NotNull(result.Error);
        Assert.Equal("RControl", result.Settings.Dictation.Hotkey);
    }

    [Fact]
    public void FixHotkeyDefaultsToAlt()
    {
        Assert.Equal("Alt", new Settings().Layout.FixHotkey);
        Assert.Equal("Alt", SettingsStore.Load(SettingsPath).Settings.Layout.FixHotkey);
    }

    [Fact]
    public void RoundTripsAllSettings()
    {
        var settings = new Settings
        {
            Dictation = new() { Hotkey = "F24", Language = "en", Prompt = "terms", MinSeconds = 2, IdleUnloadMinutes = 10, Microphone = "mic" },
            Layout = new() { AutoSwitch = false, FixHotkey = "RAlt", MinWordLength = 7 },
            Exclusions = new() { Processes = ["game", "editor"] },
            Whisper = new() { Dir = "custom", Model = "model.bin" },
            PauseInFullscreen = false
        };
        var result = Load(SettingsStore.ToJson(settings));
        Assert.Null(result.Error);
        Assert.Equal(settings.Dictation, result.Settings.Dictation);
        Assert.Equal(settings.Layout, result.Settings.Layout);
        Assert.Equal(settings.Whisper, result.Settings.Whisper);
        Assert.Equal(settings.PauseInFullscreen, result.Settings.PauseInFullscreen);
        Assert.Equal(settings.Exclusions.Processes.ToArray(), result.Settings.Exclusions.Processes.ToArray());
        Assert.Contains("\"minSeconds\"", SettingsStore.ToJson(settings));
    }

    [Fact]
    public void InaccessibleDestinationReturnsError()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "file");
        File.WriteAllText(blocker, "");
        var result = SettingsStore.Load(Path.Combine(blocker, "settings.json"), new Settings { PauseInFullscreen = false });
        Assert.NotNull(result.Error);
        Assert.Equal(SettingsStore.ToJson(new Settings()), SettingsStore.ToJson(result.Settings));
    }

    [Fact]
    public void DiagnosticsDefaultsAreCompatibleWithOldWhisperPath()
    {
        Assert.False(new Settings().Diagnostics.LogWords);
        var result = Load("""{"whisper":{"dir":"E:\\voice\\whisper"}}""");
        Assert.Null(result.Error);
        Assert.False(result.Settings.Diagnostics.LogWords);
        Assert.Equal(@"E:\voice\whisper", result.Settings.Whisper.Dir);
    }

    [Fact]
    public void WordLoggingOptInRoundTrips()
    {
        var result = Load(SettingsStore.ToJson(new Settings { Diagnostics = new() { LogWords = true } }));
        Assert.Null(result.Error);
        Assert.True(result.Settings.Diagnostics.LogWords);
    }

    [Fact]
    public void NullDiagnosticsIsInvalidStructure()
    {
        var previous = new Settings();
        var result = Load("""{"diagnostics":null}""", previous);
        Assert.Equal("Неверная структура настроек.", result.Error);
        Assert.Same(previous, result.Settings);
    }

    [Fact]
    public void WhisperDefaultsToLocalApplicationData()
    {
        var path = new Settings().Whisper.Dir;
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Raskladushka", "whisper"), path);
        Assert.EndsWith(Path.Combine("Raskladushka", "whisper"), path);
        Assert.False(path.StartsWith("D:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingFileUsesNewWhisperDefaults()
    {
        var result = SettingsStore.Load(SettingsPath);
        Assert.Null(result.Error);
        Assert.Equal("ggml-large-v3-turbo-q5_0.bin", result.Settings.Whisper.Model);
        Assert.Equal("ggml-small.bin", result.Settings.Whisper.CpuModel);
        Assert.Equal("cuda", result.Settings.Whisper.GpuBuild);
        Assert.Equal("cpu", result.Settings.Whisper.CpuBuild);
        Assert.Equal(0, result.Settings.Whisper.CpuThreads);
    }

    [Theory]
    [InlineData("""{"whisper":{"gpuBuild":"..\\x"}}""")]
    [InlineData("""{"whisper":{"model":"a/b.bin"}}""")]
    [InlineData("""{"whisper":{"model":""}}""")]
    [InlineData("""{"whisper":{"cpuModel":" small.bin"}}""")]
    [InlineData("""{"whisper":{"cpuBuild":"cpu "}}""")]
    [InlineData("""{"whisper":{"cpuBuild":"C:cpu"}}""")]
    [InlineData("""{"whisper":{"model":"a..bin"}}""")]
    public void InvalidWhisperNameKeepsPrevious(string json)
    {
        var previous = new Settings();
        var result = Load(json, previous);
        Assert.Same(previous, result.Settings);
        Assert.Equal("Неверный путь голоса в настройках.", result.Error);
        Assert.Equal(new Settings().Whisper, Load(json).Settings.Whisper);
    }

    [Theory]
    [InlineData("cpuModel")]
    [InlineData("gpuBuild")]
    [InlineData("cpuBuild")]
    public void NullWhisperNameIsInvalidStructure(string key)
    {
        var previous = new Settings();
        var result = Load("{\"whisper\":{\"" + key + "\":null}}", previous);
        Assert.Same(previous, result.Settings);
        Assert.Equal("Неверная структура настроек.", result.Error);
    }

    [Fact]
    public void OldWhisperSettingsKeepModelAndUseNewDefaults()
    {
        var result = Load("""{"whisper":{"dir":"E:\\voice","model":"ggml-large-v3-turbo.bin"}}""");
        Assert.Null(result.Error);
        Assert.Equal(new WhisperSettings { Dir = @"E:\voice", Model = "ggml-large-v3-turbo.bin" }, result.Settings.Whisper);
    }

    [Theory]
    [InlineData(65, 64)]
    [InlineData(-1, 0)]
    public void ClampsCpuThreads(int value, int expected)
    {
        var result = Load(SettingsStore.ToJson(new Settings { Whisper = new() { CpuThreads = value } }));
        Assert.Null(result.Error);
        Assert.Equal(expected, result.Settings.Whisper.CpuThreads);
    }

    [Fact]
    public void EmptyBuildUsesRoot()
    {
        var result = Load("""{"whisper":{"gpuBuild":""}}""");
        Assert.Null(result.Error);
        Assert.Equal("", result.Settings.Whisper.GpuBuild);
    }

    [Fact]
    public void UpdateNotificationsDefaultToEnabledAndPreserveDisabled()
    {
        var defaults = Load("{}");
        Assert.Null(defaults.Error);
        Assert.True(defaults.Settings.Updates.Notify);
        var disabled = Load("""{"updates":{"notify":false}}""");
        Assert.Null(disabled.Error);
        Assert.False(disabled.Settings.Updates.Notify);
        var roundTrip = Load(SettingsStore.ToJson(disabled.Settings));
        Assert.Null(roundTrip.Error);
        Assert.False(roundTrip.Settings.Updates.Notify);
    }

    [Fact]
    public void NullUpdatesIsInvalidStructure()
    {
        var previous = new Settings { Updates = new() { Notify = false } };
        var result = Load("""{"updates":null}""", previous);
        Assert.Same(previous, result.Settings);
        Assert.Equal("Неверная структура настроек.", result.Error);
    }

    [Fact]
    public void N1_VoiceServerDefaultsAndRoundTrip()
    {
        var old = Load("{}");
        Assert.Null(old.Error);
        Assert.Equal("", old.Settings.VoiceServer.Url);
        Assert.Equal("", old.Settings.VoiceServer.KeyProtected);
        var settings = new Settings
        {
            VoiceServer = new() { Url = "https://example.test/inference", KeyProtected = "test-key-protected" }
        };
        var json = SettingsStore.ToJson(settings);
        Assert.Contains("\"voiceServer\"", json);
        Assert.Contains("\"url\"", json);
        Assert.Contains("\"keyProtected\"", json);
        var loaded = Load(json);
        Assert.Null(loaded.Error);
        Assert.Equal(settings.VoiceServer, loaded.Settings.VoiceServer);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

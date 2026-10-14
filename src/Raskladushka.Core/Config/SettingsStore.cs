using System.Text.Json;

namespace Raskladushka.Core.Config;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static LoadResult Load(string path, Settings? previous = null)
    {
        try
        {
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                var defaults = new Settings();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    File.WriteAllText(path, ToJson(defaults));
                    return new(defaults, null);
                }
                catch (Exception)
                {
                    return new(defaults, "Не удалось записать настройки по умолчанию.");
                }
            }

            var settings = JsonSerializer.Deserialize<Settings>(json, _options);
            if (settings is null || settings.Dictation is null || settings.Layout is null || settings.Updates is null ||
                settings.Diagnostics is null || settings.Exclusions is null || settings.Whisper is null || settings.Exclusions.Processes is null ||
                settings.Exclusions.Processes.Any(process => process is null) ||
                settings.Dictation.Language is null || settings.Dictation.Prompt is null ||
                settings.Dictation.Microphone is null || settings.Whisper.Dir is null || settings.Whisper.Model is null ||
                settings.Whisper.CpuModel is null || settings.Whisper.GpuBuild is null || settings.Whisper.CpuBuild is null)
                return new(previous ?? new Settings(), "Неверная структура настроек.");

            if (settings.Whisper.Model.Length == 0 ||
                !IsSimpleName(settings.Whisper.Model) || !IsSimpleName(settings.Whisper.CpuModel) ||
                !IsSimpleName(settings.Whisper.GpuBuild) || !IsSimpleName(settings.Whisper.CpuBuild))
                return new(previous ?? new Settings(), "Неверный путь голоса в настройках.");

            var validDictationKey = HotkeyNames.TryGetVk(settings.Dictation.Hotkey, out _);
            var validFixKey = HotkeyNames.IsValidFixHotkey(settings.Layout.FixHotkey);
            settings = settings with
            {
                Dictation = settings.Dictation with
                {
                    Hotkey = validDictationKey ? settings.Dictation.Hotkey : "RControl",
                    MinSeconds = Math.Clamp(settings.Dictation.MinSeconds, 0.1, 5),
                    IdleUnloadMinutes = Math.Clamp(settings.Dictation.IdleUnloadMinutes, 1, 120)
                },
                Whisper = settings.Whisper with { CpuThreads = Math.Clamp(settings.Whisper.CpuThreads, 0, 64) },
                Layout = settings.Layout with
                {
                    FixHotkey = validFixKey ? settings.Layout.FixHotkey : "Alt",
                    MinWordLength = Math.Clamp(settings.Layout.MinWordLength, 2, 10)
                }
            };
            return new(settings, validDictationKey && validFixKey ? null : "Неизвестная клавиша в настройках; применена клавиша по умолчанию.");
        }
        catch (JsonException error)
        {
            // JsonException.Message may include user-provided text.
            return new(previous ?? new Settings(), $"Некорректный JSON: строка {error.LineNumber}, позиция {error.BytePositionInLine}.");
        }
        catch (Exception)
        {
            return new(previous ?? new Settings(), "Не удалось прочитать или записать настройки.");
        }
    }

    private static bool IsSimpleName(string name) =>
        name == name.Trim() && name.IndexOfAny(['\\', '/', ':']) < 0 && !name.Contains("..");

    public static string ToJson(Settings settings) => JsonSerializer.Serialize(settings, _options);
}

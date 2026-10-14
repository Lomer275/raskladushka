using System.Globalization;
using Raskladushka.Core.Config;

namespace Raskladushka.Core.Dictation;

public sealed record WhisperLaunch(string Executable, string WorkingDirectory, string ModelFile, WhisperMode Mode,
    string Language, int? Threads)
{
    public IReadOnlyList<string> Arguments(int port)
    {
        List<string> arguments = ["-m", ModelFile, "--host", "127.0.0.1", "--port",
            port.ToString(CultureInfo.InvariantCulture), "-l", Language];
        if (Mode == WhisperMode.Cpu)
            arguments.AddRange(["-ng", "-t", Threads!.Value.ToString(CultureInfo.InvariantCulture)]);
        return arguments;
    }
}

public static class WhisperLayout
{
    public const string ServerExe = "whisper-server.exe";

    public static string BuildDirectory(WhisperSettings settings, WhisperMode mode)
    {
        var directory = Path.GetFullPath(settings.Dir);
        var build = mode == WhisperMode.Gpu ? settings.GpuBuild : settings.CpuBuild;
        return build.Length == 0 ? directory : Path.Combine(directory, build);
    }

    public static bool HasBuild(WhisperSettings settings, WhisperMode mode, Func<string, bool> fileExists) =>
        fileExists(Path.Combine(BuildDirectory(settings, mode), ServerExe));

    public static string? ModelFor(WhisperSettings settings, WhisperMode mode, Func<string, bool> fileExists)
    {
        var directory = Path.GetFullPath(settings.Dir);
        if (mode == WhisperMode.Cpu && settings.CpuModel.Length > 0 &&
            fileExists(Path.Combine(directory, settings.CpuModel)))
            return settings.CpuModel;
        return fileExists(Path.Combine(directory, settings.Model)) ? settings.Model : null;
    }

    public static bool HasCpuModel(WhisperSettings settings, Func<string, bool> fileExists) =>
        settings.CpuModel.Length == 0 || fileExists(Path.Combine(Path.GetFullPath(settings.Dir), settings.CpuModel));

    public static bool IsInstalled(WhisperSettings settings, Func<string, bool> fileExists) =>
        (HasBuild(settings, WhisperMode.Gpu, fileExists) && ModelFor(settings, WhisperMode.Gpu, fileExists) is not null) ||
        (HasBuild(settings, WhisperMode.Cpu, fileExists) && ModelFor(settings, WhisperMode.Cpu, fileExists) is not null);

    public static int CpuThreads(WhisperSettings settings, int processorCount) =>
        settings.CpuThreads > 0 ? settings.CpuThreads : Math.Clamp(processorCount / 2, 4, 8);

    public static WhisperLaunch Resolve(WhisperSettings settings, WhisperMode mode, string language,
        int processorCount, Func<string, bool> fileExists)
    {
        var executable = Path.Combine(BuildDirectory(settings, mode), ServerExe);
        if (!fileExists(executable)) throw new WhisperStartException($"Не найден {executable}");
        var directory = Path.GetFullPath(settings.Dir);
        var model = ModelFor(settings, mode, fileExists) ??
            throw new WhisperStartException($"Не найден {Path.Combine(directory, settings.Model)}");
        return new(executable, directory, model, mode, language,
            mode == WhisperMode.Cpu ? CpuThreads(settings, processorCount) : null);
    }
}

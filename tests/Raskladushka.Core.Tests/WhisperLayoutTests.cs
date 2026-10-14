using Raskladushka.Core.Config;
using Raskladushka.Core.Dictation;

namespace Raskladushka.Core.Tests;

public sealed class WhisperLayoutTests
{
    private readonly WhisperSettings _settings = new()
    {
        Dir = Path.Combine(Path.GetTempPath(), "whisper-layout-tests")
    };

    private static Func<string, bool> Files(WhisperSettings settings, params string[] names)
    {
        var paths = names.Select(name => Path.Combine(Path.GetFullPath(settings.Dir), name)).ToHashSet();
        return paths.Contains;
    }

    [Fact]
    public void GpuUsesCudaBuildAndShortModelName()
    {
        var exists = Files(_settings, Path.Combine("cuda", WhisperLayout.ServerExe), _settings.Model);
        var launch = WhisperLayout.Resolve(_settings, WhisperMode.Gpu, "ru", 12, exists);
        Assert.Equal(Path.Combine(_settings.Dir, "cuda", WhisperLayout.ServerExe), launch.Executable);
        Assert.Equal(_settings.Dir, launch.WorkingDirectory);
        Assert.Equal("ggml-large-v3-turbo-q5_0.bin", launch.ModelFile);
        Assert.Equal(WhisperMode.Gpu, launch.Mode);
        Assert.Null(launch.Threads);
        Assert.Equal(["-m", "ggml-large-v3-turbo-q5_0.bin", "--host", "127.0.0.1", "--port", "5000", "-l", "ru"],
            launch.Arguments(5000));
    }

    [Fact]
    public void CpuUsesSmallModelAndAutoThreads()
    {
        var exists = Files(_settings, Path.Combine("cpu", WhisperLayout.ServerExe), _settings.CpuModel);
        var launch = WhisperLayout.Resolve(_settings, WhisperMode.Cpu, "ru", 12, exists);
        Assert.Equal(Path.Combine(_settings.Dir, "cpu", WhisperLayout.ServerExe), launch.Executable);
        Assert.Equal(_settings.Dir, launch.WorkingDirectory);
        Assert.Equal("ggml-small.bin", launch.ModelFile);
        Assert.Equal(WhisperMode.Cpu, launch.Mode);
        Assert.Equal(6, launch.Threads);
        Assert.Equal(["-m", "ggml-small.bin", "--host", "127.0.0.1", "--port", "5000", "-l", "ru", "-ng", "-t", "6"],
            launch.Arguments(5000));
        Assert.True(WhisperLayout.HasCpuModel(_settings, exists));
    }

    [Fact]
    public void CpuFallsBackToGpuModel()
    {
        var exists = Files(_settings, Path.Combine("cpu", WhisperLayout.ServerExe), _settings.Model);
        var launch = WhisperLayout.Resolve(_settings, WhisperMode.Cpu, "ru", 12, exists);
        Assert.Equal("ggml-large-v3-turbo-q5_0.bin", launch.ModelFile);
        Assert.False(WhisperLayout.HasCpuModel(_settings, exists));
    }

    [Theory]
    [InlineData(WhisperMode.Gpu)]
    [InlineData(WhisperMode.Cpu)]
    public void LegacyLayoutSharesRootBuildAndModel(WhisperMode mode)
    {
        var settings = _settings with
        {
            GpuBuild = "", CpuBuild = "", CpuModel = "", Model = "ggml-large-v3-turbo.bin"
        };
        var exists = Files(settings, WhisperLayout.ServerExe, settings.Model);
        var launch = WhisperLayout.Resolve(settings, mode, "ru", 12, exists);
        Assert.Equal(Path.Combine(settings.Dir, WhisperLayout.ServerExe), launch.Executable);
        Assert.Equal(settings.Model, launch.ModelFile);
        Assert.Equal(mode == WhisperMode.Cpu, launch.Arguments(5000).Contains("-ng"));
        Assert.True(WhisperLayout.HasCpuModel(settings, exists));
    }

    [Theory]
    [InlineData(0, 2, 4)]
    [InlineData(0, 8, 4)]
    [InlineData(0, 12, 6)]
    [InlineData(0, 32, 8)]
    [InlineData(3, 32, 3)]
    [InlineData(64, 2, 64)]
    public void CpuThreadCountRespectsOverrideAndAutoBounds(int threads, int processors, int expected)
    {
        Assert.Equal(expected, WhisperLayout.CpuThreads(_settings with { CpuThreads = threads }, processors));
    }

    [Fact]
    public void CyrillicDirectoryDoesNotAppearInArguments()
    {
        var settings = _settings with { Dir = Path.Combine(Path.GetTempPath(), "Иван Петров", "голос") };
        var exists = Files(settings, Path.Combine("cpu", WhisperLayout.ServerExe), settings.CpuModel);
        var launch = WhisperLayout.Resolve(settings, WhisperMode.Cpu, "ru", 12, exists);
        Assert.Equal(settings.Dir, launch.WorkingDirectory);
        Assert.All(launch.Arguments(5000), argument => Assert.DoesNotContain("Иван", argument));
    }

    [Theory]
    [InlineData(WhisperMode.Gpu, false)]
    [InlineData(WhisperMode.Gpu, true)]
    [InlineData(WhisperMode.Cpu, false)]
    [InlineData(WhisperMode.Cpu, true)]
    public void MissingFilesNameTheFullExpectedPath(WhisperMode mode, bool hasBuild)
    {
        var build = mode == WhisperMode.Gpu ? "cuda" : "cpu";
        var exists = Files(_settings, hasBuild ? [Path.Combine(build, WhisperLayout.ServerExe)] : []);
        var error = Assert.Throws<WhisperStartException>(() =>
            WhisperLayout.Resolve(_settings, mode, "ru", 12, exists));
        Assert.Equal($"Не найден {Path.Combine(_settings.Dir, hasBuild ? _settings.Model : Path.Combine(build, WhisperLayout.ServerExe))}",
            error.Message);
        Assert.Null(WhisperLayout.ModelFor(_settings, mode, exists));
    }

    [Theory]
    [InlineData("cpu", "ggml-small.bin", true)]
    [InlineData("cuda", "ggml-large-v3-turbo-q5_0.bin", true)]
    [InlineData("cuda", "", false)]
    [InlineData("", "", false)]
    public void InstalledRequiresBuildAndModelForSameMode(string build, string model, bool expected)
    {
        List<string> files = [];
        if (build.Length > 0) files.Add(Path.Combine(build, WhisperLayout.ServerExe));
        if (model.Length > 0) files.Add(model);
        Assert.Equal(expected, WhisperLayout.IsInstalled(_settings, Files(_settings, files.ToArray())));
    }

    [Fact]
    public void CpuBuildDoesNotCountAsGpuBuild()
    {
        Assert.False(WhisperLayout.HasBuild(_settings, WhisperMode.Gpu,
            Files(_settings, Path.Combine("cpu", WhisperLayout.ServerExe))));
    }

    [Fact]
    public void RelativeDirectoryIsResolvedToFullPath()
    {
        var settings = _settings with { Dir = "voice" };
        var exists = Files(settings, Path.Combine("cpu", WhisperLayout.ServerExe), settings.CpuModel);
        var launch = WhisperLayout.Resolve(settings, WhisperMode.Cpu, "ru", 12, exists);
        Assert.Equal(Path.GetFullPath(settings.Dir), launch.WorkingDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(settings.Dir), "cpu", WhisperLayout.ServerExe), launch.Executable);
    }
}

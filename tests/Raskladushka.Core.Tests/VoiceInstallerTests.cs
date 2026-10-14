using Raskladushka.Core.Voice;

namespace Raskladushka.Core.Tests;

public class VoiceInstallerTests
{
    [Fact]
    public async Task InstalledCheckBeforeAndAfterCpuInstallDoesNotUseNetwork()
    {
        using var kit = new VoiceTestKit();
        var installer = kit.Installer();
        var ct = TestContext.Current.CancellationToken;
        Assert.False(await installer.IsInstalledAsync(VoiceVariant.Cpu, kit.WhisperDir, ct));
        Assert.Empty(kit.Handler.Requests);
        await kit.Install(ct: ct);
        kit.Handler.Requests.Clear();
        Assert.True(await installer.IsInstalledAsync(VoiceVariant.Cpu, kit.WhisperDir, ct));
        Assert.Empty(kit.Handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BlockedDestinationReportsExtractionAndKeepsDownload(bool build)
    {
        using var kit = new VoiceTestKit();
        if (build) kit.Seed(Path.Combine(kit.WhisperDir, "cpu"), [42]);
        else Directory.CreateDirectory(Path.Combine(kit.WhisperDir, kit.Model.Target));
        var ex = await Assert.ThrowsAsync<VoiceInstallException>(() => kit.Install(
            build ? VoiceVariant.Cpu : VoiceVariant.CpuModel, ct: TestContext.Current.CancellationToken));
        Assert.Equal(VoiceFailure.Extraction, ex.Failure);
        Assert.Contains(build ? "cpu" : kit.Model.Target, ex.Message);
        Assert.Contains("файлы заняты", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(kit.WhisperDir, "cpu.new")));
        Assert.True(File.Exists(Path.Combine(kit.Downloads, build ? "cpu.zip" : kit.Model.File)));
    }

    [Fact]
    public async Task ChangedInstalledModelIsReplaced()
    {
        using var kit = new VoiceTestKit();
        await kit.Install(VoiceVariant.CpuModel, ct: TestContext.Current.CancellationToken);
        var bytes = kit.Data[kit.Model.File].ToArray();
        bytes[0] ^= 1;
        kit.Seed(Path.Combine(kit.WhisperDir, kit.Model.Target), bytes);
        kit.Handler.Requests.Clear();
        await kit.Install(VoiceVariant.CpuModel, ct: TestContext.Current.CancellationToken);
        Assert.Single(kit.Handler.Requests);
        Assert.Equal(kit.Data[kit.Model.File], File.ReadAllBytes(Path.Combine(kit.WhisperDir, kit.Model.Target)));
    }

    [Fact]
    public async Task CpuInstallAndSecondInstallAreComplete()
    {
        using var kit = new VoiceTestKit();
        await kit.Install(ct: TestContext.Current.CancellationToken);
        var build = Path.Combine(kit.WhisperDir, "cpu");
        foreach (var name in RuntimeCopier.Files.Concat(["whisper-server.exe", "a.dll", ".package"]))
            Assert.True(File.Exists(Path.Combine(build, name)), name);
        Assert.False(File.Exists(Path.Combine(build, "bench.exe")));
        Assert.Equal(kit.Data[kit.Model.File], File.ReadAllBytes(Path.Combine(kit.WhisperDir, kit.Model.Target)));
        Assert.False(Directory.Exists(kit.Downloads));
        Assert.Equal(new[] { "cpu.zip", "ggml-small.bin" }, kit.Handler.Requests.Select(r => r.Url.Segments[^1]));
        kit.Handler.Requests.Clear();
        var reports = new List<VoiceProgress>();
        await kit.Install(progress: new VoiceProgressSink(reports.Add), ct: TestContext.Current.CancellationToken);
        Assert.Empty(kit.Handler.Requests);
        Assert.Equal(2, reports.Count);
        Assert.All(reports, p => Assert.Equal(p.Total, p.Done));
    }

    [Fact]
    public async Task DiskSpaceCheckedBeforeRequests()
    {
        using var kit = new VoiceTestKit();
        var ex = await Assert.ThrowsAsync<VoiceInstallException>(() => kit.Installer(path =>
        {
            Assert.Equal(kit.WhisperDir, path);
            return 0;
        }).InstallAsync(VoiceVariant.Cpu, kit.WhisperDir, kit.RuntimeDir, null, TestContext.Current.CancellationToken));
        Assert.Equal(VoiceFailure.DiskSpace, ex.Failure);
        Assert.Contains("нужно", ex.Message);
        Assert.Contains("свободно 0,0", ex.Message);
        Assert.Empty(kit.Handler.Requests);
    }

    [Fact]
    public async Task RuntimeCheckedBeforeDiskAndRequests()
    {
        using var kit = new VoiceTestKit();
        File.Delete(Path.Combine(kit.RuntimeDir, "vcomp140.dll"));
        var ex = await Assert.ThrowsAsync<VoiceInstallException>(() => kit.Installer(_ => throw new Exception("disk queried"))
            .InstallAsync(VoiceVariant.Cpu, kit.WhisperDir, kit.RuntimeDir, null, TestContext.Current.CancellationToken));
        Assert.Equal(VoiceFailure.MissingRuntime, ex.Failure);
        Assert.Contains("vcomp140.dll", ex.Message);
        Assert.Empty(kit.Handler.Requests);
    }

    [Fact]
    public async Task GpuThenCpuModelDownloadsOnlySmallModel()
    {
        using var kit = new VoiceTestKit();
        await kit.Install(VoiceVariant.Gpu, ct: TestContext.Current.CancellationToken);
        kit.Handler.Requests.Clear();
        await kit.Install(VoiceVariant.CpuModel, ct: TestContext.Current.CancellationToken);
        Assert.Equal("ggml-small.bin", Assert.Single(kit.Handler.Requests).Url.Segments[^1]);
        kit.Handler.Requests.Clear();
        await kit.Install(VoiceVariant.Cpu, ct: TestContext.Current.CancellationToken);
        Assert.Empty(kit.Handler.Requests);
    }

    [Fact]
    public async Task ArchivePathsAreFlattened()
    {
        using var kit = new VoiceTestKit();
        kit.Data["cpu.zip"] = VoiceTestKit.Zip("Release/whisper-server.exe", "Release/sub/x.dll",
            "Release/../../evil.dll", "Release\\sub\\windows.dll");
        kit.RebuildCatalog();
        await kit.Install(ct: TestContext.Current.CancellationToken);
        foreach (var file in new[] { "x.dll", "evil.dll", "windows.dll" })
            Assert.True(File.Exists(Path.Combine(kit.WhisperDir, "cpu", file)));
        Assert.Single(Directory.GetFiles(kit.Root, "evil.dll", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(kit.WhisperDir, "evil.dll")));
    }

    [Fact]
    public async Task CancellationAfterDownloadPreservesPreviousBuild()
    {
        using var kit = new VoiceTestKit();
        var build = Path.Combine(kit.WhisperDir, "cpu");
        kit.Seed(Path.Combine(build, "marker"), [42]);
        kit.Seed(Path.Combine(build, ".package"), [0]);
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => kit.Install(progress: new VoiceProgressSink(p =>
        {
            if (p.PackageId == "build-cpu" && p.Done == p.Total) cts.Cancel();
        }), ct: cts.Token));
        Assert.False(Directory.Exists(build + ".new"));
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(build, "marker")));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("drive")]
    public async Task ExtractionFailureCleansStagingAndPreservesOldBuild(string mode)
    {
        using var kit = new VoiceTestKit();
        kit.Data["cpu.zip"] = mode switch
        {
            "duplicate" => VoiceTestKit.Zip("Release/whisper-server.exe", "Release/x.dll", "Release/sub/X.DLL"),
            "missing" => VoiceTestKit.Zip("Release/x.dll"),
            "drive" => VoiceTestKit.Zip("whisper-server.exe", "C:evil.dll"),
            _ => [1, 2, 3]
        };
        kit.RebuildCatalog();
        kit.Seed(Path.Combine(kit.WhisperDir, "cpu", "marker"), [42]);
        var ex = await Assert.ThrowsAsync<VoiceInstallException>(() => kit.Install(ct: TestContext.Current.CancellationToken));
        Assert.Equal(VoiceFailure.Extraction, ex.Failure);
        Assert.Contains("cpu.zip", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(kit.WhisperDir, "cpu.new")));
        Assert.True(File.Exists(Path.Combine(kit.WhisperDir, "cpu", "marker")));
    }

    [Fact]
    public async Task StaleDirectoriesRemovedAndOldBuildReplaced()
    {
        using var kit = new VoiceTestKit();
        foreach (var name in new[] { "cpu", "cpu.new", "cpu.old" })
            kit.Seed(Path.Combine(kit.WhisperDir, name, "marker"), [42]);
        await kit.Install(ct: TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(Path.Combine(kit.WhisperDir, "cpu.new")));
        Assert.False(Directory.Exists(Path.Combine(kit.WhisperDir, "cpu.old")));
        Assert.False(File.Exists(Path.Combine(kit.WhisperDir, "cpu", "marker")));
    }

    [Fact]
    public void EmbeddedGpuSpaceIsExact()
    {
        using var kit = new VoiceTestKit();
        var catalog = VoiceCatalog.LoadEmbedded();
        var expected = catalog.Packages(VoiceVariant.Gpu).Sum(p => p.Size + p.UnpackedSize) + VoiceInstaller.Margin;
        Assert.Equal(expected, new VoiceInstaller(catalog, kit.Downloader, _ => 0)
            .RequiredFreeBytes(VoiceVariant.Gpu, kit.WhisperDir));
    }

    [Fact]
    public async Task SpaceSubtractsPartialReadyAndInstalledPackages()
    {
        using var kit = new VoiceTestKit();
        var installer = kit.Installer();
        var total = installer.RequiredFreeBytes(VoiceVariant.Cpu, kit.WhisperDir);
        kit.Seed(kit.Part, new byte[123]);
        Assert.Equal(total - 123, installer.RequiredFreeBytes(VoiceVariant.Cpu, kit.WhisperDir));
        kit.Seed(Path.Combine(kit.Downloads, kit.Model.File), kit.Data[kit.Model.File]);
        Assert.Equal(total - kit.Model.Size, installer.RequiredFreeBytes(VoiceVariant.Cpu, kit.WhisperDir));
        File.Delete(kit.Part);
        await kit.Install(ct: TestContext.Current.CancellationToken);
        Assert.Equal(VoiceInstaller.Margin, installer.RequiredFreeBytes(VoiceVariant.Cpu, kit.WhisperDir));
    }

    [Fact]
    public async Task UpperCaseMarkerAcceptedWithoutRuntime()
    {
        using var kit = new VoiceTestKit();
        await kit.Install(ct: TestContext.Current.CancellationToken);
        var marker = Path.Combine(kit.WhisperDir, "cpu", ".package");
        File.WriteAllText(marker, File.ReadAllText(marker).ToUpperInvariant());
        Directory.Delete(kit.RuntimeDir, recursive: true);
        kit.Handler.Requests.Clear();
        await kit.Install(ct: TestContext.Current.CancellationToken);
        Assert.Empty(kit.Handler.Requests);
    }

    [Fact]
    public void RuntimeCopierRequiresAllFilesAndReplacesExisting()
    {
        using var kit = new VoiceTestKit();
        var build = Path.Combine(kit.Root, "build");
        Directory.CreateDirectory(build);
        kit.Seed(Path.Combine(build, RuntimeCopier.Files[0]), [42]);
        RuntimeCopier.CopyInto(kit.RuntimeDir, build);
        Assert.Equal(RuntimeCopier.Files[0], File.ReadAllText(Path.Combine(build, RuntimeCopier.Files[0])));
        File.Delete(Path.Combine(kit.RuntimeDir, "vcomp140.dll"));
        Assert.Equal(VoiceFailure.MissingRuntime, Assert.Throws<VoiceInstallException>(() =>
            RuntimeCopier.CopyInto(kit.RuntimeDir, build)).Failure);
    }
}

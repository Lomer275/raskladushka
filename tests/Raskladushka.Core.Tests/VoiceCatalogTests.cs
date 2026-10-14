using System.Text.Json.Nodes;
using Raskladushka.Core.Config;
using Raskladushka.Core.Voice;

namespace Raskladushka.Core.Tests;

public class VoiceCatalogTests
{
    [Fact]
    public void EmbeddedCatalogMatchesLayoutAndPinnedSources()
    {
        var catalog = VoiceCatalog.LoadEmbedded();
        var packages = catalog.Packages(VoiceVariant.Gpu).Concat(catalog.Packages(VoiceVariant.Cpu)).Distinct().ToArray();
        Assert.Equal(4, packages.Length);
        Assert.Equal("b5454", catalog.WhisperRelease);
        foreach (var p in packages)
            Assert.StartsWith(p.Kind == PackageKind.Build
                ? "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/"
                : "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/", p.Url.ToString());
        var settings = new WhisperSettings();
        Assert.Equal(settings.GpuBuild, packages.Single(p => p.Id == "build-cuda").Target);
        Assert.Equal(settings.CpuBuild, packages.Single(p => p.Id == "build-cpu").Target);
        Assert.Equal(settings.Model, packages.Single(p => p.Id == "model-gpu").File);
        Assert.Equal(settings.CpuModel, packages.Single(p => p.Id == "model-cpu").File);
        Assert.Equal(packages.Where(p => p.Kind == PackageKind.Model).Sum(p => p.Size) +
            packages.Where(p => p.Kind == PackageKind.Build).Sum(p => p.UnpackedSize),
            catalog.DiskBytes(VoiceVariant.Gpu) + catalog.DiskBytes(VoiceVariant.CpuModel));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("kind")]
    [InlineData("target")]
    [InlineData("file")]
    [InlineData("url")]
    [InlineData("size")]
    [InlineData("unpackedSize")]
    [InlineData("sha256")]
    public void MissingPackageField(string field)
    {
        using var kit = new VoiceTestKit();
        var root = JsonNode.Parse(kit.Json)!;
        root["packages"]![0]!.AsObject().Remove(field);
        Assert.Throws<FormatException>(() => VoiceCatalog.Parse(root.ToJsonString()));
    }

    [Theory]
    [InlineData("kind", "other")]
    [InlineData("sha256", "abc")]
    [InlineData("sha256", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("url", "http://example.test/file")]
    [InlineData("url", "relative")]
    [InlineData("target", "a/b")]
    [InlineData("target", "a\\b")]
    [InlineData("target", "a:b")]
    [InlineData("target", "a..b")]
    [InlineData("target", "")]
    [InlineData("file", "a/b")]
    [InlineData("file", "a\\b")]
    [InlineData("file", "a:b")]
    [InlineData("file", "a..b")]
    [InlineData("file", "")]
    public void InvalidText(string field, string value)
    {
        using var kit = new VoiceTestKit();
        var root = JsonNode.Parse(kit.Json)!;
        root["packages"]![0]![field] = value;
        Assert.Throws<FormatException>(() => VoiceCatalog.Parse(root.ToJsonString()));
    }

    [Theory]
    [InlineData("size", 0)]
    [InlineData("size", -1)]
    [InlineData("unpackedSize", -1)]
    [InlineData("unpackedSize", 0)]
    public void InvalidSize(string field, long value)
    {
        using var kit = new VoiceTestKit();
        var root = JsonNode.Parse(kit.Json)!;
        root["packages"]![0]![field] = value;
        Assert.Throws<FormatException>(() => VoiceCatalog.Parse(root.ToJsonString()));
    }

    [Theory]
    [InlineData("whisperRelease")]
    [InlineData("packages")]
    [InlineData("variants")]
    public void MissingRootField(string field)
    {
        using var kit = new VoiceTestKit();
        var root = JsonNode.Parse(kit.Json)!.AsObject();
        root.Remove(field);
        Assert.Throws<FormatException>(() => VoiceCatalog.Parse(root.ToJsonString()));
    }

    [Fact]
    public void DuplicateIdsUnknownReferencesAndMalformedJson()
    {
        using var kit = new VoiceTestKit();
        var root = JsonNode.Parse(kit.Json)!;
        root["packages"]![1]!["id"] = "build-cpu";
        Assert.Throws<FormatException>(() => VoiceCatalog.Parse(root.ToJsonString()));
        root = JsonNode.Parse(kit.Json)!;
        root["variants"]!["gpu"]![0] = "missing";
        Assert.Throws<FormatException>(() => VoiceCatalog.Parse(root.ToJsonString()));
        Assert.Throws<FormatException>(() => VoiceCatalog.Parse("{"));
    }

    [Fact]
    public void HashIsNormalizedAndTotalsAreExact()
    {
        using var kit = new VoiceTestKit();
        var root = JsonNode.Parse(kit.Json)!;
        root["packages"]![0]!["sha256"] = new string('A', 64);
        var catalog = VoiceCatalog.Parse(root.ToJsonString());
        Assert.Equal(new string('a', 64), catalog.Packages(VoiceVariant.Cpu)[0].Sha256);
        Assert.Equal(catalog.Packages(VoiceVariant.Cpu).Sum(p => p.Size), catalog.DownloadBytes(VoiceVariant.Cpu));
    }
}

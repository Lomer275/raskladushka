namespace Raskladushka.Core.Voice;

public enum PackageKind { Build, Model }
public sealed record VoicePackage(string Id, PackageKind Kind, string Target, string File, Uri Url,
    long Size, long UnpackedSize, string Sha256);
public enum VoiceVariant { Gpu, Cpu, CpuModel }
public enum VoiceFailure { Network, Checksum, DiskSpace, Extraction, MissingRuntime }
public sealed record VoiceProgress(string PackageId, long Done, long Total);

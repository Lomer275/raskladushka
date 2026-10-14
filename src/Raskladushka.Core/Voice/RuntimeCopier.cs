namespace Raskladushka.Core.Voice;

public static class RuntimeCopier
{
    public static readonly IReadOnlyList<string> Files =
        Array.AsReadOnly(new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll" });

    internal static void Check(string runtimeDir)
    {
        foreach (var name in Files)
            if (!File.Exists(Path.Combine(runtimeDir, name)))
                throw new VoiceInstallException(VoiceFailure.MissingRuntime,
                    $"Не найден {name} в папке программы. Переустановите Раскладушку.");
    }

    public static void CopyInto(string runtimeDir, string buildDir)
    {
        Check(runtimeDir);
        foreach (var name in Files)
            File.Copy(Path.Combine(runtimeDir, name), Path.Combine(buildDir, name), overwrite: true);
    }
}

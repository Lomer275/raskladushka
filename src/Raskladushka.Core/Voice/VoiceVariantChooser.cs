namespace Raskladushka.Core.Voice;

public static class VoiceVariantChooser
{
    public static VoiceVariant Choose(IEnumerable<string> adapterProviders, bool nvcudaPresent) =>
        nvcudaPresent && adapterProviders.Any(p => p.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            ? VoiceVariant.Gpu : VoiceVariant.Cpu;
}

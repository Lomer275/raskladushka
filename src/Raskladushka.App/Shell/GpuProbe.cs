using Microsoft.Win32;

namespace Raskladushka.App.Shell;

public static class GpuProbe
{
    public static IReadOnlyList<string> AdapterProviders()
    {
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            var providers = new List<string>();
            if (adapters is null) return providers;
            foreach (var name in adapters.GetSubKeyNames())
            {
                try
                {
                    using var adapter = adapters.OpenSubKey(name);
                    if (adapter?.GetValue("ProviderName") is string provider) providers.Add(provider);
                }
                catch (Exception)
                {
                    // The Properties subkey is accessible only to SYSTEM.
                }
            }
            return providers;
        }
        catch (Exception) { return []; }
    }

    public static bool NvcudaPresent()
    {
        try { return File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll")); }
        catch (Exception) { return false; }
    }
}

namespace Raskladushka.Core.Shell;

public static class ConflictingApps
{
    public static IReadOnlyList<string> Find(IEnumerable<string> processNames)
    {
        var punto = false;
        var keyray = false;
        foreach (var name in processNames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (name.Equals("punto", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ps64ldr", StringComparison.OrdinalIgnoreCase)) punto = true;
            // KeyRay's exact process name is not verified.
            if (name.StartsWith("keyray", StringComparison.OrdinalIgnoreCase)) keyray = true;
        }
        var apps = new List<string>();
        if (punto) apps.Add("Punto Switcher");
        if (keyray) apps.Add("KeyRay");
        return apps;
    }
}

namespace Raskladushka.Core.Shell;

[Flags]
public enum MissingLayouts { None = 0, Russian = 1, English = 2 }

public static class LayoutCheck
{
    public const int Russian = 0x0419;
    public const int English = 0x0409;

    // The engine supports only these exact language ids, taken from the low word of HKL.
    public static MissingLayouts Missing(IEnumerable<int> languageIds)
    {
        var missing = MissingLayouts.Russian | MissingLayouts.English;
        foreach (var id in languageIds)
        {
            if (id == Russian) missing &= ~MissingLayouts.Russian;
            if (id == English) missing &= ~MissingLayouts.English;
        }
        return missing;
    }

    public static string LogValue(MissingLayouts missing) => missing switch
    {
        MissingLayouts.None => "",
        MissingLayouts.Russian => "ru",
        MissingLayouts.English => "en",
        _ => "ru,en"
    };
}

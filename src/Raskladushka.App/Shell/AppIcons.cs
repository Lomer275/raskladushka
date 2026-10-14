namespace Raskladushka.App.Shell;

internal static class AppIcons
{
    // Shared by windows for the lifetime of the process.
    public static Icon App { get; } = LoadApp();

    private static Icon LoadApp()
    {
        using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream("app.ico")!;
        return new Icon(stream);
    }

    public static Icon Load(string resource, Size size)
    {
        using var stream = typeof(AppIcons).Assembly.GetManifestResourceStream(resource)!;
        return new Icon(stream, size);
    }
}

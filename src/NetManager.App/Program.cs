using Avalonia;

namespace NetManager.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Relaunch elevated once if we started without administrator rights. If that is declined (or fails) we carry on
        // read-only; the window then shows a banner with a "Restart as administrator" button.
        if (OperatingSystem.IsWindows() && !Elevation.IsElevated
            && !args.Contains(Elevation.AttemptedFlag) && !args.Contains(Elevation.SkipFlag)
            && Elevation.TryRelaunch(args, markAttempted: true))
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

using Avalonia;

namespace NetManager.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Relaunch elevated once if we started without administrator rights. If that is declined (or fails) we carry on
        // read-only; the window then shows a banner with a "Restart as administrator" button.
        Elevation.WasRequested = args.Contains(Elevation.AttemptedFlag);
        if (OperatingSystem.IsWindows() && !Elevation.IsElevated)
        {
            Elevation.Log($"start: not elevated, args={string.Join(' ', args)}, path={Environment.ProcessPath}, network={Elevation.RunsFromNetworkLocation}, adminAccount={Elevation.IsAdminAccount}, uac=[{Elevation.UacSummary}]");
            if (!Elevation.WasRequested && !args.Contains(Elevation.SkipFlag)
                && Elevation.TryRelaunch(args, markAttempted: true).Result == RelaunchResult.Started)
                return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

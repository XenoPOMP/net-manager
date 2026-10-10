using Avalonia;
using Avalonia.Styling;

namespace NetManager.App;

internal static class ThemeService
{
    public static readonly IReadOnlyList<string> Modes = ["system", "light", "dark"];

    /// <summary>"light" / "dark" force a theme; anything else follows the OS.</summary>
    public static void Apply(string? mode)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = mode switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}

using System.Globalization;
using System.Reflection;

namespace NetManager.App;

/// <summary>"v0.1.0 · build 2026-10-10 15:42" — shown in the status bar so a screenshot says which exe is really running.</summary>
internal static class BuildInfo
{
    public static string Text { get; } = Build();

    private static string Build()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? asm.GetName().Version?.ToString(3) ?? "?";

        var stamp = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTime")?.Value;
        var built = DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
            ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "?";
        return $"v{version} · build {built}";
    }
}

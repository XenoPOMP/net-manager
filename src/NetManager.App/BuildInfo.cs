using System.Reflection;

namespace NetManager.App;

/// <summary>"v0.1.0 · build 7" — shown in the status bar so a screenshot says which exe is really running.</summary>
internal static class BuildInfo
{
    public static string Text { get; } = Build();

    private static string Build()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? asm.GetName().Version?.ToString(3) ?? "?";
        var number = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildNumber")?.Value;
        return $"v{version} · {(string.IsNullOrEmpty(number) ? "dev build" : $"build {number}")}";
    }
}

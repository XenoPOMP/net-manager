using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using NetManager.Core.Services;

namespace NetManager.App;

/// <summary>
/// Changing network settings needs administrator rights. app.manifest already asks for them (UAC prompt on a normal launch), but that
/// is bypassed when the program is started via `dotnet run`, an IDE or a non-elevated parent — so the app also checks itself.
/// </summary>
internal static class Elevation
{
    /// <summary>Added to the relaunched process so a failed elevation can't loop forever.</summary>
    public const string AttemptedFlag = "--elevation-attempted";
    /// <summary>Opt-out for debugging (an IDE debugger would otherwise lose the process to the elevated copy).</summary>
    public const string SkipFlag = "--no-elevate";

    public static bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return true;   // nothing to elevate on the demo backend
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Starts an elevated copy of this program (shows the UAC prompt). False when the prompt was declined or it couldn't start.</summary>
    public static bool TryRelaunch(IEnumerable<string> args, bool markAttempted)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe) return false;

        var pass = args.Where(a => a != AttemptedFlag).ToList();
        // `dotnet NetManager.dll`: the process is dotnet.exe and the program has to be passed to it as the first argument.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && Assembly.GetEntryAssembly()?.Location is { Length: > 0 } dll)
            pass.Insert(0, dll);
        if (markAttempted) pass.Add(AttemptedFlag);

        try
        {
            Process.Start(new ProcessStartInfo(exe, CommandLine.Join(pass))
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
            })?.Dispose();
            return true;
        }
        catch (Win32Exception) { return false; }   // 1223 = the user clicked "No" in the UAC dialog
    }
}

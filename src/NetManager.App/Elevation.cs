using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;
using NetManager.Core.Services;

namespace NetManager.App;

public enum RelaunchResult { Started, Declined, Failed }

internal readonly record struct RelaunchOutcome(RelaunchResult Result, string? Detail = null);

/// <summary>
/// Changing network settings needs administrator rights. app.manifest already asks for them (UAC prompt on a normal launch), but that
/// is bypassed when the program is started via `dotnet run`, an IDE or a non-elevated parent — and silently suppressed by a compatibility
/// override such as <c>__COMPAT_LAYER=RunAsInvoker</c> — so the app also checks itself, and says why when elevation doesn't happen.
/// </summary>
internal static class Elevation
{
    /// <summary>Added to the relaunched process so a failed elevation can't loop forever.</summary>
    public const string AttemptedFlag = "--elevation-attempted";
    /// <summary>Opt-out for debugging (an IDE debugger would otherwise lose the process to the elevated copy).</summary>
    public const string SkipFlag = "--no-elevate";

    /// <summary>True when this process is the result of an elevation request (so being un-elevated now means Windows refused or ignored it).</summary>
    public static bool WasRequested { get; set; }

    public static bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return true;   // nothing to elevate on the demo backend
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Member of the Administrators group (even if this token is the UAC-filtered one). False = a standard account: elevation needs credentials.</summary>
    public static bool IsAdminAccount
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return true;
            using var identity = WindowsIdentity.GetCurrent();
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            return identity.Groups?.Contains(admins) == true;
        }
    }

    /// <summary>
    /// The exe lives on a network share / mapped network drive (e.g. a VM's shared folder). Elevated processes run in a separate logon session
    /// that doesn't see mapped drives, and Windows is unreliable at elevating programs from such locations.
    /// </summary>
    public static bool RunsFromNetworkLocation
    {
        get
        {
            if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } path) return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            try
            {
                return Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch (Exception) { return false; }
        }
    }

    private const string PoliciesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    private static int? ReadPolicy(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.LocalMachine.OpenSubKey(PoliciesKey);
        return key?.GetValue(name) as int?;
    }

    /// <summary>EnableLUA = 0: User Account Control is switched off. Then a standard account has no way to elevate at all.</summary>
    public static bool UacIsOff => ReadPolicy("EnableLUA") == 0;

    /// <summary>ConsentPromptBehaviorUser = 0: "Automatically deny elevation requests" for standard users (no credentials prompt is ever shown).</summary>
    public static bool UacDeniesStandardUsers => ReadPolicy("ConsentPromptBehaviorUser") == 0;

    public static string UacSummary =>
        $"EnableLUA={ReadPolicy("EnableLUA")?.ToString() ?? "-"} ConsentPromptBehaviorUser={ReadPolicy("ConsentPromptBehaviorUser")?.ToString() ?? "-"} " +
        $"ConsentPromptBehaviorAdmin={ReadPolicy("ConsentPromptBehaviorAdmin")?.ToString() ?? "-"}";

    /// <summary>Captured once at start-up: <see cref="TryRelaunch"/> removes the variable from our own environment, and the banner must still explain what it found.</summary>
    public static readonly bool CompatLayerBlocksElevation =
        OperatingSystem.IsWindows() && CompatLayer.ForcesRunAsInvoker(Environment.GetEnvironmentVariable("__COMPAT_LAYER"));

    /// <summary>Starts an elevated copy of this program (shows the UAC prompt). Never throws; the outcome says what happened.</summary>
    public static RelaunchOutcome TryRelaunch(IEnumerable<string> args, bool markAttempted, string verb = "runas")
    {
        if (!OperatingSystem.IsWindows()) return new(RelaunchResult.Failed, "not Windows");
        if (Environment.ProcessPath is not { } exe) return new(RelaunchResult.Failed, "the program path is unknown");

        var pass = args.Where(a => a != AttemptedFlag).ToList();
        // `dotnet NetManager.dll`: the process is dotnet.exe and the program has to be passed to it as the first argument.
        // (Not Assembly.Location: it is empty in single-file apps and trips analyzer IL3000. That case never gets here,
        // because a single-file app's ProcessPath is the exe itself.)
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && Assembly.GetEntryAssembly()?.GetName().Name is { } name
            && Path.Combine(AppContext.BaseDirectory, name + ".dll") is var dll && File.Exists(dll))
            pass.Insert(0, dll);
        if (markAttempted) pass.Add(AttemptedFlag);

        // RunAsInvoker would make Windows start the "elevated" copy un-elevated, without any prompt. Only our own environment is touched.
        var compat = Environment.GetEnvironmentVariable("__COMPAT_LAYER");
        if (CompatLayer.ForcesRunAsInvoker(compat))
            Environment.SetEnvironmentVariable("__COMPAT_LAYER", CompatLayer.WithoutRunAsInvoker(compat));

        Log($"relaunch: exe={exe} args={CommandLine.Join(pass)} compat={compat ?? "-"} verb={verb} network={RunsFromNetworkLocation} adminAccount={IsAdminAccount} uac=[{UacSummary}] user={Environment.UserDomainName}\\{Environment.UserName}");
        try
        {
            Process.Start(new ProcessStartInfo(exe, CommandLine.Join(pass))
            {
                UseShellExecute = true,
                Verb = verb,   // "runas" = elevate this account; "runasuser" = the "Run as different user" credentials dialog
                WorkingDirectory = Environment.CurrentDirectory,
            })?.Dispose();
            Log("relaunch: started");
            return new(RelaunchResult.Started);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED: "No" in the UAC dialog
        {
            Log("relaunch: declined");
            return new(RelaunchResult.Declined);
        }
        catch (Exception ex)
        {
            Log($"relaunch: failed {ex}");
            return new(RelaunchResult.Failed, ex.Message);
        }
    }

    /// <summary>Appends to %APPDATA%\NetManager\elevation.log — the only trace when the UAC prompt never shows up.</summary>
    public static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetManager");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "elevation.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [pid {Environment.ProcessId}, elevated={IsElevated}] {message}{Environment.NewLine}");
        }
        catch (Exception) { /* logging must never break startup */ }
    }
}

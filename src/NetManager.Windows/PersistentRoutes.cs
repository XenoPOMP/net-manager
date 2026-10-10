using Microsoft.Win32;
using NetManager.Core.Models;

namespace NetManager.Windows;

/// <summary>
/// Persistent routes live in HKLM\...\Tcpip\Parameters\PersistentRoutes as value names
/// "destination,netmask,gateway,metric" (exactly what <c>route -p add</c> writes). Windows re-creates them at boot.
/// </summary>
internal static class PersistentRoutes
{
    private const string KeyPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\PersistentRoutes";

    public static HashSet<string> LoadKeys()
    {
        var set = new HashSet<string>();
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        if (key is null) return set;
        foreach (var name in key.GetValueNames())
        {
            var p = name.Split(',');
            if (p.Length >= 3) set.Add(Key(p[0], p[1], p[2]));
        }
        return set;
    }

    public static string Key(string dest, string mask, string? gateway) => $"{dest}|{mask}|{gateway}";

    public static void Add(string dest, int prefix, string gateway, int metric)
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
        key.SetValue($"{dest},{Ipv4Util.PrefixToMask(prefix)},{gateway},{metric}", "", RegistryValueKind.String);
    }

    public static void Remove(string dest, int prefix, string? gateway)
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
        if (key is null) return;
        var mask = Ipv4Util.PrefixToMask(prefix);
        foreach (var name in key.GetValueNames())
        {
            var p = name.Split(',');
            if (p.Length >= 3 && p[0] == dest && p[1] == mask && p[2] == gateway) key.DeleteValue(name, throwOnMissingValue: false);
        }
    }
}

using System.Text;

namespace NetManager.Core.Services;

/// <summary>
/// Builds a Windows command line the way CommandLineToArgvW (and so .NET's own argument parsing) expects it.
/// Needed for relaunching elevated through ShellExecute, which only accepts a single argument string.
/// </summary>
public static class CommandLine
{
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;

        var sb = new StringBuilder("\"");
        for (int i = 0; ; i++)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }

            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2);   // backslashes before the closing quote must be doubled
                break;
            }
            if (arg[i] == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');   // escape the quote and everything before it
            else sb.Append('\\', backslashes).Append(arg[i]);
        }
        return sb.Append('"').ToString();
    }
}

/// <summary>
/// The <c>__COMPAT_LAYER</c> environment variable can contain "RunAsInvoker", which makes Windows ignore every elevation request
/// (manifest <c>requireAdministrator</c> and the "runas" verb alike) — a common leftover of dev tooling. We detect it and clear it before relaunching.
/// </summary>
public static class CompatLayer
{
    public static bool ForcesRunAsInvoker(string? value) =>
        Tokens(value).Any(t => t.Equals("RunAsInvoker", StringComparison.OrdinalIgnoreCase));

    /// <summary>The value without the RunAsInvoker token; null when nothing else is left.</summary>
    public static string? WithoutRunAsInvoker(string? value)
    {
        var rest = Tokens(value).Where(t => !t.Equals("RunAsInvoker", StringComparison.OrdinalIgnoreCase)).ToList();
        return rest.Count == 0 ? null : string.Join(' ', rest);
    }

    private static IEnumerable<string> Tokens(string? value) =>
        (value ?? "").Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
}

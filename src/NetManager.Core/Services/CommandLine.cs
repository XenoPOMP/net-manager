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

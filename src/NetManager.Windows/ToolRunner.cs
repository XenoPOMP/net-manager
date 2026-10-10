using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NetManager.Windows;

/// <summary>Runs a console tool without a shell (arguments are passed as a list, never concatenated) and captures its output.</summary>
internal static class ToolRunner
{
    static ToolRunner() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public readonly record struct Result(int ExitCode, string Output);

    public static Result Run(string exe, IEnumerable<string> args, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding(),
            StandardErrorEncoding = ConsoleEncoding(),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        // Read both streams concurrently so a chatty tool can't fill a pipe and deadlock.
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{Path.GetFileName(exe)} did not finish within {timeoutMs / 1000} s.");
        }
        Task.WaitAll(stdout, stderr);
        return new Result(p.ExitCode, (stdout.Result + Environment.NewLine + stderr.Result).Trim());
    }

    /// <summary>Console tools write in the OEM code page (cp866 on Russian Windows); decoding as UTF-8 would garble the message.</summary>
    private static Encoding ConsoleEncoding()
    {
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.UTF8; }
    }
}

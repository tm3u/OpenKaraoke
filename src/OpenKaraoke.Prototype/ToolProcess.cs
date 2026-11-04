using System.Diagnostics;
using System.IO;
using System.Text;

namespace OpenKaraoke.Prototype;

internal static class ToolProcess
{
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken cancellation, Action<string>? output = null, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (workingDirectory != null) start.WorkingDirectory = workingDirectory;
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("無法啟動處理工具。");
        using var registration = cancellation.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        });
        var stdout = new StringBuilder(); var stderr = new StringBuilder();
        async Task Read(StreamReader reader, StringBuilder text)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (text.Length < 1_000_000) text.AppendLine(line);
                output?.Invoke(line);
            }
        }
        await Task.WhenAll(Read(process.StandardOutput, stdout), Read(process.StandardError, stderr), process.WaitForExitAsync());
        cancellation.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            string detail = stderr.Length > 0 ? stderr.ToString() : stdout.ToString();
            throw new IOException("處理失敗：" + detail[^Math.Min(detail.Length, 2000)..]);
        }
        return stdout.ToString();
    }
}

using System.Diagnostics;
using System.Text;

namespace PakAssetStudio.Services;

public sealed record ProcessResult(int ExitCode, string Output);

public sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var outputLock = new object();

        void Receive(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (outputLock)
            {
                output.AppendLine(line);
            }
            onLine?.Invoke(line);
        }

        process.OutputDataReceived += (_, e) => Receive(e.Data);
        process.ErrorDataReceived += (_, e) => Receive(e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动：{executable}");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have exited between the checks.
            }
        });

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        lock (outputLock)
        {
            return new ProcessResult(process.ExitCode, output.ToString());
        }
    }
}

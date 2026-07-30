using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PakAssetStudio.Services;

public sealed record ProcessResult(int ExitCode, string Output);

public sealed class ProcessLaunchException : InvalidOperationException
{
    public ProcessLaunchException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onLine,
        CancellationToken cancellationToken,
        ProcessPriorityClass? priority = null,
        bool captureOutput = true);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onLine,
        CancellationToken cancellationToken,
        ProcessPriorityClass? priority = null,
        bool captureOutput = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
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
            if (captureOutput)
            {
                lock (outputLock)
                {
                    output.AppendLine(line);
                }
            }
            onLine?.Invoke(line);
        }

        process.OutputDataReceived += (_, e) => Receive(e.Data);
        process.ErrorDataReceived += (_, e) => Receive(e.Data);

        try
        {
            if (!process.Start())
                throw new ProcessLaunchException(LocalizationService.TextFormat("Error_ProcessStart", executable));
        }
        catch (ProcessLaunchException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new ProcessLaunchException(LocalizationService.TextFormat("Error_ProcessStart", executable), ex);
        }
        if (priority.HasValue)
        {
            try
            {
                process.PriorityClass = priority.Value;
            }
            catch
            {
                // 进程可能已快速退出或系统拒绝调整优先级，忽略。
            }
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

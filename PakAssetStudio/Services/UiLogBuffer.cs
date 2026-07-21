using System.Collections.Concurrent;

namespace PakAssetStudio.Services;

public enum UiLogLevel
{
    Info,
    Stage,
    Success,
    Warning,
    Error
}

public sealed record UiLogLine(UiLogLevel Level, string Text);

public sealed record UiLogBatch(IReadOnlyList<UiLogLine> Lines, int Dropped, int Remaining);

public sealed class UiLogBuffer
{
    private readonly ConcurrentQueue<UiLogLine> _lines = new();

    public int Count => _lines.Count;

    public void Enqueue(string line, UiLogLevel level = UiLogLevel.Info) => _lines.Enqueue(new UiLogLine(level, line));

    public void Clear()
    {
        while (_lines.TryDequeue(out _)) { }
    }

    public UiLogBatch Drain(
        int maximumBacklog = 5_000,
        int retainedBacklog = 1_000,
        int batchSize = 400)
    {
        var dropped = 0;
        var pending = _lines.Count;
        if (pending > maximumBacklog)
        {
            var toDrop = pending - retainedBacklog;
            while (dropped < toDrop && _lines.TryDequeue(out _)) dropped++;
        }

        var lines = new List<UiLogLine>();
        if (dropped > 0)
            lines.Add(new UiLogLine(UiLogLevel.Warning, $"[界面日志已省略 {dropped:N0} 行；完整内容保存在 PakAssetStudio.log]"));
        for (var index = 0; index < batchSize && _lines.TryDequeue(out var line); index++)
            lines.Add(line);
        return new UiLogBatch(lines, dropped, _lines.Count);
    }
}

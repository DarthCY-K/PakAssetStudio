using System.Collections.Concurrent;
using System.Text;

namespace PakAssetStudio.Services;

public sealed record UiLogBatch(string Text, int Dropped, int Remaining);

public sealed class UiLogBuffer
{
    private readonly ConcurrentQueue<string> _lines = new();

    public int Count => _lines.Count;

    public void Enqueue(string line) => _lines.Enqueue(line);

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

        var builder = new StringBuilder();
        if (dropped > 0)
            builder.AppendLine($"[界面日志已省略 {dropped:N0} 行；完整内容保存在 PakAssetStudio.log]");
        for (var index = 0; index < batchSize && _lines.TryDequeue(out var line); index++)
            builder.AppendLine(line);
        return new UiLogBatch(builder.ToString(), dropped, _lines.Count);
    }
}

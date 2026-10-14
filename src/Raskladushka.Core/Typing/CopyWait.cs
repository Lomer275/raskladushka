namespace Raskladushka.Core.Typing;

public static class CopyWait
{
    public static CopyWaitResult Run(Func<uint> sequence, uint before, CopyWaitOptions options,
        TimeProvider time, Action<int> sleep)
    {
        var start = time.GetTimestamp();
        long ElapsedMs() => time.GetElapsedTime(start).Ticks / TimeSpan.TicksPerMillisecond;

        uint current;
        long firstMs;
        while (true)
        {
            current = sequence();
            firstMs = ElapsedMs();
            if (current != before) break;
            if (firstMs >= options.FirstMs)
                return new(CopyWaitStatus.NothingCopied, -1, -1, 0, ElapsedMs());
            sleep(options.PollMs);
        }

        var lastMs = firstMs;
        var changes = 1;
        while (true)
        {
            // A slow copy suggests the repeated copy through VS Code may take comparable time.
            var quiet = Math.Max(options.QuietMs, firstMs);
            var elapsed = ElapsedMs();
            if (elapsed - lastMs >= quiet)
                return new(CopyWaitStatus.Copied, firstMs, lastMs, changes, ElapsedMs());
            if (elapsed - firstMs >= options.SettleCapMs)
                return new(CopyWaitStatus.Busy, firstMs, lastMs, changes, ElapsedMs());
            sleep(options.PollMs);
            var next = sequence();
            if (next != current)
            {
                changes++;
                lastMs = ElapsedMs();
                current = next;
            }
        }
    }

    public static int PasteHoldMs(long lastCopyMs, int minMs, int maxMs) =>
        lastCopyMs < 0 ? minMs : (int)Math.Clamp(3 * Math.Min(lastCopyMs, int.MaxValue), minMs, maxMs);
}

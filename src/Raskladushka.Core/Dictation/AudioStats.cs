namespace Raskladushka.Core.Dictation;

public static class AudioStats
{
    public static double Rms(ReadOnlySpan<short> pcm16)
    {
        if (pcm16.IsEmpty) return 0;
        double sum = 0;
        foreach (var sample in pcm16)
        {
            var normalized = sample / 32768.0;
            sum += normalized * normalized;
        }
        return Math.Sqrt(sum / pcm16.Length);
    }
}

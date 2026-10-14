namespace Raskladushka.Core.Typing;

public sealed record DetectorOptions
{
    public double FrequencyRatio { get; init; } = 100;
    public double CommonEdgePenalty { get; init; } = 0.2;
    public double RareEdgePenalty { get; init; } = 0.01;
    public int MinWordLength { get; init; } = 3;
    public double WeakPercentile { get; init; } = 5;
    public double StrongPercentile { get; init; } = 25;
}

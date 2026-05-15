namespace FastClip.Models;

internal sealed record CompressionEstimateResult(string DisplayText)
{
    public static CompressionEstimateResult FromBytes(long bytes, bool usedFallback)
    {
        var kiloBytes = Math.Max(1, (int)Math.Round(bytes / 1024d));
        return new CompressionEstimateResult(
            usedFallback
                ? $"Estimated output size: {kiloBytes} KB (standard estimate)"
                : $"Estimated output size: {kiloBytes} KB");
    }

    public static CompressionEstimateResult Unavailable()
    {
        return new CompressionEstimateResult("Estimated output size: unavailable");
    }
}

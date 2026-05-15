namespace FastClip.Models;

internal sealed record OxipngOptimizeResult(bool Success, string? ErrorMessage)
{
    public static OxipngOptimizeResult Succeeded() => new(true, null);
    public static OxipngOptimizeResult Failed(string? errorMessage) => new(false, errorMessage);
}

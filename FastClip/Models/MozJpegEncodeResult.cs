namespace FastClip.Models;

internal sealed record MozJpegEncodeResult(bool Success, string? ErrorMessage)
{
    public static MozJpegEncodeResult Succeeded() => new(true, null);
    public static MozJpegEncodeResult Failed(string? errorMessage) => new(false, errorMessage);
}

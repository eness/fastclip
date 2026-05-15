namespace FastClip.Models;

internal sealed class ResizeOptions
{
    public required int OriginalWidth { get; init; }
    public required int OriginalHeight { get; init; }
    public required int Width { get; set; }
    public required int Height { get; set; }
    public required bool KeepAspectRatio { get; set; }
    public int? ScalePercent { get; set; }
    public bool IsResizeActive => Width != OriginalWidth || Height != OriginalHeight;
}

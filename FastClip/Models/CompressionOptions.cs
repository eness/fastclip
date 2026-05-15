namespace FastClip.Models;

internal sealed class CompressionOptions
{
    public required bool Enabled { get; set; }
    public required bool AvailableForCurrentTarget { get; set; }
    public required CompressionTargetFormat TargetFormat { get; set; }
    public required int JpegQuality { get; set; }
    public required int PngOptimizationLevel { get; set; }
}

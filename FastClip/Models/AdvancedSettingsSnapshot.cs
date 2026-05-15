namespace FastClip.Models;

internal sealed record AdvancedSettingsSnapshot(
    bool KeepAspectRatio,
    int? ScalePercent,
    double WidthRatio,
    double HeightRatio,
    int JpegQuality,
    int PngOptimizationLevel,
    string OutputExtension)
{
    public static AdvancedSettingsSnapshot FromPasteOptions(PasteOptions options)
    {
        return new AdvancedSettingsSnapshot(
            options.Resize.KeepAspectRatio,
            options.Resize.ScalePercent,
            options.Resize.Width / (double)Math.Max(1, options.Resize.OriginalWidth),
            options.Resize.Height / (double)Math.Max(1, options.Resize.OriginalHeight),
            options.Compression.JpegQuality,
            options.Compression.PngOptimizationLevel,
            options.OutputExtension);
    }
}

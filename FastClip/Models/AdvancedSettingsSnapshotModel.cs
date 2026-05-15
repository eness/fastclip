namespace FastClip.Models;

internal sealed class AdvancedSettingsSnapshotModel
{
    public bool KeepAspectRatio { get; set; }
    public int? ScalePercent { get; set; }
    public double WidthRatio { get; set; }
    public double HeightRatio { get; set; }
    public int JpegQuality { get; set; }
    public int PngOptimizationLevel { get; set; }
    public string OutputExtension { get; set; } = ".jpg";
}

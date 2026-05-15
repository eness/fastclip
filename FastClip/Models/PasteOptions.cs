namespace FastClip.Models;

internal sealed class PasteOptions
{
    public required string OutputExtension { get; set; }
    public required ResizeOptions Resize { get; set; }
    public required CompressionOptions Compression { get; set; }
}

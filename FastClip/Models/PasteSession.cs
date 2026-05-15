using System.Drawing;

namespace FastClip.Models;

internal sealed class PasteSession
{
    private static readonly string[] SupportedNewFileExtensions = [".jpg", ".png", ".bmp", ".gif", ".tif"];

    public static PasteSession Create(Image image, ExplorerContext explorerContext)
    {
        var targetKind = !string.IsNullOrWhiteSpace(explorerContext.SelectedFilePath)
            ? PasteTargetKind.ExistingFile
            : string.IsNullOrWhiteSpace(explorerContext.CurrentFolderPath)
                ? PasteTargetKind.None
                : PasteTargetKind.NewFile;
        var outputExtension = targetKind == PasteTargetKind.ExistingFile
            ? Path.GetExtension(explorerContext.SelectedFilePath!).ToLowerInvariant()
            : targetKind == PasteTargetKind.NewFile
                ? ".jpg"
                : string.Empty;
        var compressionFormat = outputExtension switch
        {
            ".jpg" or ".jpeg" => CompressionTargetFormat.Jpeg,
            ".png" => CompressionTargetFormat.Png,
            _ => CompressionTargetFormat.None
        };
        var compressionAvailable = compressionFormat != CompressionTargetFormat.None;

        return new PasteSession
        {
            SourceImage = image,
            ExplorerContext = explorerContext,
            TargetKind = targetKind,
            OutputExtension = outputExtension,
            Options = new PasteOptions
            {
                OutputExtension = outputExtension,
                Resize = new ResizeOptions
                {
                    OriginalWidth = image.Width,
                    OriginalHeight = image.Height,
                    Width = image.Width,
                    Height = image.Height,
                    KeepAspectRatio = true
                },
                Compression = new CompressionOptions
                {
                    Enabled = false,
                    AvailableForCurrentTarget = compressionAvailable,
                    TargetFormat = compressionFormat,
                    JpegQuality = 85,
                    PngOptimizationLevel = 4
                }
            }
        };
    }

    public static bool IsSupportedNewFileExtension(string extension)
    {
        return SupportedNewFileExtensions.Contains(NormalizeExtension(extension), StringComparer.OrdinalIgnoreCase);
    }

    public void SetOutputExtension(string extension)
    {
        var normalizedExtension = NormalizeExtension(extension);
        if (TargetKind == PasteTargetKind.ExistingFile || !IsSupportedNewFileExtension(normalizedExtension))
        {
            return;
        }

        OutputExtension = normalizedExtension;
        Options.OutputExtension = normalizedExtension;

        var compressionFormat = normalizedExtension switch
        {
            ".jpg" or ".jpeg" => CompressionTargetFormat.Jpeg,
            ".png" => CompressionTargetFormat.Png,
            _ => CompressionTargetFormat.None
        };

        Options.Compression.AvailableForCurrentTarget = compressionFormat != CompressionTargetFormat.None;
        Options.Compression.TargetFormat = compressionFormat;
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var normalizedExtension = extension.StartsWith('.') ? extension : $".{extension}";
        return normalizedExtension.ToLowerInvariant();
    }

    public required Image SourceImage { get; init; }
    public required ExplorerContext ExplorerContext { get; init; }
    public required PasteTargetKind TargetKind { get; init; }
    public string OutputExtension { get; private set; } = string.Empty;
    public required PasteOptions Options { get; init; }
}

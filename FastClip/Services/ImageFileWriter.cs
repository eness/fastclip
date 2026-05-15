using System.Drawing;
using System.Drawing.Imaging;
using FastClip.Models;

namespace FastClip.Services;

internal sealed class ImageFileWriter : IImageFileWriter
{
    private const long JpegQuality = 95L;
    private readonly MozJpegEncoder _mozJpegEncoder;
    private readonly OxipngEncoder _oxipngEncoder;

    public ImageFileWriter()
        : this(new MozJpegEncoder(), new OxipngEncoder())
    {
    }

    public ImageFileWriter(MozJpegEncoder mozJpegEncoder, OxipngEncoder oxipngEncoder)
    {
        _mozJpegEncoder = mozJpegEncoder;
        _oxipngEncoder = oxipngEncoder;
    }

    public ImageSaveResult ReplaceImageFile(Image image, string targetPath, PasteOptions options)
    {
        ValidateTargetFile(targetPath);

        var directory = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("Target directory could not be determined.");
        var extension = Path.GetExtension(targetPath).ToLowerInvariant();
        var tempPath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(targetPath)}.{Guid.NewGuid():N}{extension}");
        string? warningMessage = null;

        try
        {
            warningMessage = SaveImage(image, tempPath, extension, options);
            EnsureGeneratedFileLooksValid(tempPath);

            try
            {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            catch (PlatformNotSupportedException)
            {
                ReplaceByMove(tempPath, targetPath);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                ReplaceByMove(tempPath, targetPath);
            }
            catch (UnauthorizedAccessException) when (File.Exists(targetPath))
            {
                ReplaceByMove(tempPath, targetPath);
            }
        }
        finally
        {
            TryDeleteIfExists(tempPath);
        }

        return new ImageSaveResult(targetPath, warningMessage);
    }

    public ImageSaveResult CreateNewImage(Image image, string folderPath, string extension, PasteOptions options)
    {
        ValidateTargetDirectory(folderPath);
        extension = NormalizeExtension(extension);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new NotSupportedException("Only png, jpg, jpeg, bmp, gif, tif, and tiff files are supported.");
        }

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidatePath = Path.Combine(folderPath, $"{GenerateRandomName()}{extension}");

            try
            {
                var warningMessage = SaveImage(image, candidatePath, extension, options);
                EnsureGeneratedFileLooksValid(candidatePath);
                return new ImageSaveResult(candidatePath, warningMessage);
            }
            catch (IOException) when (File.Exists(candidatePath))
            {
            }
        }

        throw new IOException("A unique file name could not be generated in the current folder.");
    }

    private string? SaveImage(Image image, string targetPath, string extension, PasteOptions options)
    {
        ValidateImage(image);

        switch (extension)
        {
            case ".png":
                return SavePng(image, targetPath, options);
            case ".bmp":
                image.Save(targetPath, ImageFormat.Bmp);
                return null;
            case ".gif":
                image.Save(targetPath, ImageFormat.Gif);
                return null;
            case ".tif":
            case ".tiff":
                image.Save(targetPath, ImageFormat.Tiff);
                return null;
            case ".jpg":
            case ".jpeg":
                return SaveJpeg(image, targetPath, options);
            default:
                throw new NotSupportedException("Only png, jpg, jpeg, bmp, gif, tif, and tiff files are supported.");
        }
    }

    private string? SaveJpeg(Image image, string targetPath, PasteOptions options)
    {
        var quality = options.Compression.Enabled && options.Compression.AvailableForCurrentTarget
            ? options.Compression.JpegQuality
            : (int)JpegQuality;

        if (options.Compression.Enabled && options.Compression.AvailableForCurrentTarget)
        {
            var mozJpegResult = _mozJpegEncoder.TryEncode(image, targetPath, quality);
            if (mozJpegResult.Success)
            {
                return null;
            }
        }

        TryDeleteIfExists(targetPath);
        using var stream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        SaveJpeg(image, stream, quality);
        return options.Compression.Enabled && options.Compression.AvailableForCurrentTarget
            ? "mozjpeg unavailable, standard JPEG fallback used"
            : null;
    }

    private string? SavePng(Image image, string targetPath, PasteOptions options)
    {
        image.Save(targetPath, ImageFormat.Png);

        if (!options.Compression.Enabled ||
            !options.Compression.AvailableForCurrentTarget ||
            options.Compression.TargetFormat != CompressionTargetFormat.Png)
        {
            return null;
        }

        var result = _oxipngEncoder.TryOptimizeFile(targetPath, options.Compression.PngOptimizationLevel);
        return result.Success ? null : "oxipng unavailable, standard PNG fallback used";
    }

    private static void ValidateTargetFile(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("No target file was selected.", nameof(targetPath));
        }

        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("The selected file no longer exists.", targetPath);
        }

        var extension = Path.GetExtension(targetPath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            throw new NotSupportedException("Only png, jpg, jpeg, bmp, gif, tif, and tiff files are supported.");
        }

        var attributes = File.GetAttributes(targetPath);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            throw new UnauthorizedAccessException("The selected file is read-only.");
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The selected file is a reparse point and cannot be replaced safely.");
        }

        ValidateTargetDirectory(Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("Target directory could not be determined."));

        try
        {
            using var stream = new FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException("The selected file is currently in use by another application.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException("The selected file cannot be written.", ex);
        }
    }

    private static void ValidateTargetDirectory(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException("The current Explorer folder is not available.");
        }

        var probePath = Path.Combine(folderPath, $".write-test-{Guid.NewGuid():N}.tmp");

        try
        {
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(probePath);
        }
        catch (IOException ex)
        {
            throw new IOException("The current Explorer folder cannot be written.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException("The current Explorer folder cannot be written.", ex);
        }
        finally
        {
            TryDeleteIfExists(probePath);
        }
    }

    private static void ValidateImage(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new InvalidOperationException("The clipboard image has invalid dimensions.");
        }
    }

    private static void EnsureGeneratedFileLooksValid(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            throw new IOException("The generated image file is empty.");
        }
    }

    private static void ReplaceByMove(string tempPath, string targetPath)
    {
        var backupPath = $"{targetPath}.{Guid.NewGuid():N}.rollback";

        try
        {
            File.Move(targetPath, backupPath, overwrite: true);
            File.Move(tempPath, targetPath, overwrite: true);
            TryDeleteIfExists(backupPath);
        }
        catch
        {
            if (!File.Exists(targetPath) && File.Exists(backupPath))
            {
                File.Move(backupPath, targetPath, overwrite: true);
            }

            throw;
        }
        finally
        {
            TryDeleteIfExists(backupPath);
        }
    }

    private static void SaveJpeg(Image image, Stream targetStream, long quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

        if (encoder is null)
        {
            image.Save(targetStream, ImageFormat.Jpeg);
            return;
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        image.Save(targetStream, encoder, parameters);
    }

    private static string GenerateRandomName()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz";
        Span<char> buffer = stackalloc char[10];

        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        }

        return new string(buffer);
    }

    private static void TryDeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".gif",
        ".tif",
        ".tiff"
    };

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        return extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}";
    }
}

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using FastClip.Models;

namespace FastClip.Services;

internal sealed class MozJpegEncoder
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public MozJpegEncodeResult TryEncode(Image image, string outputPath, int quality)
    {
        var executablePath = MozJpegLocator.TryFindEncoder();
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return MozJpegEncodeResult.Failed("mozjpeg executable not found.");
        }

        var tempInputPath = Path.Combine(Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory, $"{Guid.NewGuid():N}.mozjpeg-input.png");

        try
        {
            image.Save(tempInputPath, ImageFormat.Png);

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"-quality {Math.Clamp(quality, 0, 100)} -outfile \"{outputPath}\" \"{tempInputPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return MozJpegEncodeResult.Failed("mozjpeg process could not be started.");
            }

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                TryKill(process);
                return MozJpegEncodeResult.Failed("mozjpeg process timed out.");
            }

            var standardError = process.StandardError.ReadToEnd();
            var standardOutput = process.StandardOutput.ReadToEnd();
            if (process.ExitCode != 0)
            {
                return MozJpegEncodeResult.Failed(string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError);
            }

            return File.Exists(outputPath)
                ? MozJpegEncodeResult.Succeeded()
                : MozJpegEncodeResult.Failed("mozjpeg did not create the output file.");
        }
        catch (Exception ex)
        {
            return MozJpegEncodeResult.Failed(ex.Message);
        }
        finally
        {
            TryDeleteFile(tempInputPath);
        }
    }

    public CompressionEstimateResult EstimateSize(Image image, int quality)
    {
        var tempOutputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");

        try
        {
            var encodeResult = TryEncode(image, tempOutputPath, quality);
            if (encodeResult.Success && File.Exists(tempOutputPath))
            {
                return CompressionEstimateResult.FromBytes(new FileInfo(tempOutputPath).Length, usedFallback: false);
            }

            using var stream = new MemoryStream();
            SaveStandardJpeg(image, stream, quality);
            return CompressionEstimateResult.FromBytes(stream.Length, usedFallback: true);
        }
        finally
        {
            TryDeleteFile(tempOutputPath);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
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

    private static void SaveStandardJpeg(Image image, Stream targetStream, long quality)
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
}

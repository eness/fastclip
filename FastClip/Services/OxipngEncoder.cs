using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using FastClip.Models;

namespace FastClip.Services;

internal sealed class OxipngEncoder
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public OxipngOptimizeResult TryOptimizeFile(string filePath, int optimizationLevel)
    {
        var executablePath = OxipngLocator.TryFindEncoder();
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return OxipngOptimizeResult.Failed("oxipng executable not found.");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"-o {Math.Clamp(optimizationLevel, 0, 6)} --strip safe --alpha -q \"{filePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return OxipngOptimizeResult.Failed("oxipng process could not be started.");
            }

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                TryKill(process);
                return OxipngOptimizeResult.Failed("oxipng process timed out.");
            }

            var standardError = process.StandardError.ReadToEnd();
            var standardOutput = process.StandardOutput.ReadToEnd();
            if (process.ExitCode != 0)
            {
                return OxipngOptimizeResult.Failed(string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError);
            }

            return File.Exists(filePath)
                ? OxipngOptimizeResult.Succeeded()
                : OxipngOptimizeResult.Failed("oxipng did not preserve the output file.");
        }
        catch (Exception ex)
        {
            return OxipngOptimizeResult.Failed(ex.Message);
        }
    }

    public CompressionEstimateResult EstimateSize(Image image, int optimizationLevel)
    {
        var tempOutputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");

        try
        {
            image.Save(tempOutputPath, ImageFormat.Png);
            var standardSize = new FileInfo(tempOutputPath).Length;
            var optimizeResult = TryOptimizeFile(tempOutputPath, optimizationLevel);
            if (optimizeResult.Success && File.Exists(tempOutputPath))
            {
                return CompressionEstimateResult.FromBytes(new FileInfo(tempOutputPath).Length, usedFallback: false);
            }

            return CompressionEstimateResult.FromBytes(standardSize, usedFallback: true);
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
}

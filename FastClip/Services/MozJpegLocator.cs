namespace FastClip.Services;

internal static class MozJpegLocator
{
    public static string? TryFindEncoder()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "Tools", "mozjpeg", "win-x64", "cjpeg-static.exe"),
            Path.Combine(baseDirectory, "cjpeg-static.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}

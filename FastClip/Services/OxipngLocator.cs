namespace FastClip.Services;

internal static class OxipngLocator
{
    public static string? TryFindEncoder()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "Tools", "oxipng", "win-x64", "oxipng.exe"),
            Path.Combine(baseDirectory, "oxipng.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}

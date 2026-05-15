namespace FastClip.Models;

internal sealed record ExplorerContext(string? SelectedFilePath, string? CurrentFolderPath)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(SelectedFilePath) && string.IsNullOrWhiteSpace(CurrentFolderPath);
    public static ExplorerContext Empty { get; } = new(null, null);
}

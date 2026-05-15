namespace FastClip.Models;

internal sealed record FormatOption(string Extension, string Label)
{
    public override string ToString() => Label;
}

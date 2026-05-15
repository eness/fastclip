using FastClip.Interop;

namespace FastClip.Models;

internal readonly record struct HotkeyRegistration(int Id, uint Modifiers, Keys Key)
{
    public static HotkeyRegistration Default { get; } = new(0x2401, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, Keys.V);

    public string ToDisplayString()
    {
        var parts = new List<string>();

        if ((Modifiers & NativeMethods.MOD_CONTROL) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & NativeMethods.MOD_SHIFT) != 0)
        {
            parts.Add("Shift");
        }

        if ((Modifiers & NativeMethods.MOD_ALT) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & NativeMethods.MOD_WIN) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(Key.ToString().ToUpperInvariant());
        return string.Join(" + ", parts);
    }
}

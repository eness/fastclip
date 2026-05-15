using System.Drawing;

namespace FastClip.Models;

internal sealed record ClipboardReadResult(bool IsSuccess, Bitmap? Image, string Message)
{
    public static ClipboardReadResult Success(Bitmap image) => new(true, image, string.Empty);
    public static ClipboardReadResult Empty(string message) => new(false, null, message);
    public static ClipboardReadResult Busy(string message) => new(false, null, message);
    public static ClipboardReadResult Unsupported(string message) => new(false, null, message);
}

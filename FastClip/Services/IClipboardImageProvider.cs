using FastClip.Models;

namespace FastClip.Services;

internal interface IClipboardImageProvider
{
    Task<ClipboardReadResult> TryCaptureAsync(CancellationToken cancellationToken);
}

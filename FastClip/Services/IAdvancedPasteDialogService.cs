using FastClip.Models;

namespace FastClip.Services;

internal interface IAdvancedPasteDialogService
{
    Task<AdvancedPasteDialogResult> ShowAsync(PasteSession session, CancellationToken cancellationToken);
}

using System.Threading;
using System.Windows.Forms;
using FastClip.Forms;
using FastClip.Models;

namespace FastClip.Services;

internal sealed class AdvancedPasteDialogService : IAdvancedPasteDialogService
{
    private readonly SynchronizationContext _uiContext;

    public AdvancedPasteDialogService(SynchronizationContext uiContext)
    {
        _uiContext = uiContext;
    }

    public Task<AdvancedPasteDialogResult> ShowAsync(PasteSession session, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<AdvancedPasteDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        _uiContext.Post(_ =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                using var dialog = new AdvancedPasteForm(session);
                tcs.TrySetResult(new AdvancedPasteDialogResult(
                    dialog.ShowDialog() == DialogResult.OK,
                    dialog.AutoApplyNextTime));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, null);

        return tcs.Task;
    }
}

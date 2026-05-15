using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using FastClip.Interop;
using FastClip.Models;

namespace FastClip.Services;

internal sealed class ClipboardImageProvider : IClipboardImageProvider
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan ClipboardReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PhotoshopWarmupDelay = TimeSpan.FromMilliseconds(900);
    private const int MaxAttempts = 8;

    public async Task<ClipboardReadResult> TryCaptureAsync(CancellationToken cancellationToken)
    {
        var owner = GetClipboardOwnerProfile();
        var initialDelay = owner.RequiresWarmup
            ? TimeSpan.FromMilliseconds(Math.Max(InitialDelay.TotalMilliseconds, PhotoshopWarmupDelay.TotalMilliseconds))
            : InitialDelay;

        await Task.Delay(initialDelay, cancellationToken).ConfigureAwait(false);
        return await CaptureWithRetriesAsync(owner, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ClipboardReadResult> CaptureWithRetriesAsync(ClipboardOwnerProfile owner, CancellationToken cancellationToken)
    {
        var delay = AttemptDelay;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await RunStaAsync(CaptureAttemptCore, cancellationToken)
                    .WaitAsync(ClipboardReadTimeout, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Status != ClipboardCaptureStatus.Busy || attempt == MaxAttempts)
                {
                    return ToClipboardReadResult(result, owner);
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (ExternalException) when (attempt < MaxAttempts)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (COMException) when (attempt < MaxAttempts)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return ClipboardReadResult.Busy(owner.TimeoutMessage);
            }

            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.6d, MaxDelay.TotalMilliseconds));
        }

        return ClipboardReadResult.Busy(owner.BusyMessage);
    }

    private static ClipboardCaptureResult CaptureAttemptCore()
    {
        var dataObject = Clipboard.GetDataObject();
        if (dataObject is null)
        {
            return ClipboardCaptureResult.Busy();
        }

        var rawFormats = dataObject.GetFormats(autoConvert: false);
        if (rawFormats.Length == 0)
        {
            return ClipboardCaptureResult.Busy();
        }

        if (!ContainsImageFormat(rawFormats) && !HasNativeImageFormat())
        {
            return ClipboardCaptureResult.Empty();
        }

        if (dataObject.GetDataPresent(DataFormats.Bitmap, autoConvert: false) &&
            dataObject.GetData(DataFormats.Bitmap, autoConvert: false) is Image directBitmap)
        {
            return ClipboardCaptureResult.Success(CloneBitmap(directBitmap));
        }

        if (dataObject.GetDataPresent(DataFormats.Bitmap) &&
            dataObject.GetData(DataFormats.Bitmap) is Image autoConvertedBitmap)
        {
            return ClipboardCaptureResult.Success(CloneBitmap(autoConvertedBitmap));
        }

        if (Clipboard.ContainsImage() && Clipboard.GetImage() is Image clipboardImage)
        {
            using (clipboardImage)
            {
                return ClipboardCaptureResult.Success(CloneBitmap(clipboardImage));
            }
        }

        return ClipboardCaptureResult.Unsupported();
    }

    private static Bitmap CloneBitmap(Image image)
    {
        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new InvalidOperationException("The clipboard image has invalid dimensions.");
        }

        return new Bitmap(image);
    }

    private static bool ContainsImageFormat(IEnumerable<string> formats)
    {
        foreach (var format in formats)
        {
            if (string.Equals(format, DataFormats.Bitmap, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(format, "PNG", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(format, "DeviceIndependentBitmap", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(format, "DeviceIndependentBitmapV5", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasNativeImageFormat()
    {
        return NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_BITMAP) ||
               NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB) ||
               NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIBV5);
    }

    private static ClipboardReadResult ToClipboardReadResult(ClipboardCaptureResult result, ClipboardOwnerProfile owner)
    {
        return result.Status switch
        {
            ClipboardCaptureStatus.Success when result.Image is not null => ClipboardReadResult.Success(result.Image),
            ClipboardCaptureStatus.Empty => ClipboardReadResult.Empty("No image exists in the clipboard."),
            ClipboardCaptureStatus.Unsupported => ClipboardReadResult.Unsupported("The clipboard contains image data in an unsupported format."),
            _ => ClipboardReadResult.Busy(owner.BusyMessage)
        };
    }

    private static ClipboardOwnerProfile GetClipboardOwnerProfile()
    {
        try
        {
            var ownerHandle = NativeMethods.GetClipboardOwner();
            if (ownerHandle == IntPtr.Zero)
            {
                return ClipboardOwnerProfile.Unknown;
            }

            NativeMethods.GetWindowThreadProcessId(ownerHandle, out var processId);
            if (processId == 0)
            {
                return ClipboardOwnerProfile.Unknown;
            }

            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName;

            if (string.Equals(processName, "Photoshop", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, "Adobe Photoshop", StringComparison.OrdinalIgnoreCase))
            {
                return ClipboardOwnerProfile.Photoshop;
            }

            return new ClipboardOwnerProfile(
                processName,
                false,
                "The clipboard owner is still preparing the image data.",
                "The clipboard owner did not finish rendering the image in time.");
        }
        catch
        {
            return ClipboardOwnerProfile.Unknown;
        }
    }

    private static Task<T> RunStaAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                tcs.TrySetResult(action());
            }
            catch (OperationCanceledException ex)
            {
                tcs.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private enum ClipboardCaptureStatus
    {
        Success,
        Busy,
        Empty,
        Unsupported
    }

    private sealed record ClipboardCaptureResult(ClipboardCaptureStatus Status, Bitmap? Image)
    {
        public static ClipboardCaptureResult Success(Bitmap image) => new(ClipboardCaptureStatus.Success, image);
        public static ClipboardCaptureResult Busy() => new(ClipboardCaptureStatus.Busy, null);
        public static ClipboardCaptureResult Empty() => new(ClipboardCaptureStatus.Empty, null);
        public static ClipboardCaptureResult Unsupported() => new(ClipboardCaptureStatus.Unsupported, null);
    }

    private sealed record ClipboardOwnerProfile(
        string? ProcessName,
        bool RequiresWarmup,
        string BusyMessage,
        string TimeoutMessage)
    {
        public static ClipboardOwnerProfile Photoshop { get; } = new(
            "Photoshop",
            true,
            "Photoshop is still preparing the copied image. Wait a moment and try again.",
            "Photoshop did not finish rendering the copied image in time. Wait a moment and try again.");

        public static ClipboardOwnerProfile Unknown { get; } = new(
            null,
            false,
            "The clipboard owner is still preparing the image data.",
            "The clipboard owner did not finish rendering the image in time.");
    }
}

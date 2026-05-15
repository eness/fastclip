using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ClipboardToSelectedFile;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        Application.ThreadException += (_, args) => GlobalErrorHandler.Handle("ui-thread", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            GlobalErrorHandler.Handle("appdomain", args.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception."));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            GlobalErrorHandler.Handle("task-scheduler", args.Exception);
            args.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int HotkeyId = 0x2401;
    private static readonly TimeSpan OperationCooldown = TimeSpan.FromMilliseconds(150);
    private readonly NotifyIcon _notifyIcon;
    private readonly HotkeyWindow _window;
    private readonly SynchronizationContext _uiContext;
    private readonly ClipboardImageProvider _clipboardImageProvider = new();
    private readonly ExplorerContextResolver _explorerContextResolver = new();
    private readonly ImageFileWriter _imageFileWriter = new();
    private readonly OperationCoordinator _operationCoordinator = new();
    private readonly ErrorLogger _errorLogger = new();
    private volatile bool _isExiting;

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Clipboard To Selected File",
            ContextMenuStrip = BuildMenu()
        };

        _window = new HotkeyWindow(TriggerHotkeyOperation);
        if (!NativeMethods.RegisterHotKey(_window.Handle, HotkeyId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, (uint)Keys.V))
        {
            ShowBalloon("Hotkey registration failed", "Ctrl+Shift+V is already in use by another application.");
        }
        else
        {
            ShowBalloon("Ready", "Press Ctrl+Shift+V to replace the selected Explorer image file with the clipboard image.");
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("How to use", null, (_, _) =>
            ShowBalloon("Usage", "Copy an image to the clipboard, select a file in Explorer, then press Ctrl+Shift+V."));
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private void TriggerHotkeyOperation()
    {
        if (!_operationCoordinator.TryBegin())
        {
            ShowBalloon("Busy", "A clipboard save is already in progress.");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(OperationCooldown).ConfigureAwait(false);
                await ExecuteHotkeyOperationAsync().ConfigureAwait(false);
            }
            finally
            {
                _operationCoordinator.End();
            }
        });
    }

    private async Task ExecuteHotkeyOperationAsync()
    {
        var operationId = Guid.NewGuid().ToString("N")[..8];

        try
        {
            var clipboardResult = await _clipboardImageProvider.TryCaptureAsync(CancellationToken.None).ConfigureAwait(false);
            if (!clipboardResult.IsSuccess || clipboardResult.Image is null)
            {
                ShowBalloon("Image unavailable", clipboardResult.Message);
                return;
            }

            using var image = clipboardResult.Image;
            var explorerContext = await _explorerContextResolver.GetContextAsync(CancellationToken.None).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(explorerContext.SelectedFilePath))
            {
                _imageFileWriter.ReplaceImageFile(image, explorerContext.SelectedFilePath);
                ShowBalloon("Saved", Path.GetFileName(explorerContext.SelectedFilePath));
                return;
            }

            if (string.IsNullOrWhiteSpace(explorerContext.CurrentFolderPath))
            {
                ShowBalloon("No Explorer folder", "Open a Windows Explorer folder to create a new jpg file.");
                return;
            }

            var newFilePath = _imageFileWriter.CreateNewJpeg(image, explorerContext.CurrentFolderPath);
            ShowBalloon("New file created", Path.GetFileName(newFilePath));
        }
        catch (OperationCanceledException)
        {
            ShowBalloon("Cancelled", "The clipboard operation was cancelled.");
        }
        catch (NotSupportedException ex)
        {
            _errorLogger.Log(operationId, ex);
            ShowBalloon("Unsupported format", ex.Message);
        }
        catch (Exception ex)
        {
            _errorLogger.Log(operationId, ex);
            ShowBalloon("Error", $"Operation {operationId} failed. {TrimForBalloon(ex.Message)}");
        }
    }

    private static string TrimForBalloon(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Unexpected failure.";
        }

        return message.Length <= 180 ? message : $"{message[..177]}...";
    }

    private void ShowBalloon(string title, string message)
    {
        if (_isExiting)
        {
            return;
        }

        _uiContext.Post(_ =>
        {
            if (_isExiting)
            {
                return;
            }

            try
            {
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = message;
                _notifyIcon.ShowBalloonTip(2500);
            }
            catch (Exception ex)
            {
                _errorLogger.Log("notify", ex);
            }
        }, null);
    }

    protected override void ExitThreadCore()
    {
        _isExiting = true;
        NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _window.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private readonly Action _onHotkey;

    public HotkeyWindow(Action onHotkey)
    {
        _onHotkey = onHotkey;
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            _onHotkey();
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
    }
}

internal sealed class OperationCoordinator
{
    private int _isRunning;

    public bool TryBegin() => Interlocked.CompareExchange(ref _isRunning, 1, 0) == 0;

    public void End() => Interlocked.Exchange(ref _isRunning, 0);
}

internal sealed class ClipboardImageProvider
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(750);
    private const int MaxAttempts = 8;

    public Task<ClipboardReadResult> TryCaptureAsync(CancellationToken cancellationToken)
    {
        return RunStaAsync(() => CaptureCore(cancellationToken), cancellationToken);
    }

    private static ClipboardReadResult CaptureCore(CancellationToken cancellationToken)
    {
        Thread.Sleep(InitialDelay);
        var delay = AttemptDelay;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!Clipboard.ContainsData(DataFormats.Bitmap) && !Clipboard.ContainsImage())
                {
                    return ClipboardReadResult.Empty("No image exists in the clipboard.");
                }

                var dataObject = Clipboard.GetDataObject();
                if (dataObject is null)
                {
                    return ClipboardReadResult.Busy("The clipboard owner is still preparing the image data.");
                }

                var image = MaterializeImage(dataObject);
                if (image is not null)
                {
                    return ClipboardReadResult.Success(image);
                }

                return ClipboardReadResult.Unsupported("The clipboard contains image data in an unsupported format.");
            }
            catch (ExternalException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.6d, MaxDelay.TotalMilliseconds));
            }
            catch (COMException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.6d, MaxDelay.TotalMilliseconds));
            }
        }

        return ClipboardReadResult.Busy("The source application kept the clipboard busy for too long.");
    }

    private static Bitmap? MaterializeImage(IDataObject dataObject)
    {
        if (dataObject.GetDataPresent(DataFormats.Bitmap))
        {
            if (dataObject.GetData(DataFormats.Bitmap) is Image image)
            {
                return CloneBitmap(image);
            }
        }

        if (Clipboard.GetImage() is Image clipboardImage)
        {
            using (clipboardImage)
            {
                return CloneBitmap(clipboardImage);
            }
        }

        return null;
    }

    private static Bitmap CloneBitmap(Image image)
    {
        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new InvalidOperationException("The clipboard image has invalid dimensions.");
        }

        return new Bitmap(image);
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
}

internal sealed record ClipboardReadResult(bool IsSuccess, Bitmap? Image, string Message)
{
    public static ClipboardReadResult Success(Bitmap image) => new(true, image, string.Empty);
    public static ClipboardReadResult Empty(string message) => new(false, null, message);
    public static ClipboardReadResult Busy(string message) => new(false, null, message);
    public static ClipboardReadResult Unsupported(string message) => new(false, null, message);
}

internal sealed class ExplorerContextResolver
{
    public Task<ExplorerContext> GetContextAsync(CancellationToken cancellationToken)
    {
        return RunStaAsync(GetContextCore, cancellationToken);
    }

    private static ExplorerContext GetContextCore()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            return ExplorerContext.Empty;
        }

        object? shell = null;
        object? windows = null;
        ExplorerContext? activeContext = null;
        ExplorerContext? fallbackContext = null;

        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return ExplorerContext.Empty;
            }

            dynamic shellDynamic = shell;
            windows = shellDynamic.Windows();
            var foregroundWindow = NativeMethods.GetForegroundWindow();

            foreach (var window in (System.Collections.IEnumerable)windows)
            {
                try
                {
                    var context = TryGetWindowContext(window, foregroundWindow, out var isForegroundExplorer);
                    if (context is null || context.IsEmpty)
                    {
                        continue;
                    }

                    if (isForegroundExplorer)
                    {
                        activeContext = context;
                        return activeContext;
                    }

                    fallbackContext ??= context;
                }
                finally
                {
                    ReleaseComObject(window);
                }
            }
        }
        catch
        {
            return ExplorerContext.Empty;
        }
        finally
        {
            ReleaseComObject(windows);
            ReleaseComObject(shell);
        }

        return activeContext ?? fallbackContext ?? ExplorerContext.Empty;
    }

    private static ExplorerContext? TryGetWindowContext(object window, IntPtr foregroundWindow, out bool isForegroundExplorer)
    {
        isForegroundExplorer = false;

        try
        {
            dynamic explorerWindow = window;
            string executable = Path.GetFileName(((string)explorerWindow.FullName) ?? string.Empty);
            if (!executable.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var hwnd = new IntPtr(Convert.ToInt64(explorerWindow.HWND));
            isForegroundExplorer = hwnd == foregroundWindow;

            var folderPath = GetFolderPath(explorerWindow);
            var selectedFilePath = GetSelectedFilePath(explorerWindow);
            return new ExplorerContext(selectedFilePath, folderPath);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetFolderPath(dynamic window)
    {
        object? document = null;
        object? folder = null;
        object? self = null;

        try
        {
            document = window.Document;
            folder = document?.GetType().InvokeMember("Folder", System.Reflection.BindingFlags.GetProperty, null, document, null);
            self = folder?.GetType().InvokeMember("Self", System.Reflection.BindingFlags.GetProperty, null, folder, null);
            var path = self?.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, self, null) as string;
            return string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) ? null : path;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(self);
            ReleaseComObject(folder);
            ReleaseComObject(document);
        }
    }

    private static string? GetSelectedFilePath(dynamic window)
    {
        object? document = null;
        object? selectedItems = null;
        object? item = null;

        try
        {
            document = window.Document;
            selectedItems = document?.GetType().InvokeMember("SelectedItems", System.Reflection.BindingFlags.InvokeMethod, null, document, null);
            if (selectedItems is null)
            {
                return null;
            }

            var count = Convert.ToInt32(selectedItems.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, selectedItems, null));
            if (count != 1)
            {
                return null;
            }

            item = selectedItems.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, selectedItems, [0]);
            var path = item?.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
            return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(item);
            ReleaseComObject(selectedItems);
            ReleaseComObject(document);
        }
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
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
}

internal sealed class ImageFileWriter
{
    private const long JpegQuality = 95L;

    public void ReplaceImageFile(Image image, string targetPath)
    {
        ValidateTargetFile(targetPath);

        var directory = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("Target directory could not be determined.");
        var extension = Path.GetExtension(targetPath).ToLowerInvariant();
        var tempPath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(targetPath)}.{Guid.NewGuid():N}{extension}");

        try
        {
            SaveImage(image, tempPath, extension);
            EnsureGeneratedFileLooksValid(tempPath);

            try
            {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            catch (PlatformNotSupportedException)
            {
                ReplaceByMove(tempPath, targetPath);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                ReplaceByMove(tempPath, targetPath);
            }
            catch (UnauthorizedAccessException) when (File.Exists(targetPath))
            {
                ReplaceByMove(tempPath, targetPath);
            }
        }
        finally
        {
            TryDeleteIfExists(tempPath);
        }
    }

    public string CreateNewJpeg(Image image, string folderPath)
    {
        ValidateTargetDirectory(folderPath);

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidatePath = Path.Combine(folderPath, $"{GenerateRandomName()}.jpg");

            try
            {
                using (var stream = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    SaveJpeg(image, stream, JpegQuality);
                }

                EnsureGeneratedFileLooksValid(candidatePath);
                return candidatePath;
            }
            catch (IOException) when (File.Exists(candidatePath))
            {
            }
        }

        throw new IOException("A unique file name could not be generated in the current folder.");
    }

    private static void ValidateTargetFile(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("No target file was selected.", nameof(targetPath));
        }

        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("The selected file no longer exists.", targetPath);
        }

        var extension = Path.GetExtension(targetPath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            throw new NotSupportedException("Only png, jpg, jpeg, bmp, gif, tif, and tiff files are supported.");
        }

        var attributes = File.GetAttributes(targetPath);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            throw new UnauthorizedAccessException("The selected file is read-only.");
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The selected file is a reparse point and cannot be replaced safely.");
        }

        ValidateTargetDirectory(Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("Target directory could not be determined."));

        try
        {
            using var stream = new FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException("The selected file is currently in use by another application.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException("The selected file cannot be written.", ex);
        }
    }

    private static void ValidateTargetDirectory(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException("The current Explorer folder is not available.");
        }

        var probePath = Path.Combine(folderPath, $".write-test-{Guid.NewGuid():N}.tmp");

        try
        {
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(probePath);
        }
        catch (IOException ex)
        {
            throw new IOException("The current Explorer folder cannot be written.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException("The current Explorer folder cannot be written.", ex);
        }
        finally
        {
            TryDeleteIfExists(probePath);
        }
    }

    private static void SaveImage(Image image, string targetPath, string extension)
    {
        ValidateImage(image);

        switch (extension)
        {
            case ".png":
                image.Save(targetPath, ImageFormat.Png);
                break;
            case ".bmp":
                image.Save(targetPath, ImageFormat.Bmp);
                break;
            case ".gif":
                image.Save(targetPath, ImageFormat.Gif);
                break;
            case ".tif":
            case ".tiff":
                image.Save(targetPath, ImageFormat.Tiff);
                break;
            case ".jpg":
            case ".jpeg":
                SaveJpeg(image, targetPath, JpegQuality);
                break;
            default:
                throw new NotSupportedException("Only png, jpg, jpeg, bmp, gif, tif, and tiff files are supported.");
        }
    }

    private static void ValidateImage(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new InvalidOperationException("The clipboard image has invalid dimensions.");
        }
    }

    private static void EnsureGeneratedFileLooksValid(string path)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            throw new IOException("The generated image file is empty.");
        }
    }

    private static void ReplaceByMove(string tempPath, string targetPath)
    {
        var backupPath = $"{targetPath}.{Guid.NewGuid():N}.rollback";

        try
        {
            File.Move(targetPath, backupPath, overwrite: true);
            File.Move(tempPath, targetPath, overwrite: true);
            TryDeleteIfExists(backupPath);
        }
        catch
        {
            if (!File.Exists(targetPath) && File.Exists(backupPath))
            {
                File.Move(backupPath, targetPath, overwrite: true);
            }

            throw;
        }
        finally
        {
            TryDeleteIfExists(backupPath);
        }
    }

    private static void SaveJpeg(Image image, string targetPath, long quality)
    {
        using var stream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        SaveJpeg(image, stream, quality);
    }

    private static void SaveJpeg(Image image, Stream targetStream, long quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

        if (encoder is null)
        {
            image.Save(targetStream, ImageFormat.Jpeg);
            return;
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        image.Save(targetStream, encoder, parameters);
    }

    private static string GenerateRandomName()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz";
        Span<char> buffer = stackalloc char[10];

        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        }

        return new string(buffer);
    }

    private static void TryDeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".gif",
        ".tif",
        ".tiff"
    };
}

internal sealed record ExplorerContext(string? SelectedFilePath, string? CurrentFolderPath)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(SelectedFilePath) && string.IsNullOrWhiteSpace(CurrentFolderPath);
    public static ExplorerContext Empty { get; } = new(null, null);
}

internal sealed class ErrorLogger
{
    private readonly string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipboardToSelectedFile",
        "Logs");

    public void Log(string operationId, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var logPath = Path.Combine(_logDirectory, $"{DateTime.UtcNow:yyyyMMdd}.log");
            var lines = new[]
            {
                $"[{DateTime.UtcNow:O}] [{operationId}] {ex.GetType().FullName}",
                ex.Message,
                ex.StackTrace ?? string.Empty,
                string.Empty
            };

            File.AppendAllLines(logPath, lines);
        }
        catch
        {
        }
    }
}

internal static class GlobalErrorHandler
{
    private static readonly ErrorLogger Logger = new();

    public static void Handle(string source, Exception exception)
    {
        Logger.Log(source, exception);
    }
}

internal static class NativeMethods
{
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}

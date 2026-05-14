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
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int HotkeyId = 0x2401;
    private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(200);
    private readonly NotifyIcon _notifyIcon;
    private readonly HotkeyWindow _window;

    public TrayApplicationContext()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "Clipboard To Selected File",
            ContextMenuStrip = BuildMenu()
        };

        _window = new HotkeyWindow(HandleHotkey);
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

    private void HandleHotkey()
    {
        try
        {
            using var image = TryGetClipboardImage();
            if (image is null)
            {
                ShowBalloon("Image unavailable", "No clipboard image was available, or the source application is still preparing it.");
                return;
            }

            var explorerContext = ExplorerSelection.GetContext();
            if (!string.IsNullOrWhiteSpace(explorerContext.SelectedFilePath))
            {
                if (!File.Exists(explorerContext.SelectedFilePath))
                {
                    ShowBalloon("File not found", explorerContext.SelectedFilePath);
                    return;
                }

                SaveClipboardImage(image, explorerContext.SelectedFilePath);
                ShowBalloon("Saved", Path.GetFileName(explorerContext.SelectedFilePath));
                return;
            }

            if (string.IsNullOrWhiteSpace(explorerContext.CurrentFolderPath) || !Directory.Exists(explorerContext.CurrentFolderPath))
            {
                ShowBalloon("No Explorer folder", "Open a Windows Explorer folder to create a new jpg file.");
                return;
            }

            var newFilePath = CreateNewJpegFromClipboard(image, explorerContext.CurrentFolderPath);
            ShowBalloon("New file created", Path.GetFileName(newFilePath));
        }
        catch (NotSupportedException ex)
        {
            ShowBalloon("Unsupported format", ex.Message);
        }
        catch (Exception ex)
        {
            ShowBalloon("Error", ex.Message);
        }
    }

    private static void SaveClipboardImage(Image image, string targetPath)
    {
        EnsureFileIsWritable(targetPath);

        var directory = Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory;
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        var extension = Path.GetExtension(targetPath).ToLowerInvariant();

        try
        {
            switch (extension)
            {
                case ".png":
                    image.Save(tempPath, ImageFormat.Png);
                    break;
                case ".bmp":
                    image.Save(tempPath, ImageFormat.Bmp);
                    break;
                case ".gif":
                    image.Save(tempPath, ImageFormat.Gif);
                    break;
                case ".tif":
                case ".tiff":
                    image.Save(tempPath, ImageFormat.Tiff);
                    break;
                case ".jpg":
                case ".jpeg":
                    SaveJpeg(image, tempPath, 95L);
                    break;
                default:
                    throw new NotSupportedException("Only png, jpg, jpeg, bmp, gif, tif, and tiff files are supported.");
            }

            File.Copy(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string CreateNewJpegFromClipboard(Image image, string folderPath)
    {
        EnsureDirectoryIsWritable(folderPath);

        var newFilePath = GetUniqueJpegPath(folderPath);
        SaveJpeg(image, newFilePath, 95L);
        return newFilePath;
    }

    private static Bitmap? TryGetClipboardImage(int retries = 5)
    {
        for (var attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                if (!Clipboard.ContainsImage())
                {
                    Thread.Sleep(ClipboardRetryDelay);
                    continue;
                }

                using var clipboardImage = Clipboard.GetImage();
                return clipboardImage is null ? null : new Bitmap(clipboardImage);
            }
            catch (ExternalException)
            {
                Thread.Sleep(ClipboardRetryDelay);
            }
        }

        return null;
    }

    private static void EnsureFileIsWritable(string targetPath)
    {
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

    private static void EnsureDirectoryIsWritable(string folderPath)
    {
        try
        {
            var probePath = Path.Combine(folderPath, $".write-test-{Guid.NewGuid():N}.tmp");
            using (File.Create(probePath))
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

    private static string GetUniqueJpegPath(string folderPath)
    {
        string candidatePath;

        do
        {
            candidatePath = Path.Combine(folderPath, $"{GenerateRandomName()}.jpg");
        }
        while (File.Exists(candidatePath));

        return candidatePath;
    }

    private static void SaveJpeg(Image image, string targetPath, long quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

        if (encoder is null)
        {
            image.Save(targetPath, ImageFormat.Jpeg);
            return;
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        image.Save(targetPath, encoder, parameters);
    }

    private void ShowBalloon(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(2500);
    }

    protected override void ExitThreadCore()
    {
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

    public void Dispose() => DestroyHandle();
}

internal static class ExplorerSelection
{
    public static ExplorerContext GetContext()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            return ExplorerContext.Empty;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        ExplorerContext? activeContext = null;
        ExplorerContext? fallbackContext = null;

        try
        {
            var foregroundWindow = NativeMethods.GetForegroundWindow();

            foreach (var window in shell.Windows())
            {
                try
                {
                    string executable = Path.GetFileName(((string)window.FullName) ?? string.Empty);
                    if (!executable.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var folderPath = GetFolderPath(window);
                    var selectedFilePath = GetSelectedFilePath(window);
                    var context = new ExplorerContext(selectedFilePath, folderPath);

                    if (new IntPtr(Convert.ToInt64(window.HWND)) == foregroundWindow)
                    {
                        activeContext = context;
                        if (!context.IsEmpty)
                        {
                            return context;
                        }
                    }

                    if (fallbackContext is null && !context.IsEmpty)
                    {
                        fallbackContext = context;
                    }
                }
                catch
                {
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        return activeContext ?? fallbackContext ?? ExplorerContext.Empty;
    }

    private static string? GetFolderPath(dynamic window)
    {
        try
        {
            return window.Document?.Folder?.Self?.Path as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetSelectedFilePath(dynamic window)
    {
        try
        {
            var selectedItems = window.Document?.SelectedItems();
            if (selectedItems is null || selectedItems.Count != 1)
            {
                return null;
            }

            var item = selectedItems.Item(0);
            if (item is null)
            {
                return null;
            }

            var path = item.Path as string;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record ExplorerContext(string? SelectedFilePath, string? CurrentFolderPath)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(SelectedFilePath) && string.IsNullOrWhiteSpace(CurrentFolderPath);
    public static ExplorerContext Empty { get; } = new(null, null);
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

using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using FastClip.Forms;
using FastClip.Infrastructure;
using FastClip.Interop;
using FastClip.Models;
using FastClip.Services;

namespace FastClip.Application;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan OperationCooldown = TimeSpan.FromMilliseconds(150);
    private readonly NotifyIcon _notifyIcon;
    private readonly HotkeyWindow _window;
    private readonly SynchronizationContext _uiContext;
    private readonly IClipboardImageProvider _clipboardImageProvider;
    private readonly IExplorerContextResolver _explorerContextResolver;
    private readonly IImageFileWriter _imageFileWriter;
    private readonly IImageTransformPipeline _imageTransformPipeline;
    private readonly IAdvancedPasteDialogService _advancedPasteDialogService;
    private readonly OperationCoordinator _operationCoordinator;
    private readonly IErrorLogger _errorLogger;
    private readonly IAppSettingsStore _appSettingsStore;
    private readonly ToolStripMenuItem _hotkeyDisplayItem;
    private readonly ToolStripMenuItem _advancedModeMenuItem;
    private AppSettings _appSettings;
    private HotkeyRegistration _currentHotkey;
    private volatile bool _isExiting;

    public TrayApplicationContext()
        : this(
            new ClipboardImageProvider(),
            new ExplorerContextResolver(),
            new ImageFileWriter(),
            new ImageTransformPipeline(),
            new OperationCoordinator(),
            new ErrorLogger(),
            new AppSettingsStore())
    {
    }

    internal TrayApplicationContext(
        IClipboardImageProvider clipboardImageProvider,
        IExplorerContextResolver explorerContextResolver,
        IImageFileWriter imageFileWriter,
        IImageTransformPipeline imageTransformPipeline,
        OperationCoordinator operationCoordinator,
        IErrorLogger errorLogger,
        IAppSettingsStore appSettingsStore)
    {
        _clipboardImageProvider = clipboardImageProvider;
        _explorerContextResolver = explorerContextResolver;
        _imageFileWriter = imageFileWriter;
        _imageTransformPipeline = imageTransformPipeline;
        _operationCoordinator = operationCoordinator;
        _errorLogger = errorLogger;
        _appSettingsStore = appSettingsStore;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _advancedPasteDialogService = new AdvancedPasteDialogService(_uiContext);
        _appSettings = _appSettingsStore.Load();
        _currentHotkey = _appSettings.Hotkey;
        _hotkeyDisplayItem = new ToolStripMenuItem { Enabled = false };
        _advancedModeMenuItem = new ToolStripMenuItem
        {
            Text = "Advanced Mode",
            Checked = _appSettings.AdvancedModeEnabled
        };
        _advancedModeMenuItem.Click += (_, _) => ToggleAdvancedMode();

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "Clipboard To Selected File",
            ContextMenuStrip = BuildMenu()
        };

        _window = new HotkeyWindow(TriggerHotkeyOperation);
        if (!TryRegisterCurrentHotkey(showError: true))
        {
            _currentHotkey = HotkeyRegistration.Default;
            _appSettings = _appSettings with { Hotkey = _currentHotkey };
            _appSettingsStore.Save(_appSettings);
            TryRegisterCurrentHotkey(showError: false);
        }
        else
        {
            ShowBalloon("Ready", $"Press {_currentHotkey.ToDisplayString()} to replace the selected Explorer image file with the clipboard image.");
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        UpdateHotkeyDisplay();
        menu.Items.Add(_hotkeyDisplayItem);
        menu.Items.Add("Change Hot Key", null, (_, _) => ChangeHotkey());
        menu.Items.Add(_advancedModeMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("About", null, (_, _) => ShowAbout());
        menu.Items.Add("How to use", null, (_, _) =>
            ShowBalloon("Usage", $"Copy an image to the clipboard, select a file in Explorer, then press {_currentHotkey.ToDisplayString()}."));
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private void ChangeHotkey()
    {
        using var dialog = new HotkeySettingsForm(_currentHotkey);
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var nextHotkey = dialog.SelectedHotkey;
        if (nextHotkey == _currentHotkey)
        {
            return;
        }

        var previousHotkey = _currentHotkey;
        UnregisterHotkey(previousHotkey);
        _currentHotkey = nextHotkey;

        if (!TryRegisterCurrentHotkey(showError: false))
        {
            _currentHotkey = previousHotkey;
            TryRegisterCurrentHotkey(showError: false);
            ShowBalloon("Hotkey registration failed", $"{nextHotkey.ToDisplayString()} is already in use by another application.");
            return;
        }

        _appSettings = _appSettings with { Hotkey = _currentHotkey };
        _appSettingsStore.Save(_appSettings);
        UpdateHotkeyDisplay();
        ShowBalloon("Hotkey updated", $"New hotkey: {_currentHotkey.ToDisplayString()}");
    }

    private void ToggleAdvancedMode()
    {
        SetAdvancedMode(!_advancedModeMenuItem.Checked);
    }

    private void SetAdvancedMode(bool isEnabled)
    {
        var nextAutoApply = isEnabled ? false : _appSettings.AutoApplyAdvancedSettings;
        if (_appSettings.AdvancedModeEnabled == isEnabled &&
            _appSettings.AutoApplyAdvancedSettings == nextAutoApply)
        {
            _advancedModeMenuItem.Checked = isEnabled;
            return;
        }

        _advancedModeMenuItem.Checked = isEnabled;
        _appSettings = _appSettings with
        {
            AdvancedModeEnabled = isEnabled,
            AutoApplyAdvancedSettings = nextAutoApply
        };
        _appSettingsStore.Save(_appSettings);
    }

    private void ShowAbout()
    {
        using var dialog = new AboutForm();
        dialog.ShowDialog();
    }

    private bool TryRegisterCurrentHotkey(bool showError)
    {
        UpdateHotkeyDisplay();

        if (NativeMethods.RegisterHotKey(_window.Handle, _currentHotkey.Id, _currentHotkey.Modifiers, (uint)_currentHotkey.Key))
        {
            return true;
        }

        if (showError)
        {
            ShowBalloon("Hotkey registration failed", $"{_currentHotkey.ToDisplayString()} is already in use by another application.");
        }

        return false;
    }

    private void UnregisterHotkey(HotkeyRegistration hotkey)
    {
        NativeMethods.UnregisterHotKey(_window.Handle, hotkey.Id);
    }

    private void UpdateHotkeyDisplay()
    {
        _hotkeyDisplayItem.Text = $"Hotkey: {_currentHotkey.ToDisplayString()}";
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
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
            var pasteSession = PasteSession.Create(image, explorerContext);

            if (pasteSession.TargetKind == PasteTargetKind.None)
            {
                ShowBalloon("No Explorer folder", "Open a Windows Explorer folder to create a new jpg file.");
                return;
            }

            if (_appSettings.SavedAdvancedSettings is not null)
            {
                ApplySavedAdvancedSettings(pasteSession, _appSettings.SavedAdvancedSettings);
            }

            if (_appSettings.AdvancedModeEnabled)
            {
                var dialogResult = await _advancedPasteDialogService.ShowAsync(pasteSession, CancellationToken.None).ConfigureAwait(false);
                if (!dialogResult.ShouldSave)
                {
                    return;
                }

                _appSettings = _appSettings with
                {
                    AdvancedModeEnabled = !dialogResult.AutoApplyNextTime,
                    AutoApplyAdvancedSettings = dialogResult.AutoApplyNextTime,
                    SavedAdvancedSettings = AdvancedSettingsSnapshot.FromPasteOptions(pasteSession.Options)
                };
                _advancedModeMenuItem.Checked = _appSettings.AdvancedModeEnabled;
                _appSettingsStore.Save(_appSettings);
            }
            else if (_appSettings.AutoApplyAdvancedSettings && _appSettings.SavedAdvancedSettings is not null)
            {
                _appSettings = _appSettings with
                {
                    SavedAdvancedSettings = AdvancedSettingsSnapshot.FromPasteOptions(pasteSession.Options)
                };
                _appSettingsStore.Save(_appSettings);
            }

            using var outputImage = _imageTransformPipeline.Apply(image, pasteSession.Options);
            var saveResult = SavePasteSession(outputImage, pasteSession);
            if (!string.IsNullOrWhiteSpace(saveResult.WarningMessage))
            {
                _errorLogger.Log(operationId, new InvalidOperationException(saveResult.WarningMessage));
            }

            ShowBalloon(
                pasteSession.TargetKind == PasteTargetKind.ExistingFile ? "Saved" : "New file created",
                saveResult.WarningMessage is null
                    ? Path.GetFileName(saveResult.SavedPath)
                    : $"{Path.GetFileName(saveResult.SavedPath)} ({saveResult.WarningMessage})");
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

    private ImageSaveResult SavePasteSession(Image image, PasteSession session)
    {
        if (session.TargetKind == PasteTargetKind.ExistingFile)
        {
            return _imageFileWriter.ReplaceImageFile(image, session.ExplorerContext.SelectedFilePath!, session.Options);
        }

        return _imageFileWriter.CreateNewImage(image, session.ExplorerContext.CurrentFolderPath!, session.OutputExtension, session.Options);
    }

    private static void ApplySavedAdvancedSettings(PasteSession session, AdvancedSettingsSnapshot savedSettings)
    {
        session.Options.Resize.KeepAspectRatio = savedSettings.KeepAspectRatio;
        session.Options.Resize.ScalePercent = savedSettings.ScalePercent;

        if (savedSettings.ScalePercent.HasValue)
        {
            var percent = savedSettings.ScalePercent.Value / 100d;
            session.Options.Resize.Width = Math.Clamp((int)Math.Round(session.Options.Resize.OriginalWidth * percent), 1, session.Options.Resize.OriginalWidth);
            session.Options.Resize.Height = Math.Clamp((int)Math.Round(session.Options.Resize.OriginalHeight * percent), 1, session.Options.Resize.OriginalHeight);
        }
        else if (savedSettings.KeepAspectRatio)
        {
            var width = Math.Clamp((int)Math.Round(session.Options.Resize.OriginalWidth * savedSettings.WidthRatio), 1, session.Options.Resize.OriginalWidth);
            var height = Math.Clamp((int)Math.Round(session.Options.Resize.OriginalHeight * savedSettings.WidthRatio), 1, session.Options.Resize.OriginalHeight);
            session.Options.Resize.Width = width;
            session.Options.Resize.Height = height;
        }
        else
        {
            session.Options.Resize.Width = Math.Clamp((int)Math.Round(session.Options.Resize.OriginalWidth * savedSettings.WidthRatio), 1, session.Options.Resize.OriginalWidth);
            session.Options.Resize.Height = Math.Clamp((int)Math.Round(session.Options.Resize.OriginalHeight * savedSettings.HeightRatio), 1, session.Options.Resize.OriginalHeight);
        }

        if (session.Options.Compression.AvailableForCurrentTarget)
        {
            session.Options.Compression.JpegQuality = Math.Clamp(savedSettings.JpegQuality, 0, 100);
            session.Options.Compression.PngOptimizationLevel = Math.Clamp(savedSettings.PngOptimizationLevel, 0, 6);
        }

        if (session.TargetKind == PasteTargetKind.NewFile &&
            !string.IsNullOrWhiteSpace(savedSettings.OutputExtension) &&
            PasteSession.IsSupportedNewFileExtension(savedSettings.OutputExtension))
        {
            session.SetOutputExtension(savedSettings.OutputExtension);
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
        UnregisterHotkey(_currentHotkey);
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _window.Dispose();
        base.ExitThreadCore();
    }
}

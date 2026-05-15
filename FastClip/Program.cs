using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    private static readonly TimeSpan OperationCooldown = TimeSpan.FromMilliseconds(150);
    private readonly NotifyIcon _notifyIcon;
    private readonly HotkeyWindow _window;
    private readonly SynchronizationContext _uiContext;
    private readonly ClipboardImageProvider _clipboardImageProvider = new();
    private readonly ExplorerContextResolver _explorerContextResolver = new();
    private readonly ImageFileWriter _imageFileWriter = new();
    private readonly ImageTransformPipeline _imageTransformPipeline = new();
    private readonly OperationCoordinator _operationCoordinator = new();
    private readonly ErrorLogger _errorLogger = new();
    private readonly AppSettingsStore _appSettingsStore = new();
    private readonly ToolStripMenuItem _hotkeyDisplayItem;
    private readonly ToolStripMenuItem _advancedModeMenuItem;
    private AppSettings _appSettings;
    private HotkeyRegistration _currentHotkey;
    private volatile bool _isExiting;

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _appSettings = _appSettingsStore.Load();
        _currentHotkey = _appSettings.Hotkey;
        _hotkeyDisplayItem = new ToolStripMenuItem
        {
            Enabled = false
        };
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
        SetAdvancedMode(!_advancedModeMenuItem.Checked, resetAutoApply: true);
    }

    private void SetAdvancedMode(bool isEnabled, bool resetAutoApply)
    {
        if (_appSettings.AdvancedModeEnabled == isEnabled &&
            (!resetAutoApply || !_appSettings.AutoApplyAdvancedSettings))
        {
            _advancedModeMenuItem.Checked = isEnabled;
            return;
        }

        _advancedModeMenuItem.Checked = isEnabled;
        _appSettings = _appSettings with
        {
            AdvancedModeEnabled = isEnabled,
            AutoApplyAdvancedSettings = resetAutoApply ? false : _appSettings.AutoApplyAdvancedSettings
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
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
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

            if (_appSettings.AdvancedModeEnabled)
            {
                if (_appSettings.SavedAdvancedSettings is not null)
                {
                    ApplySavedAdvancedSettings(pasteSession, _appSettings.SavedAdvancedSettings);
                }

                if (!_appSettings.AutoApplyAdvancedSettings)
                {
                    var dialogResult = await ShowAdvancedPasteDialogAsync(pasteSession).ConfigureAwait(false);
                    if (!dialogResult.ShouldSave)
                    {
                        ShowBalloon("Cancelled", "The advanced paste operation was cancelled.");
                        return;
                    }

                    _appSettings = _appSettings with
                    {
                        AutoApplyAdvancedSettings = dialogResult.AutoApplyNextTime,
                        SavedAdvancedSettings = AdvancedSettingsSnapshot.FromPasteOptions(pasteSession.Options)
                    };
                    _appSettingsStore.Save(_appSettings);
                }
                else
                {
                    _appSettings = _appSettings with
                    {
                        SavedAdvancedSettings = AdvancedSettingsSnapshot.FromPasteOptions(pasteSession.Options)
                    };
                    _appSettingsStore.Save(_appSettings);
                }
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

    private Task<AdvancedPasteDialogResult> ShowAdvancedPasteDialogAsync(PasteSession session)
    {
        var tcs = new TaskCompletionSource<AdvancedPasteDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        _uiContext.Post(_ =>
        {
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

internal sealed class AppSettingsStore
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipboardToSelectedFile",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return AppSettings.Default;
            }

            var json = File.ReadAllText(_settingsPath);
            var model = JsonSerializer.Deserialize<AppSettingsModel>(json);
            if (model is null)
            {
                return AppSettings.Default;
            }

            var key = Enum.TryParse<Keys>(model.Key, ignoreCase: true, out var parsedKey) ? parsedKey : Keys.None;
            if (key == Keys.None)
            {
                return AppSettings.Default;
            }

            uint modifiers = 0;
            if (model.Control)
            {
                modifiers |= NativeMethods.MOD_CONTROL;
            }

            if (model.Shift)
            {
                modifiers |= NativeMethods.MOD_SHIFT;
            }

            if (model.Alt)
            {
                modifiers |= NativeMethods.MOD_ALT;
            }

            if (model.Win)
            {
                modifiers |= NativeMethods.MOD_WIN;
            }

            if (modifiers == 0)
            {
                return AppSettings.Default;
            }

            return new AppSettings(
                new HotkeyRegistration(HotkeyRegistration.Default.Id, modifiers, key),
                model.AdvancedModeEnabled,
                model.AutoApplyAdvancedSettings,
                model.SavedAdvancedSettings is null
                    ? null
                    : new AdvancedSettingsSnapshot(
                        model.SavedAdvancedSettings.KeepAspectRatio,
                        model.SavedAdvancedSettings.ScalePercent,
                        model.SavedAdvancedSettings.WidthRatio,
                        model.SavedAdvancedSettings.HeightRatio,
                        model.SavedAdvancedSettings.JpegQuality,
                        model.SavedAdvancedSettings.PngOptimizationLevel,
                        model.SavedAdvancedSettings.OutputExtension));
        }
        catch
        {
            return AppSettings.Default;
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var model = new AppSettingsModel
        {
            Control = (settings.Hotkey.Modifiers & NativeMethods.MOD_CONTROL) != 0,
            Shift = (settings.Hotkey.Modifiers & NativeMethods.MOD_SHIFT) != 0,
            Alt = (settings.Hotkey.Modifiers & NativeMethods.MOD_ALT) != 0,
            Win = (settings.Hotkey.Modifiers & NativeMethods.MOD_WIN) != 0,
            Key = settings.Hotkey.Key.ToString(),
            AdvancedModeEnabled = settings.AdvancedModeEnabled,
            AutoApplyAdvancedSettings = settings.AutoApplyAdvancedSettings,
            SavedAdvancedSettings = settings.SavedAdvancedSettings is null
                ? null
                : new AdvancedSettingsSnapshotModel
                {
                    KeepAspectRatio = settings.SavedAdvancedSettings.KeepAspectRatio,
                    ScalePercent = settings.SavedAdvancedSettings.ScalePercent,
                    WidthRatio = settings.SavedAdvancedSettings.WidthRatio,
                    HeightRatio = settings.SavedAdvancedSettings.HeightRatio,
                    JpegQuality = settings.SavedAdvancedSettings.JpegQuality,
                    PngOptimizationLevel = settings.SavedAdvancedSettings.PngOptimizationLevel,
                    OutputExtension = settings.SavedAdvancedSettings.OutputExtension
                }
        };

        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }
}

internal sealed class AppSettingsModel
{
    public bool Control { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
    public bool Win { get; set; }
    public string Key { get; set; } = Keys.V.ToString();
    public bool AdvancedModeEnabled { get; set; }
    public bool AutoApplyAdvancedSettings { get; set; }
    public AdvancedSettingsSnapshotModel? SavedAdvancedSettings { get; set; }
}

internal sealed class AdvancedSettingsSnapshotModel
{
    public bool KeepAspectRatio { get; set; }
    public int? ScalePercent { get; set; }
    public double WidthRatio { get; set; }
    public double HeightRatio { get; set; }
    public int JpegQuality { get; set; }
    public int PngOptimizationLevel { get; set; }
    public string OutputExtension { get; set; } = ".jpg";
}

internal sealed record AppSettings(
    HotkeyRegistration Hotkey,
    bool AdvancedModeEnabled,
    bool AutoApplyAdvancedSettings,
    AdvancedSettingsSnapshot? SavedAdvancedSettings)
{
    public static AppSettings Default { get; } = new(HotkeyRegistration.Default, false, false, null);
}

internal sealed record AdvancedSettingsSnapshot(
    bool KeepAspectRatio,
    int? ScalePercent,
    double WidthRatio,
    double HeightRatio,
    int JpegQuality,
    int PngOptimizationLevel,
    string OutputExtension)
{
    public static AdvancedSettingsSnapshot FromPasteOptions(PasteOptions options)
    {
        return new AdvancedSettingsSnapshot(
            options.Resize.KeepAspectRatio,
            options.Resize.ScalePercent,
            options.Resize.Width / (double)Math.Max(1, options.Resize.OriginalWidth),
            options.Resize.Height / (double)Math.Max(1, options.Resize.OriginalHeight),
            options.Compression.JpegQuality,
            options.Compression.PngOptimizationLevel,
            options.OutputExtension);
    }
}

internal sealed record AdvancedPasteDialogResult(bool ShouldSave, bool AutoApplyNextTime);

internal sealed class HotkeySettingsForm : Form
{
    private readonly CheckBox _ctrlCheckBox;
    private readonly CheckBox _shiftCheckBox;
    private readonly CheckBox _altCheckBox;
    private readonly CheckBox _winCheckBox;
    private readonly ComboBox _keyComboBox;
    public HotkeyRegistration SelectedHotkey { get; private set; }

    public HotkeySettingsForm(HotkeyRegistration currentHotkey)
    {
        SelectedHotkey = currentHotkey;
        Text = "Change Hot Key";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(320, 210);

        var titleLabel = new Label
        {
            AutoSize = true,
            Location = new Point(20, 18),
            Text = "Select a new hotkey"
        };

        _ctrlCheckBox = new CheckBox
        {
            AutoSize = true,
            Location = new Point(23, 52),
            Text = "Ctrl"
        };

        _shiftCheckBox = new CheckBox
        {
            AutoSize = true,
            Location = new Point(95, 52),
            Text = "Shift"
        };

        _altCheckBox = new CheckBox
        {
            AutoSize = true,
            Location = new Point(175, 52),
            Text = "Alt"
        };

        _winCheckBox = new CheckBox
        {
            AutoSize = true,
            Location = new Point(235, 52),
            Text = "Win"
        };

        var keyLabel = new Label
        {
            AutoSize = true,
            Location = new Point(20, 92),
            Text = "Key"
        };

        _keyComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(23, 114),
            Width = 274
        };

        foreach (var key in SupportedKeys)
        {
            _keyComboBox.Items.Add(key);
        }

        var okButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(141, 160),
            Width = 75
        };
        okButton.Click += (_, args) => OnSave(args);

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(222, 160),
            Width = 75
        };

        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.Add(titleLabel);
        Controls.Add(_ctrlCheckBox);
        Controls.Add(_shiftCheckBox);
        Controls.Add(_altCheckBox);
        Controls.Add(_winCheckBox);
        Controls.Add(keyLabel);
        Controls.Add(_keyComboBox);
        Controls.Add(okButton);
        Controls.Add(cancelButton);

        _ctrlCheckBox.Checked = (currentHotkey.Modifiers & NativeMethods.MOD_CONTROL) != 0;
        _shiftCheckBox.Checked = (currentHotkey.Modifiers & NativeMethods.MOD_SHIFT) != 0;
        _altCheckBox.Checked = (currentHotkey.Modifiers & NativeMethods.MOD_ALT) != 0;
        _winCheckBox.Checked = (currentHotkey.Modifiers & NativeMethods.MOD_WIN) != 0;
        _keyComboBox.SelectedItem = currentHotkey.Key;
        if (_keyComboBox.SelectedIndex < 0)
        {
            _keyComboBox.SelectedItem = Keys.V;
        }
    }

    private void OnSave(EventArgs args)
    {
        var modifiers = 0u;

        if (_ctrlCheckBox.Checked)
        {
            modifiers |= NativeMethods.MOD_CONTROL;
        }

        if (_shiftCheckBox.Checked)
        {
            modifiers |= NativeMethods.MOD_SHIFT;
        }

        if (_altCheckBox.Checked)
        {
            modifiers |= NativeMethods.MOD_ALT;
        }

        if (_winCheckBox.Checked)
        {
            modifiers |= NativeMethods.MOD_WIN;
        }

        if (modifiers == 0)
        {
            MessageBox.Show(this, "Select at least one modifier key.", "Invalid Hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        if (_keyComboBox.SelectedItem is not Keys selectedKey)
        {
            MessageBox.Show(this, "Select a key.", "Invalid Hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        SelectedHotkey = new HotkeyRegistration(HotkeyRegistration.Default.Id, modifiers, selectedKey);
    }

    private static readonly Keys[] SupportedKeys =
    [
        Keys.A, Keys.B, Keys.C, Keys.D, Keys.E, Keys.F, Keys.G, Keys.H, Keys.I, Keys.J, Keys.K, Keys.L, Keys.M,
        Keys.N, Keys.O, Keys.P, Keys.Q, Keys.R, Keys.S, Keys.T, Keys.U, Keys.V, Keys.W, Keys.X, Keys.Y, Keys.Z,
        Keys.D0, Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6, Keys.D7, Keys.D8, Keys.D9,
        Keys.F1, Keys.F2, Keys.F3, Keys.F4, Keys.F5, Keys.F6, Keys.F7, Keys.F8, Keys.F9, Keys.F10, Keys.F11, Keys.F12
    ];
}

internal sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = "About FastClip";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(420, 270);
        BackColor = Color.FromArgb(247, 248, 244);

        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 96,
            BackColor = Color.FromArgb(230, 242, 232)
        };

        var titleLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = "FastClip",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 22f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(34, 74, 48)
        };

        headerPanel.Controls.Add(titleLabel);

        var nameLabel = new Label
        {
            AutoSize = false,
            Location = new Point(32, 118),
            Size = new Size(356, 24),
            Text = "Enes Sonmez",
            Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(32, 32, 32),
            TextAlign = ContentAlignment.MiddleCenter
        };

        var descriptionLabel = new Label
        {
            AutoSize = false,
            Location = new Point(42, 146),
            Size = new Size(336, 42),
            Text = "Clipboard-to-file workflow utility for fast image replacement and export on Windows.",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(90, 90, 90),
            TextAlign = ContentAlignment.MiddleCenter
        };

        var websiteLink = BuildLinkLabel("enes.dev", "https://enes.dev", new Point(32, 198));
        var xLink = BuildLinkLabel("x.com/enes_dev", "https://x.com/enes_dev", new Point(32, 226));

        var closeButton = new Button
        {
            Text = "Close",
            Size = new Size(84, 30),
            Location = new Point(304, 224),
            BackColor = Color.White
        };
        closeButton.Click += (_, _) => Close();

        Controls.Add(headerPanel);
        Controls.Add(nameLabel);
        Controls.Add(descriptionLabel);
        Controls.Add(websiteLink);
        Controls.Add(xLink);
        Controls.Add(closeButton);
    }

    private static LinkLabel BuildLinkLabel(string text, string url, Point location)
    {
        var link = new LinkLabel
        {
            AutoSize = false,
            Location = location,
            Size = new Size(240, 24),
            Text = text,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
            LinkColor = Color.FromArgb(40, 98, 72),
            ActiveLinkColor = Color.FromArgb(24, 72, 50),
            VisitedLinkColor = Color.FromArgb(40, 98, 72)
        };

        link.Click += (_, _) => OpenExternalUrl(url);
        return link;
    }

    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }
}

internal enum PasteTargetKind
{
    None,
    ExistingFile,
    NewFile
}

internal sealed class PasteSession
{
    private static readonly string[] SupportedNewFileExtensions = [".jpg", ".png", ".bmp", ".gif", ".tif"];

    public static PasteSession Create(Image image, ExplorerContext explorerContext)
    {
        var targetKind = !string.IsNullOrWhiteSpace(explorerContext.SelectedFilePath)
            ? PasteTargetKind.ExistingFile
            : string.IsNullOrWhiteSpace(explorerContext.CurrentFolderPath)
                ? PasteTargetKind.None
                : PasteTargetKind.NewFile;
        var outputExtension = targetKind == PasteTargetKind.ExistingFile
            ? Path.GetExtension(explorerContext.SelectedFilePath!).ToLowerInvariant()
            : targetKind == PasteTargetKind.NewFile
                ? ".jpg"
                : string.Empty;
        var compressionFormat = outputExtension switch
        {
            ".jpg" or ".jpeg" => CompressionTargetFormat.Jpeg,
            ".png" => CompressionTargetFormat.Png,
            _ => CompressionTargetFormat.None
        };
        var compressionAvailable = compressionFormat != CompressionTargetFormat.None;

        return new PasteSession
        {
            SourceImage = image,
            ExplorerContext = explorerContext,
            TargetKind = targetKind,
            OutputExtension = outputExtension,
            Options = new PasteOptions
            {
                OutputExtension = outputExtension,
                Resize = new ResizeOptions
                {
                    OriginalWidth = image.Width,
                    OriginalHeight = image.Height,
                    Width = image.Width,
                    Height = image.Height,
                    KeepAspectRatio = true
                },
                Compression = new CompressionOptions
                {
                    Enabled = compressionAvailable,
                    AvailableForCurrentTarget = compressionAvailable,
                    TargetFormat = compressionFormat,
                    JpegQuality = 85,
                    PngOptimizationLevel = 4
                },
            }
        };
    }

    public static bool IsSupportedNewFileExtension(string extension)
    {
        return SupportedNewFileExtensions.Contains(NormalizeExtension(extension), StringComparer.OrdinalIgnoreCase);
    }

    public void SetOutputExtension(string extension)
    {
        var normalizedExtension = NormalizeExtension(extension);
        if (TargetKind == PasteTargetKind.ExistingFile || !IsSupportedNewFileExtension(normalizedExtension))
        {
            return;
        }

        OutputExtension = normalizedExtension;
        Options.OutputExtension = normalizedExtension;

        var compressionFormat = normalizedExtension switch
        {
            ".jpg" or ".jpeg" => CompressionTargetFormat.Jpeg,
            ".png" => CompressionTargetFormat.Png,
            _ => CompressionTargetFormat.None
        };

        Options.Compression.Enabled = compressionFormat != CompressionTargetFormat.None;
        Options.Compression.AvailableForCurrentTarget = compressionFormat != CompressionTargetFormat.None;
        Options.Compression.TargetFormat = compressionFormat;
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var normalizedExtension = extension.StartsWith('.') ? extension : $".{extension}";
        return normalizedExtension.ToLowerInvariant();
    }

    public required Image SourceImage { get; init; }
    public required ExplorerContext ExplorerContext { get; init; }
    public required PasteTargetKind TargetKind { get; init; }
    public string OutputExtension { get; private set; } = string.Empty;
    public required PasteOptions Options { get; init; }
}

internal sealed class PasteOptions
{
    public required string OutputExtension { get; set; }
    public required ResizeOptions Resize { get; set; }
    public required CompressionOptions Compression { get; set; }
}

internal sealed class ResizeOptions
{
    public required int OriginalWidth { get; init; }
    public required int OriginalHeight { get; init; }
    public required int Width { get; set; }
    public required int Height { get; set; }
    public required bool KeepAspectRatio { get; set; }
    public int? ScalePercent { get; set; }
    public bool IsResizeActive => Width != OriginalWidth || Height != OriginalHeight;
}

internal sealed class CompressionOptions
{
    public required bool Enabled { get; set; }
    public required bool AvailableForCurrentTarget { get; set; }
    public required CompressionTargetFormat TargetFormat { get; set; }
    public required int JpegQuality { get; set; }
    public required int PngOptimizationLevel { get; set; }
}

internal enum CompressionTargetFormat
{
    None = 0,
    Jpeg = 1,
    Png = 2
}

internal sealed class AdvancedPasteForm : Form
{
    private const int WidthInputX = 24;
    private const int HeightInputX = 264;
    private const int InputTopY = 58;
    private const int InputWidth = 146;
    private const int LinkButtonWidth = 30;
    private const int LinkButtonHeight = 30;
    private readonly PasteSession _session;
    private readonly TabControl _tabControl;
    private readonly TabPage _compressionTab;
    private readonly NumericUpDown _widthInput;
    private readonly NumericUpDown _heightInput;
    private readonly AspectRatioIconButton _linkButton;
    private readonly ComboBox _scalePresetComboBox;
    private readonly ComboBox _formatComboBox;
    private readonly TrackBar _compressionQualityTrackBar;
    private readonly Label _compressionQualityValueLabel;
    private readonly TrackBar _pngOptimizationTrackBar;
    private readonly Label _pngOptimizationValueLabel;
    private readonly Label _compressionEstimateLabel;
    private readonly CheckBox _autoApplyCheckBox;
    private readonly System.Windows.Forms.Timer _compressionEstimateTimer;
    private readonly ToolTip _toolTip;
    private readonly ImageTransformPipeline _imageTransformPipeline;
    private readonly MozJpegEncoder _mozJpegEncoder;
    private readonly OxipngEncoder _oxipngEncoder;
    private bool _isUpdatingControls;
    private int _compressionEstimateVersion;
    public bool AutoApplyNextTime => _autoApplyCheckBox.Checked;

    public AdvancedPasteForm(PasteSession session)
    {
        _session = session;
        _toolTip = new ToolTip();
        _imageTransformPipeline = new ImageTransformPipeline();
        _mozJpegEncoder = new MozJpegEncoder();
        _oxipngEncoder = new OxipngEncoder();
        _compressionEstimateTimer = new System.Windows.Forms.Timer
        {
            Interval = 400
        };
        _compressionEstimateTimer.Tick += (_, _) => StartCompressionEstimate();
        FormClosed += (_, _) => _compressionEstimateTimer.Dispose();

        Text = "FastClip - Advanced Paste";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(248, 250, 252);
        ClientSize = new Size(468, 362);

        _tabControl = new TabControl
        {
            Location = new Point(18, 18),
            Size = new Size(432, 274)
        };

        var resizeTab = new TabPage("Resize");
        resizeTab.BackColor = Color.White;
        _tabControl.TabPages.Add(resizeTab);
        _compressionTab = new TabPage("Compress");
        _compressionTab.BackColor = Color.White;
        _tabControl.TabPages.Add(_compressionTab);

        var resizeCaptionLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 18),
            Text = "Adjust output size and file format before saving.",
            ForeColor = Color.FromArgb(91, 103, 112)
        };

        var widthLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 34),
            Text = "Width"
        };

        _widthInput = new NumericUpDown
        {
            Location = new Point(WidthInputX, InputTopY),
            Width = InputWidth,
            Minimum = 1,
            Maximum = session.Options.Resize.OriginalWidth,
            Value = session.Options.Resize.Width
        };
        _widthInput.ValueChanged += (_, _) => OnWidthChanged();

        _linkButton = new AspectRatioIconButton
        {
            Location = new Point(GetLinkButtonX(), GetLinkButtonY(_widthInput)),
            Size = new Size(LinkButtonWidth, LinkButtonHeight)
        };
        _linkButton.Click += (_, _) => ToggleAspectRatio();
        UpdateLinkButtonState();

        var heightLabel = new Label
        {
            AutoSize = true,
            Location = new Point(264, 34),
            Text = "Height"
        };

        _heightInput = new NumericUpDown
        {
            Location = new Point(HeightInputX, InputTopY),
            Width = InputWidth,
            Minimum = 1,
            Maximum = session.Options.Resize.OriginalHeight,
            Value = session.Options.Resize.Height
        };
        _heightInput.ValueChanged += (_, _) => OnHeightChanged();

        var presetLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 110),
            Text = "Scale Preset"
        };

        _scalePresetComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(24, 134),
            Width = 386
        };
        _scalePresetComboBox.Items.Add("Custom");
        foreach (var preset in ScalePresets)
        {
            _scalePresetComboBox.Items.Add($"{preset}%");
        }
        _scalePresetComboBox.SelectedIndexChanged += (_, _) => OnScalePresetChanged();

        var formatLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 182),
            Text = "Format"
        };

        _formatComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(24, 208),
            Width = 386
        };
        _formatComboBox.Items.AddRange(
        [
            new FormatOption(".jpg", "JPG"),
            new FormatOption(".png", "PNG"),
            new FormatOption(".bmp", "BMP"),
            new FormatOption(".gif", "GIF"),
            new FormatOption(".tif", "TIFF")
        ]);
        _formatComboBox.SelectedIndexChanged += (_, _) => OnFormatChanged();

        resizeTab.Controls.Add(resizeCaptionLabel);
        resizeTab.Controls.Add(widthLabel);
        resizeTab.Controls.Add(_widthInput);
        resizeTab.Controls.Add(_linkButton);
        resizeTab.Controls.Add(heightLabel);
        resizeTab.Controls.Add(_heightInput);
        resizeTab.Controls.Add(presetLabel);
        resizeTab.Controls.Add(_scalePresetComboBox);
        resizeTab.Controls.Add(formatLabel);
        resizeTab.Controls.Add(_formatComboBox);

        _compressionQualityTrackBar = new TrackBar();
        _compressionQualityValueLabel = new Label();
        _pngOptimizationTrackBar = new TrackBar();
        _pngOptimizationValueLabel = new Label();
        _compressionEstimateLabel = new Label();

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(286, 312),
            Width = 78,
            Height = 34
        };

        _autoApplyCheckBox = new CheckBox
        {
            AutoSize = false,
            Location = new Point(20, 308),
            Size = new Size(236, 40),
            Text = "Use these settings next time",
            TextAlign = ContentAlignment.MiddleLeft
        };
        _toolTip.SetToolTip(_autoApplyCheckBox, "Apply these settings automatically next time and skip this window.");

        var saveButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(372, 312),
            Width = 78,
            Height = 34
        };
        saveButton.Click += (_, _) => OnSave();

        AcceptButton = saveButton;
        CancelButton = cancelButton;

        Controls.Add(_tabControl);
        Controls.Add(_autoApplyCheckBox);
        Controls.Add(cancelButton);
        Controls.Add(saveButton);

        UpdatePresetSelection();
        UpdateFormatSelection();
        RebuildCompressionTab();
        ScheduleCompressionEstimate();
    }

    private void ToggleAspectRatio()
    {
        _session.Options.Resize.KeepAspectRatio = !_session.Options.Resize.KeepAspectRatio;
        UpdateLinkButtonState();
        if (_session.Options.Resize.KeepAspectRatio)
        {
            ApplyScaledDimensions((int)_widthInput.Value, resizeBasedOnWidth: true);
        }
    }

    private void UpdateLinkButtonState()
    {
        _linkButton.IsLinked = _session.Options.Resize.KeepAspectRatio;
        _linkButton.Invalidate();
        _toolTip.SetToolTip(
            _linkButton,
            _session.Options.Resize.KeepAspectRatio
                ? "Aspect ratio locked"
                : "Aspect ratio unlocked");
    }

    private void OnWidthChanged()
    {
        if (_isUpdatingControls)
        {
            return;
        }

        if (_session.Options.Resize.KeepAspectRatio)
        {
            ApplyScaledDimensions((int)_widthInput.Value, resizeBasedOnWidth: true);
            return;
        }

        UpdateWidthOnly((int)_widthInput.Value);
    }

    private void OnHeightChanged()
    {
        if (_isUpdatingControls)
        {
            return;
        }

        if (_session.Options.Resize.KeepAspectRatio)
        {
            ApplyScaledDimensions((int)_heightInput.Value, resizeBasedOnWidth: false);
            return;
        }

        UpdateHeightOnly((int)_heightInput.Value);
    }

    private void OnScalePresetChanged()
    {
        if (_isUpdatingControls)
        {
            return;
        }

        if (_scalePresetComboBox.SelectedItem is not string selection || selection == "Custom")
        {
            return;
        }

        var percent = int.Parse(selection.TrimEnd('%'));
        var width = Math.Max(1, _session.Options.Resize.OriginalWidth * percent / 100);
        ApplyScaledDimensions(width, resizeBasedOnWidth: true, percent);
    }

    private void OnFormatChanged()
    {
        if (_isUpdatingControls || _session.TargetKind != PasteTargetKind.NewFile || _formatComboBox.SelectedItem is not FormatOption option)
        {
            return;
        }

        _session.SetOutputExtension(option.Extension);
        RebuildCompressionTab();
        ScheduleCompressionEstimate();
    }

    private void OnCompressionQualityChanged()
    {
        if (_isUpdatingControls ||
            !_session.Options.Compression.AvailableForCurrentTarget ||
            _session.Options.Compression.TargetFormat != CompressionTargetFormat.Jpeg)
        {
            return;
        }

        _session.Options.Compression.JpegQuality = _compressionQualityTrackBar.Value;
        _compressionQualityValueLabel.Text = _compressionQualityTrackBar.Value.ToString();
        ScheduleCompressionEstimate();
    }

    private void OnPngOptimizationLevelChanged()
    {
        if (_isUpdatingControls ||
            !_session.Options.Compression.AvailableForCurrentTarget ||
            _session.Options.Compression.TargetFormat != CompressionTargetFormat.Png)
        {
            return;
        }

        _session.Options.Compression.PngOptimizationLevel = _pngOptimizationTrackBar.Value;
        _pngOptimizationValueLabel.Text = _pngOptimizationTrackBar.Value.ToString();
        ScheduleCompressionEstimate();
    }

    private void ApplyScaledDimensions(int primaryValue, bool resizeBasedOnWidth, int? scalePercent = null)
    {
        var original = _session.Options.Resize;
        var width = resizeBasedOnWidth ? primaryValue : Math.Max(1, (int)Math.Round(primaryValue * (original.OriginalWidth / (double)original.OriginalHeight)));
        var height = resizeBasedOnWidth ? Math.Max(1, (int)Math.Round(width * (original.OriginalHeight / (double)original.OriginalWidth))) : primaryValue;

        width = Math.Min(width, original.OriginalWidth);
        height = Math.Min(height, original.OriginalHeight);

        UpdateControls(width, height, scalePercent);
    }

    private void UpdateWidthOnly(int width)
    {
        UpdateResizeOptions(width, _session.Options.Resize.Height, null);
    }

    private void UpdateHeightOnly(int height)
    {
        UpdateResizeOptions(_session.Options.Resize.Width, height, null);
    }

    private void UpdateResizeOptions(int width, int height, int? scalePercent)
    {
        width = Math.Clamp(width, 1, _session.Options.Resize.OriginalWidth);
        height = Math.Clamp(height, 1, _session.Options.Resize.OriginalHeight);
        _session.Options.Resize.Width = width;
        _session.Options.Resize.Height = height;
        _session.Options.Resize.ScalePercent = scalePercent;
        UpdatePresetSelection();
        ScheduleCompressionEstimate();
    }

    private void UpdateControls(int width, int height, int? scalePercent)
    {
        _isUpdatingControls = true;
        try
        {
            _widthInput.Value = width;
            _heightInput.Value = height;
            UpdateResizeOptions(width, height, scalePercent);
        }
        finally
        {
            _isUpdatingControls = false;
        }
    }

    private void UpdatePresetSelection()
    {
        _isUpdatingControls = true;
        try
        {
            var percent = _session.Options.Resize.ScalePercent;
            _scalePresetComboBox.SelectedItem = percent.HasValue ? $"{percent.Value}%" : "Custom";
        }
        finally
        {
            _isUpdatingControls = false;
        }
    }

    private void UpdateFormatSelection()
    {
        _isUpdatingControls = true;
        try
        {
            var selected = _formatComboBox.Items
                .OfType<FormatOption>()
                .FirstOrDefault(item => string.Equals(item.Extension, _session.OutputExtension, StringComparison.OrdinalIgnoreCase));

            if (selected is null)
            {
                selected = new FormatOption(_session.OutputExtension, _session.OutputExtension.TrimStart('.').ToUpperInvariant());
                _formatComboBox.Items.Add(selected);
            }

            _formatComboBox.SelectedItem = selected ?? _formatComboBox.Items.OfType<FormatOption>().FirstOrDefault();
            _formatComboBox.Enabled = _session.TargetKind == PasteTargetKind.NewFile;
        }
        finally
        {
            _isUpdatingControls = false;
        }
    }

    private void RebuildCompressionTab()
    {
        _compressionTab.SuspendLayout();
        try
        {
            _compressionTab.Controls.Clear();

            if (_session.Options.Compression.AvailableForCurrentTarget)
            {
                _compressionTab.Enabled = true;

                if (_session.Options.Compression.TargetFormat == CompressionTargetFormat.Jpeg)
                {
                    var captionLabel = new Label
                    {
                        AutoSize = true,
                        Location = new Point(24, 18),
                        Text = "Control JPEG quality before saving.",
                        ForeColor = Color.FromArgb(91, 103, 112)
                    };

                    var compressionLabel = new Label
                    {
                        AutoSize = true,
                        Location = new Point(24, 46),
                        Text = "Quality"
                    };

                    _compressionQualityTrackBar.Minimum = 0;
                    _compressionQualityTrackBar.Maximum = 100;
                    _compressionQualityTrackBar.TickFrequency = 10;
                    _compressionQualityTrackBar.SmallChange = 1;
                    _compressionQualityTrackBar.LargeChange = 5;
                    _compressionQualityTrackBar.Location = new Point(24, 72);
                    _compressionQualityTrackBar.Width = 336;
                    _compressionQualityTrackBar.ValueChanged -= CompressionQualityTrackBarValueChanged;
                    _compressionQualityTrackBar.Value = Math.Clamp(_session.Options.Compression.JpegQuality, 0, 100);
                    _compressionQualityTrackBar.ValueChanged += CompressionQualityTrackBarValueChanged;

                    _compressionQualityValueLabel.AutoSize = true;
                    _compressionQualityValueLabel.Location = new Point(370, 78);
                    _compressionQualityValueLabel.Text = _compressionQualityTrackBar.Value.ToString();

                    _compressionEstimateLabel.AutoSize = false;
                    _compressionEstimateLabel.Location = new Point(24, 144);
                    _compressionEstimateLabel.Size = new Size(386, 40);
                    _compressionEstimateLabel.Text = "Estimated output size: calculating...";
                    _compressionEstimateLabel.ForeColor = Color.FromArgb(63, 74, 84);

                    _compressionTab.Controls.Add(captionLabel);
                    _compressionTab.Controls.Add(compressionLabel);
                    _compressionTab.Controls.Add(_compressionQualityTrackBar);
                    _compressionTab.Controls.Add(_compressionQualityValueLabel);
                    _compressionTab.Controls.Add(_compressionEstimateLabel);
                }
                else if (_session.Options.Compression.TargetFormat == CompressionTargetFormat.Png)
                {
                    var captionLabel = new Label
                    {
                        AutoSize = true,
                        Location = new Point(24, 18),
                        Text = "Apply lossless PNG optimization with oxipng.",
                        ForeColor = Color.FromArgb(91, 103, 112)
                    };

                    var compressionLabel = new Label
                    {
                        AutoSize = true,
                        Location = new Point(24, 46),
                        Text = "Optimization Level"
                    };

                    _pngOptimizationTrackBar.Minimum = 0;
                    _pngOptimizationTrackBar.Maximum = 6;
                    _pngOptimizationTrackBar.TickFrequency = 1;
                    _pngOptimizationTrackBar.SmallChange = 1;
                    _pngOptimizationTrackBar.LargeChange = 1;
                    _pngOptimizationTrackBar.Location = new Point(24, 72);
                    _pngOptimizationTrackBar.Width = 336;
                    _pngOptimizationTrackBar.ValueChanged -= PngOptimizationTrackBarValueChanged;
                    _pngOptimizationTrackBar.Value = Math.Clamp(_session.Options.Compression.PngOptimizationLevel, 0, 6);
                    _pngOptimizationTrackBar.ValueChanged += PngOptimizationTrackBarValueChanged;

                    _pngOptimizationValueLabel.AutoSize = true;
                    _pngOptimizationValueLabel.Location = new Point(370, 78);
                    _pngOptimizationValueLabel.Text = _pngOptimizationTrackBar.Value.ToString();

                    var hintLabel = new Label
                    {
                        AutoSize = false,
                        Location = new Point(24, 112),
                        Size = new Size(386, 20),
                        Text = "Higher levels are slower and usually compress better.",
                        ForeColor = Color.FromArgb(91, 103, 112)
                    };

                    _compressionEstimateLabel.AutoSize = false;
                    _compressionEstimateLabel.Location = new Point(24, 148);
                    _compressionEstimateLabel.Size = new Size(386, 44);
                    _compressionEstimateLabel.Text = "Estimated output size: calculating...";
                    _compressionEstimateLabel.ForeColor = Color.FromArgb(63, 74, 84);

                    _compressionTab.Controls.Add(captionLabel);
                    _compressionTab.Controls.Add(compressionLabel);
                    _compressionTab.Controls.Add(_pngOptimizationTrackBar);
                    _compressionTab.Controls.Add(_pngOptimizationValueLabel);
                    _compressionTab.Controls.Add(hintLabel);
                    _compressionTab.Controls.Add(_compressionEstimateLabel);
                }
            }
            else
            {
                _compressionTab.Enabled = false;
                _compressionTab.Controls.Add(new Label
                {
                    AutoSize = false,
                    Location = new Point(24, 28),
                    Size = new Size(386, 64),
                    Text = "Compression is currently available only for JPEG and PNG output.",
                    ForeColor = Color.FromArgb(91, 103, 112)
                });
            }
        }
        finally
        {
            _compressionTab.ResumeLayout();
        }
    }

    private void CompressionQualityTrackBarValueChanged(object? sender, EventArgs e)
    {
        OnCompressionQualityChanged();
    }

    private void PngOptimizationTrackBarValueChanged(object? sender, EventArgs e)
    {
        OnPngOptimizationLevelChanged();
    }

    private void OnSave()
    {
        UpdateResizeOptions((int)_widthInput.Value, (int)_heightInput.Value, _session.Options.Resize.ScalePercent);
        if (_session.Options.Compression.AvailableForCurrentTarget)
        {
            if (_session.Options.Compression.TargetFormat == CompressionTargetFormat.Jpeg)
            {
                _session.Options.Compression.JpegQuality = _compressionQualityTrackBar.Value;
                _compressionQualityValueLabel.Text = _compressionQualityTrackBar.Value.ToString();
            }
            else if (_session.Options.Compression.TargetFormat == CompressionTargetFormat.Png)
            {
                _session.Options.Compression.PngOptimizationLevel = _pngOptimizationTrackBar.Value;
                _pngOptimizationValueLabel.Text = _pngOptimizationTrackBar.Value.ToString();
            }
        }
    }

    private static int GetLinkButtonX()
    {
        var widthRight = WidthInputX + InputWidth;
        var gapCenter = widthRight + ((HeightInputX - widthRight) / 2);
        return gapCenter - (LinkButtonWidth / 2);
    }

    private static int GetLinkButtonY(Control alignedControl)
    {
        return alignedControl.Top + ((alignedControl.Height - LinkButtonHeight) / 2);
    }

    private void ScheduleCompressionEstimate()
    {
        if (!_session.Options.Compression.AvailableForCurrentTarget)
        {
            return;
        }

        _compressionEstimateTimer.Stop();
        _compressionEstimateLabel.Text = "Estimated output size: calculating...";
        _compressionEstimateTimer.Start();
    }

    private void StartCompressionEstimate()
    {
        _compressionEstimateTimer.Stop();
        if (!_session.Options.Compression.AvailableForCurrentTarget || IsDisposed)
        {
            return;
        }

        var version = Interlocked.Increment(ref _compressionEstimateVersion);
        var resizeWidth = _session.Options.Resize.Width;
        var resizeHeight = _session.Options.Resize.Height;
        var quality = _session.Options.Compression.JpegQuality;
        var pngOptimizationLevel = _session.Options.Compression.PngOptimizationLevel;
        var compressionTargetFormat = _session.Options.Compression.TargetFormat;

        _ = Task.Run(() =>
        {
            using var imageClone = new Bitmap(_session.SourceImage);
            using var transformedImage = _imageTransformPipeline.Apply(
                imageClone,
                new PasteOptions
                {
                    OutputExtension = _session.OutputExtension,
                    Resize = new ResizeOptions
                    {
                        OriginalWidth = _session.Options.Resize.OriginalWidth,
                        OriginalHeight = _session.Options.Resize.OriginalHeight,
                        Width = resizeWidth,
                        Height = resizeHeight,
                        KeepAspectRatio = _session.Options.Resize.KeepAspectRatio,
                        ScalePercent = _session.Options.Resize.ScalePercent
                    },
                    Compression = new CompressionOptions
                    {
                        Enabled = true,
                        AvailableForCurrentTarget = true,
                        TargetFormat = compressionTargetFormat,
                        JpegQuality = quality,
                        PngOptimizationLevel = pngOptimizationLevel
                    }
                });

            return compressionTargetFormat switch
            {
                CompressionTargetFormat.Jpeg => _mozJpegEncoder.EstimateSize(transformedImage, quality),
                CompressionTargetFormat.Png => _oxipngEncoder.EstimateSize(transformedImage, pngOptimizationLevel),
                _ => CompressionEstimateResult.Unavailable()
            };
        }).ContinueWith(task =>
        {
            if (IsDisposed || version != _compressionEstimateVersion)
            {
                return;
            }

            if (task.IsFaulted || task.IsCanceled)
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(() => _compressionEstimateLabel.Text = "Estimated output size: unavailable"));
                }
                return;
            }

            var estimate = task.Result;
            if (!IsHandleCreated)
            {
                return;
            }

            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || version != _compressionEstimateVersion)
                {
                    return;
                }

                _compressionEstimateLabel.Text = estimate.DisplayText;
            }));
        }, TaskScheduler.Default);
    }

    private static readonly int[] ScalePresets = [90, 80, 70, 60, 50, 40, 30, 20, 10];
}

internal sealed record FormatOption(string Extension, string Label)
{
    public override string ToString() => Label;
}

internal sealed class AspectRatioIconButton : Control
{
    public bool IsLinked { get; set; }

    public AspectRatioIconButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.Selectable,
            true);

        Cursor = Cursors.Hand;
        BackColor = SystemColors.Control;
        Size = new Size(28, 28);
        TabStop = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var iconColor = IsLinked
            ? Color.FromArgb(34, 160, 74)
            : Color.FromArgb(150, 184, 184, 184);
        var borderColor = IsLinked
            ? Color.FromArgb(190, 219, 234, 201)
            : Color.FromArgb(220, 225, 225, 225);

        using var borderPen = new Pen(borderColor);
        using var iconPen = new Pen(iconColor, 1.8f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };

        var bounds = ClientRectangle;
        bounds.Inflate(-1, -1);
        var backgroundColor = Parent?.BackColor ?? SystemColors.Control;
        using var backgroundBrush = new SolidBrush(backgroundColor);
        using var buttonBrush = new SolidBrush(Color.FromArgb(250, backgroundColor));
        e.Graphics.FillRectangle(backgroundBrush, ClientRectangle);
        e.Graphics.FillRectangle(buttonBrush, bounds);
        e.Graphics.DrawRectangle(borderPen, bounds);

        DrawChainIcon(e.Graphics, iconPen, bounds);

        if (Focused)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, bounds);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private static void DrawChainIcon(Graphics graphics, Pen pen, Rectangle bounds)
    {
        var centerY = bounds.Top + (bounds.Height / 2f);
        var leftLink = new RectangleF(bounds.Left + 4f, centerY - 4.5f, 8f, 9f);
        var rightLink = new RectangleF(bounds.Left + 15f, centerY - 4.5f, 8f, 9f);

        graphics.DrawArc(pen, leftLink, 45, 270);
        graphics.DrawArc(pen, rightLink, 225, 270);
        graphics.DrawLine(pen, bounds.Left + 10.5f, centerY, bounds.Left + 16.5f, centerY);
    }
}

internal sealed class OperationCoordinator
{
    private int _isRunning;

    public bool TryBegin() => Interlocked.CompareExchange(ref _isRunning, 1, 0) == 0;

    public void End() => Interlocked.Exchange(ref _isRunning, 0);
}

internal sealed class ImageTransformPipeline
{
    public Bitmap Apply(Image sourceImage, PasteOptions options)
    {
        ArgumentNullException.ThrowIfNull(sourceImage);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Resize.IsResizeActive)
        {
            return new Bitmap(sourceImage);
        }

        var resizedImage = new Bitmap(options.Resize.Width, options.Resize.Height);
        resizedImage.SetResolution(sourceImage.HorizontalResolution, sourceImage.VerticalResolution);

        using var graphics = Graphics.FromImage(resizedImage);
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.DrawImage(sourceImage, new Rectangle(0, 0, resizedImage.Width, resizedImage.Height));
        return resizedImage;
    }
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
    private readonly MozJpegEncoder _mozJpegEncoder = new();
    private readonly OxipngEncoder _oxipngEncoder = new();

    public ImageSaveResult ReplaceImageFile(Image image, string targetPath, PasteOptions options)
    {
        ValidateTargetFile(targetPath);

        var directory = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("Target directory could not be determined.");
        var extension = Path.GetExtension(targetPath).ToLowerInvariant();
        var tempPath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(targetPath)}.{Guid.NewGuid():N}{extension}");
        string? warningMessage = null;

        try
        {
            warningMessage = SaveImage(image, tempPath, extension, options);
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

        return new ImageSaveResult(targetPath, warningMessage);
    }

    public ImageSaveResult CreateNewImage(Image image, string folderPath, string extension, PasteOptions options)
    {
        ValidateTargetDirectory(folderPath);
        extension = NormalizeExtension(extension);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new NotSupportedException("Only png, jpg, jpeg, bmp, gif, tif, and tiff files are supported.");
        }

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidatePath = Path.Combine(folderPath, $"{GenerateRandomName()}{extension}");

            try
            {
                var warningMessage = SaveImage(image, candidatePath, ".jpg", options);
                EnsureGeneratedFileLooksValid(candidatePath);
                return new ImageSaveResult(candidatePath, warningMessage);
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

    private string? SaveImage(Image image, string targetPath, string extension, PasteOptions options)
    {
        ValidateImage(image);

        switch (extension)
        {
            case ".png":
                return SavePng(image, targetPath, options);
            case ".bmp":
                image.Save(targetPath, ImageFormat.Bmp);
                return null;
            case ".gif":
                image.Save(targetPath, ImageFormat.Gif);
                return null;
            case ".tif":
            case ".tiff":
                image.Save(targetPath, ImageFormat.Tiff);
                return null;
            case ".jpg":
            case ".jpeg":
                return SaveJpeg(image, targetPath, options);
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

    private string? SaveJpeg(Image image, string targetPath, PasteOptions options)
    {
        var quality = options.Compression.Enabled && options.Compression.AvailableForCurrentTarget
            ? options.Compression.JpegQuality
            : (int)JpegQuality;

        if (options.Compression.Enabled && options.Compression.AvailableForCurrentTarget)
        {
            var mozJpegResult = _mozJpegEncoder.TryEncode(image, targetPath, quality);
            if (mozJpegResult.Success)
            {
                return null;
            }
        }

        TryDeleteIfExists(targetPath);
        using var stream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        SaveJpeg(image, stream, quality);
        return options.Compression.Enabled && options.Compression.AvailableForCurrentTarget
            ? "mozjpeg unavailable, standard JPEG fallback used"
            : null;
    }

    private string? SavePng(Image image, string targetPath, PasteOptions options)
    {
        image.Save(targetPath, ImageFormat.Png);

        if (!options.Compression.Enabled ||
            !options.Compression.AvailableForCurrentTarget ||
            options.Compression.TargetFormat != CompressionTargetFormat.Png)
        {
            return null;
        }

        var result = _oxipngEncoder.TryOptimizeFile(targetPath, options.Compression.PngOptimizationLevel);
        return result.Success ? null : "oxipng unavailable, standard PNG fallback used";
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

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        return extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}";
    }
}

internal sealed record ImageSaveResult(string SavedPath, string? WarningMessage);

internal sealed class MozJpegEncoder
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public MozJpegEncodeResult TryEncode(Image image, string outputPath, int quality)
    {
        var executablePath = MozJpegLocator.TryFindEncoder();
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return MozJpegEncodeResult.Failed("mozjpeg executable not found.");
        }

        var tempInputPath = Path.Combine(Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory, $"{Guid.NewGuid():N}.mozjpeg-input.png");

        try
        {
            image.Save(tempInputPath, ImageFormat.Png);

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"-quality {Math.Clamp(quality, 0, 100)} -outfile \"{outputPath}\" \"{tempInputPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return MozJpegEncodeResult.Failed("mozjpeg process could not be started.");
            }

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                TryKill(process);
                return MozJpegEncodeResult.Failed("mozjpeg process timed out.");
            }

            var standardError = process.StandardError.ReadToEnd();
            var standardOutput = process.StandardOutput.ReadToEnd();
            if (process.ExitCode != 0)
            {
                return MozJpegEncodeResult.Failed(string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError);
            }

            return File.Exists(outputPath)
                ? MozJpegEncodeResult.Succeeded()
                : MozJpegEncodeResult.Failed("mozjpeg did not create the output file.");
        }
        catch (Exception ex)
        {
            return MozJpegEncodeResult.Failed(ex.Message);
        }
        finally
        {
            TryDeleteFile(tempInputPath);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
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

    public CompressionEstimateResult EstimateSize(Image image, int quality)
    {
        var tempOutputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jpg");

        try
        {
            var encodeResult = TryEncode(image, tempOutputPath, quality);
            if (encodeResult.Success && File.Exists(tempOutputPath))
            {
                return CompressionEstimateResult.FromBytes(new FileInfo(tempOutputPath).Length, usedFallback: false);
            }

            using var stream = new MemoryStream();
            SaveStandardJpeg(image, stream, quality);
            return CompressionEstimateResult.FromBytes(stream.Length, usedFallback: true);
        }
        finally
        {
            TryDeleteFile(tempOutputPath);
        }
    }

    private static void SaveStandardJpeg(Image image, Stream targetStream, long quality)
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
}

internal static class MozJpegLocator
{
    public static string? TryFindEncoder()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "Tools", "mozjpeg", "win-x64", "cjpeg-static.exe"),
            Path.Combine(baseDirectory, "cjpeg-static.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}

internal sealed record MozJpegEncodeResult(bool Success, string? ErrorMessage)
{
    public static MozJpegEncodeResult Succeeded() => new(true, null);
    public static MozJpegEncodeResult Failed(string? errorMessage) => new(false, errorMessage);
}

internal sealed class OxipngEncoder
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public OxipngOptimizeResult TryOptimizeFile(string filePath, int optimizationLevel)
    {
        var executablePath = OxipngLocator.TryFindEncoder();
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return OxipngOptimizeResult.Failed("oxipng executable not found.");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"-o {Math.Clamp(optimizationLevel, 0, 6)} --strip safe --alpha -q \"{filePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return OxipngOptimizeResult.Failed("oxipng process could not be started.");
            }

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                TryKill(process);
                return OxipngOptimizeResult.Failed("oxipng process timed out.");
            }

            var standardError = process.StandardError.ReadToEnd();
            var standardOutput = process.StandardOutput.ReadToEnd();
            if (process.ExitCode != 0)
            {
                return OxipngOptimizeResult.Failed(string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError);
            }

            return File.Exists(filePath)
                ? OxipngOptimizeResult.Succeeded()
                : OxipngOptimizeResult.Failed("oxipng did not preserve the output file.");
        }
        catch (Exception ex)
        {
            return OxipngOptimizeResult.Failed(ex.Message);
        }
    }

    public CompressionEstimateResult EstimateSize(Image image, int optimizationLevel)
    {
        var tempOutputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");

        try
        {
            image.Save(tempOutputPath, ImageFormat.Png);
            var standardSize = new FileInfo(tempOutputPath).Length;
            var optimizeResult = TryOptimizeFile(tempOutputPath, optimizationLevel);
            if (optimizeResult.Success && File.Exists(tempOutputPath))
            {
                return CompressionEstimateResult.FromBytes(new FileInfo(tempOutputPath).Length, usedFallback: false);
            }

            return CompressionEstimateResult.FromBytes(standardSize, usedFallback: true);
        }
        finally
        {
            TryDeleteFile(tempOutputPath);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
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
}

internal static class OxipngLocator
{
    public static string? TryFindEncoder()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "Tools", "oxipng", "win-x64", "oxipng.exe"),
            Path.Combine(baseDirectory, "oxipng.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}

internal sealed record OxipngOptimizeResult(bool Success, string? ErrorMessage)
{
    public static OxipngOptimizeResult Succeeded() => new(true, null);
    public static OxipngOptimizeResult Failed(string? errorMessage) => new(false, errorMessage);
}

internal sealed record CompressionEstimateResult(string DisplayText)
{
    public static CompressionEstimateResult FromBytes(long bytes, bool usedFallback)
    {
        var kiloBytes = Math.Max(1, (int)Math.Round(bytes / 1024d));
        return new CompressionEstimateResult(
            usedFallback
                ? $"Estimated output size: {kiloBytes} KB (standard estimate)"
                : $"Estimated output size: {kiloBytes} KB");
    }

    public static CompressionEstimateResult Unavailable()
    {
        return new CompressionEstimateResult("Estimated output size: unavailable");
    }
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
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}

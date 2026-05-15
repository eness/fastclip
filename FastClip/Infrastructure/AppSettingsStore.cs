using System.Text.Json;
using FastClip.Interop;
using FastClip.Models;

namespace FastClip.Infrastructure;

internal sealed class AppSettingsStore : IAppSettingsStore
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

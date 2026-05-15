namespace FastClip.Models;

internal sealed record AppSettings(
    HotkeyRegistration Hotkey,
    bool AdvancedModeEnabled,
    bool AutoApplyAdvancedSettings,
    AdvancedSettingsSnapshot? SavedAdvancedSettings)
{
    public static AppSettings Default { get; } = new(HotkeyRegistration.Default, false, false, null);
}

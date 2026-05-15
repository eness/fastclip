namespace FastClip.Models;

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

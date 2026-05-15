using FastClip.Models;

namespace FastClip.Infrastructure;

internal interface IAppSettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

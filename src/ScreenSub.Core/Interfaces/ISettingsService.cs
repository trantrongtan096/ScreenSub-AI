using ScreenSub.Core.Models;

namespace ScreenSub.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Settings { get; }
    Task LoadSettingsAsync();
    Task SaveSettingsAsync();
    string GetSecret(string key);
    void SetSecret(string key, string value);
}


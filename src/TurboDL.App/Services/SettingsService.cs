using System.IO;
using TurboDL.Core.Categories;
using TurboDL.Core.Engine;
using TurboDL.Core.Settings;

namespace TurboDL.App.Services;

/// <summary>Holds the current settings and pushes every change to the parts of the app that depend on it.</summary>
public sealed class SettingsService
{
    private readonly SettingsStore _store;
    private readonly CategoryRules _rules;
    private readonly RateLimiter _globalLimiter;

    public SettingsService(SettingsStore store, CategoryRules rules, RateLimiter globalLimiter)
    {
        _store = store;
        _rules = rules;
        _globalLimiter = globalLimiter;
        Current = store.Load();
        ApplyToEngine();
    }

    public AppSettings Current { get; private set; }

    public static string DefaultDownloadFolder => Path.Combine(KnownFolders.Downloads, "TurboDL");

    public string DownloadFolder =>
        string.IsNullOrWhiteSpace(Current.DownloadFolder) ? DefaultDownloadFolder : Current.DownloadFolder;

    /// <summary>Raised on the UI thread after new settings are saved and applied.</summary>
    public event Action<AppSettings>? Changed;

    /// <summary>A copy to edit; nothing changes until it goes back through <see cref="Apply"/>.</summary>
    public AppSettings Edit() => SettingsStore.Clone(Current);

    public void Apply(AppSettings settings)
    {
        Current = settings;
        Save();
        ApplyToEngine();
        Changed?.Invoke(Current);
    }

    /// <summary>Saves small runtime state (like whether the queue was running) without a full re-apply.</summary>
    public void Save() => _store.Save(Current);

    private void ApplyToEngine()
    {
        _globalLimiter.BytesPerSecond = Current.SpeedLimitKBps * 1024;
        _rules.Update(DownloadFolder, Current.Categories.Select(c => (c.Name, c.ExtensionList())));
    }
}

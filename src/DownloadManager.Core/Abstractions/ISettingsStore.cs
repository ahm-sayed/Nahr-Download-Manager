namespace DownloadManager.Core.Abstractions;

/// <summary>Durable key/value store for user settings.</summary>
public interface ISettingsStore
{
    string? Get(string key);
    void Set(string key, string value);
    IReadOnlyDictionary<string, string> GetAll();
}

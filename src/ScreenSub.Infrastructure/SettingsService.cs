using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;

namespace ScreenSub.Infrastructure;

public sealed class SettingsService : ISettingsService
{
    private readonly string _dbPath;
    private readonly string _settingsFilePath;
    private readonly WindowsSecretStore _secretStore;
    private AppSettings _settings;

    public AppSettings Settings => _settings;

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(appData, "ScreenSubAI");
        Directory.CreateDirectory(dir);

        _dbPath = Path.Combine(dir, "screensub.db");
        _settingsFilePath = Path.Combine(dir, "settings.json");
        _secretStore = new WindowsSecretStore();
        _settings = new AppSettings();

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Secrets (
                Key TEXT PRIMARY KEY,
                CipherValue TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS TranslationHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceText TEXT NOT NULL,
                DetectedLanguage TEXT,
                TranslatedText TEXT NOT NULL,
                TargetLanguage TEXT NOT NULL,
                CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            );
        ";
        cmd.ExecuteNonQuery();
    }

    public async Task LoadSettingsAsync()
    {
        if (File.Exists(_settingsFilePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    _settings = loaded;
                }
            }
            catch { }
        }

        // Load protected AzureApiKey if exists
        var secretKey = GetSecret("AzureApiKey");
        if (!string.IsNullOrWhiteSpace(secretKey))
        {
            _settings.AzureApiKey = secretKey;
        }
    }

    public async Task SaveSettingsAsync()
    {
        try
        {
            // Protect secret key
            if (!string.IsNullOrWhiteSpace(_settings.AzureApiKey))
            {
                SetSecret("AzureApiKey", _settings.AzureApiKey);
            }

            // Clone settings without plaintext sensitive key to disk
            var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
            copy.AzureApiKey = string.Empty; // Do not store plaintext in JSON!

            var json = JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_settingsFilePath, json);
        }
        catch { }
    }

    public string GetSecret(string key)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CipherValue FROM Secrets WHERE Key = @Key";
            cmd.Parameters.AddWithValue("@Key", key);

            var cipher = cmd.ExecuteScalar() as string;
            return string.IsNullOrEmpty(cipher) ? string.Empty : _secretStore.Unprotect(cipher);
        }
        catch
        {
            return string.Empty;
        }
    }

    public void SetSecret(string key, string value)
    {
        try
        {
            var cipher = _secretStore.Protect(value);

            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Secrets (Key, CipherValue) VALUES (@Key, @Val)
                ON CONFLICT(Key) DO UPDATE SET CipherValue = excluded.CipherValue;
            ";
            cmd.Parameters.AddWithValue("@Key", key);
            cmd.Parameters.AddWithValue("@Val", cipher);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }
}

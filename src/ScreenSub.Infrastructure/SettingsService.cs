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

    public SettingsService(string? customDirectory = null)
    {
        var dir = customDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenSubAI");
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

        // Load protected API keys if exist
        var secretKey = GetSecret("AzureApiKey");
        if (!string.IsNullOrWhiteSpace(secretKey))
        {
            _settings.AzureApiKey = secretKey.Trim();
        }

        var geminiSecret = GetSecret("GeminiApiKey");
        if (!string.IsNullOrWhiteSpace(geminiSecret))
        {
            _settings.GeminiApiKey = geminiSecret.Trim();
        }

        // Migrate legacy default Online mode when no Online API Key is configured and user did not explicitly select Online mode
        if (_settings.TranslationEngineMode == Core.Enums.TranslationEngineMode.Online &&
            string.IsNullOrWhiteSpace(_settings.AzureApiKey) &&
            string.IsNullOrWhiteSpace(_settings.GeminiApiKey) &&
            !_settings.HasExplicitlySelectedOnlineMode)
        {
            _settings.TranslationEngineMode = Core.Enums.TranslationEngineMode.Offline;
        }

        // Opt-in controlled EN<->VI Multi-Model Beta profile via environment variable without altering persisted defaults
        string? betaEnv = Environment.GetEnvironmentVariable("SCREENSUB_BETA_ROUTER");
        if (string.Equals(betaEnv, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(betaEnv, "true", StringComparison.OrdinalIgnoreCase))
        {
            long ramBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            _settings.ApplyBetaEnViRouterProfile(ramBytes);
        }
    }

    public async Task SaveSettingsAsync()
    {
        try
        {
            _settings.AzureApiKey = _settings.AzureApiKey?.Trim() ?? string.Empty;
            _settings.AzureRegion = _settings.AzureRegion?.Trim() ?? string.Empty;
            _settings.AzureEndpoint = string.IsNullOrWhiteSpace(_settings.AzureEndpoint)
                ? "https://api.cognitive.microsofttranslator.com"
                : _settings.AzureEndpoint.Trim().TrimEnd('/');
            _settings.GeminiApiKey = _settings.GeminiApiKey?.Trim() ?? string.Empty;
            _settings.GeminiModel = string.IsNullOrWhiteSpace(_settings.GeminiModel)
                ? "gemini-2.5-flash"
                : _settings.GeminiModel.Trim();

            // Protect or clear secret keys via Windows DPAPI
            if (!string.IsNullOrWhiteSpace(_settings.AzureApiKey))
            {
                SetSecret("AzureApiKey", _settings.AzureApiKey);
            }
            else
            {
                DeleteSecret("AzureApiKey");
            }

            if (!string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
            {
                SetSecret("GeminiApiKey", _settings.GeminiApiKey);
            }
            else
            {
                DeleteSecret("GeminiApiKey");
            }

            // Clone settings without plaintext sensitive keys to disk
            var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
            copy.AzureApiKey = string.Empty; // Do not store plaintext in JSON!
            copy.GeminiApiKey = string.Empty; // Do not store plaintext in JSON!

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

    public void DeleteSecret(string key)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Secrets WHERE Key = @Key";
            cmd.Parameters.AddWithValue("@Key", key);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }
}


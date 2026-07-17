using System.Text.Json;
using LocalPdfReader.Application.Configuration;
using Microsoft.Extensions.Logging;

namespace LocalPdfReader.Infrastructure.Configuration;

public sealed class JsonSettingsService(string settingsFilePath, ILogger<JsonSettingsService> logger) : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(settingsFilePath))
        {
            var defaults = new AppSettings();
            await SaveAsync(defaults, cancellationToken);
            logger.LogInformation(new EventId(2000, "SettingsCreated"), "Created default settings file.");
            return defaults;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(settingsFilePath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(new EventId(2005, "SettingsReadUnavailable"), exception,
                "Settings could not be read. In-memory defaults will be used.");
            return new AppSettings();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var declaredSchemaVersion = ReadDeclaredSchemaVersion(document.RootElement);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions)
                ?? throw new InvalidDataException("The settings file does not contain a valid settings object.");

            if (declaredSchemaVersion is null or < AppSettings.CurrentSchemaVersion)
            {
                settings = AppSettingsMigrator.Migrate(settings, declaredSchemaVersion);
                await TryPersistMigrationAsync(settings, cancellationToken);
            }

            logger.LogInformation(new EventId(2001, "SettingsLoaded"),
                "Loaded application settings schema {SchemaVersion}.", settings.SchemaVersion);
            return settings;
        }
        catch (JsonException exception)
        {
            logger.LogError(new EventId(2006, "SettingsJsonInvalid"), exception,
                "Settings JSON is invalid. Defaults will be used and the invalid file will be preserved.");
            await TryRecoverInvalidSettingsAsync(cancellationToken);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        var directoryPath = Path.GetDirectoryName(settingsFilePath)
            ?? throw new InvalidOperationException("The settings file path must include a directory.");
        Directory.CreateDirectory(directoryPath);

        var temporaryFilePath = $"{settingsFilePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = File.Create(temporaryFilePath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
            }

            File.Move(temporaryFilePath, settingsFilePath, overwrite: true);
            logger.LogInformation(new EventId(2002, "SettingsSaved"), "Saved application settings.");
        }
        finally
        {
            if (File.Exists(temporaryFilePath))
            {
                File.Delete(temporaryFilePath);
            }
        }
    }

    private async Task TryPersistMigrationAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(settingsFilePath)
            ?? throw new InvalidOperationException("The settings file path must include a directory.");
        var backupPath = Path.Combine(directoryPath, "settings.v0.1.backup.json");

        try
        {
            if (!File.Exists(backupPath))
            {
                File.Copy(settingsFilePath, backupPath);
            }

            await SaveAsync(settings, cancellationToken);
            logger.LogInformation(new EventId(2003, "SettingsMigrated"),
                "Migrated application settings to schema {SchemaVersion}.", settings.SchemaVersion);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 迁移无法落盘时仍返回内存中的兼容设置，避免设置目录故障阻止基本阅读。
            logger.LogWarning(new EventId(2004, "SettingsMigrationNotPersisted"), exception,
                "Settings migration could not be persisted. Compatible in-memory settings will be used.");
        }
    }

    private async Task TryRecoverInvalidSettingsAsync(CancellationToken cancellationToken)
    {
        var directoryPath = Path.GetDirectoryName(settingsFilePath);
        if (directoryPath is null)
        {
            return;
        }

        try
        {
            var backupPath = Path.Combine(directoryPath, "settings.invalid.backup.json");
            if (!File.Exists(backupPath))
            {
                File.Copy(settingsFilePath, backupPath);
            }

            await SaveAsync(new AppSettings(), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(new EventId(2007, "SettingsRecoveryNotPersisted"), exception,
                "Default settings could not be persisted after invalid JSON was detected.");
        }
    }

    private static int? ReadDeclaredSchemaVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "schemaVersion", StringComparison.OrdinalIgnoreCase) &&
                property.Value.TryGetInt32(out var version))
            {
                return version;
            }
        }

        return null;
    }
}

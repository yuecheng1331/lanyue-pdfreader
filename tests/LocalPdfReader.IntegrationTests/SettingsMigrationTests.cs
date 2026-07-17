using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Infrastructure.Configuration;
using LocalPdfReader.Infrastructure.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalPdfReader.IntegrationTests;

public sealed class SettingsMigrationTests
{
    [Fact]
    public async Task MissingSettingsFileCreatesCurrentDefaults()
    {
        var directoryPath = CreateTemporaryDirectory();
        var settingsPath = Path.Combine(directoryPath, "settings.json");
        var service = new JsonSettingsService(settingsPath, NullLogger<JsonSettingsService>.Instance);

        try
        {
            var settings = await service.LoadAsync(CancellationToken.None);

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal("deepseek-chat", settings.Translation.Model);
            Assert.Equal("Ctrl+F", settings.Shortcuts.SearchShortcut);
            Assert.Equal("Ctrl+T", settings.Shortcuts.TranslationShortcut);
            Assert.Equal("Yellow", settings.Annotations.DefaultColor);
            Assert.True(File.Exists(settingsPath));
            Assert.False(File.Exists(Path.Combine(directoryPath, "settings.v0.1.backup.json")));
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task V01SettingsAreBackedUpAndMigratedWithoutLosingTranslationValues()
    {
        const string legacyJson = """
            {
              "schemaVersion": 1,
              "translation": {
                "provider": "DeepSeek",
                "baseUrl": "https://api.deepseek.com",
                "model": "legacy-model",
                "targetLanguage": "en-US",
                "defaultPreset": "Literal",
                "timeoutSeconds": 75,
                "stream": false
              },
              "reader": {
                "defaultZoomMode": "FitPage",
                "minimumZoom": 0.2,
                "maximumZoom": 6.0,
                "rememberLastFile": true
              },
              "privacy": {
                "allowTranslationNetworkAccess": false,
                "logSourceText": false
              }
            }
            """;
        var directoryPath = CreateTemporaryDirectory();
        var settingsPath = Path.Combine(directoryPath, "settings.json");
        await File.WriteAllTextAsync(settingsPath, legacyJson);
        var service = new JsonSettingsService(settingsPath, NullLogger<JsonSettingsService>.Instance);

        try
        {
            var settings = await service.LoadAsync(CancellationToken.None);

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal("legacy-model", settings.Translation.Model);
            Assert.Equal("en-US", settings.Translation.TargetLanguage);
            Assert.Equal(75, settings.Translation.TimeoutSeconds);
            Assert.False(settings.Translation.Stream);
            Assert.Equal("FitPage", settings.Reader.DefaultZoomMode);
            Assert.True(settings.Reader.RememberLastFile);
            Assert.False(settings.Privacy.AllowTranslationNetworkAccess);
            Assert.True(settings.Session.RestoreLastDocument);
            Assert.True(settings.Session.SaveReadingPosition);
            Assert.True(settings.History.RecordRecentDocuments);
            Assert.Equal(20, settings.History.MaximumRecentDocuments);
            Assert.Equal("Ctrl+F", settings.Shortcuts.SearchShortcut);
            Assert.Equal("Ctrl+T", settings.Shortcuts.TranslationShortcut);
            Assert.Equal("Yellow", settings.Annotations.DefaultColor);

            var backupPath = Path.Combine(directoryPath, "settings.v0.1.backup.json");
            Assert.Equal(legacyJson, await File.ReadAllTextAsync(backupPath));
            using var migratedJson = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
            Assert.Equal(AppSettings.CurrentSchemaVersion, migratedJson.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.True(migratedJson.RootElement.TryGetProperty("session", out _));
            Assert.True(migratedJson.RootElement.TryGetProperty("history", out _));
            Assert.True(migratedJson.RootElement.TryGetProperty("search", out _));
            Assert.True(migratedJson.RootElement.TryGetProperty("shortcuts", out _));
            Assert.True(migratedJson.RootElement.TryGetProperty("annotations", out _));
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task SettingsWithoutSchemaVersionAreTreatedAsV01()
    {
        const string legacyJson = """{ "translation": { "model": "schema-less-model" } }""";
        var directoryPath = CreateTemporaryDirectory();
        var settingsPath = Path.Combine(directoryPath, "settings.json");
        await File.WriteAllTextAsync(settingsPath, legacyJson);
        var service = new JsonSettingsService(settingsPath, NullLogger<JsonSettingsService>.Instance);

        try
        {
            var settings = await service.LoadAsync(CancellationToken.None);

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal("schema-less-model", settings.Translation.Model);
            Assert.True(File.Exists(Path.Combine(directoryPath, "settings.v0.1.backup.json")));
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task PartialV01SettingsFillMissingGroupsWithoutLosingPresentValues()
    {
        const string legacyJson = """
            {
              "schemaVersion": 1,
              "translation": { "model": "partial-model" },
              "reader": null,
              "privacy": null
            }
            """;
        var directoryPath = CreateTemporaryDirectory();
        var settingsPath = Path.Combine(directoryPath, "settings.json");
        await File.WriteAllTextAsync(settingsPath, legacyJson);
        var service = new JsonSettingsService(settingsPath, NullLogger<JsonSettingsService>.Instance);

        try
        {
            var settings = await service.LoadAsync(CancellationToken.None);

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal("partial-model", settings.Translation.Model);
            Assert.NotNull(settings.Reader);
            Assert.NotNull(settings.Session);
            Assert.NotNull(settings.History);
            Assert.NotNull(settings.Search);
            Assert.NotNull(settings.Shortcuts);
            Assert.NotNull(settings.Annotations);
            Assert.NotNull(settings.Privacy);
            Assert.NotNull(settings.Diagnostics);
            Assert.True(File.Exists(Path.Combine(directoryPath, "settings.v0.1.backup.json")));
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task CurrentV10SettingsLoadWithoutCreatingALegacyBackup()
    {
        const string currentJson = """
            {
              "schemaVersion": 4,
              "translation": { "model": "current-model" },
              "shortcuts": { "searchShortcut": "Ctrl+Shift+F", "translationShortcut": "Ctrl+Shift+T" },
              "annotations": { "defaultColor": "Pink" }
            }
            """;
        var directoryPath = CreateTemporaryDirectory();
        var settingsPath = Path.Combine(directoryPath, "settings.json");
        await File.WriteAllTextAsync(settingsPath, currentJson);
        var service = new JsonSettingsService(settingsPath, NullLogger<JsonSettingsService>.Instance);

        try
        {
            var settings = await service.LoadAsync(CancellationToken.None);

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal("current-model", settings.Translation.Model);
            Assert.Equal("Ctrl+Shift+F", settings.Shortcuts.SearchShortcut);
            Assert.Equal("Pink", settings.Annotations.DefaultColor);
            Assert.False(File.Exists(Path.Combine(directoryPath, "settings.v0.1.backup.json")));
            Assert.Equal(currentJson, await File.ReadAllTextAsync(settingsPath));
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidSettingsArePreservedAndDoNotPreventDefaultsFromLoading()
    {
        const string invalidJson = "{ invalid-json";
        var directoryPath = CreateTemporaryDirectory();
        var settingsPath = Path.Combine(directoryPath, "settings.json");
        await File.WriteAllTextAsync(settingsPath, invalidJson);
        var service = new JsonSettingsService(settingsPath, NullLogger<JsonSettingsService>.Instance);

        try
        {
            var settings = await service.LoadAsync(CancellationToken.None);

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal(invalidJson, await File.ReadAllTextAsync(
                Path.Combine(directoryPath, "settings.invalid.backup.json")));
            using var recoveredJson = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
            Assert.Equal(AppSettings.CurrentSchemaVersion, recoveredJson.RootElement.GetProperty("schemaVersion").GetInt32());
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    [Fact]
    public void ApplicationInfoUsesTheUnifiedProductVersion()
    {
        var provider = new ApplicationInfoProvider(
            typeof(ApplicationInfoProvider).Assembly,
            AppContext.BaseDirectory,
            "C:\\settings",
            "C:\\logs");

        var applicationInfo = provider.GetApplicationInfo();

        Assert.Equal("1.0.0", applicationInfo.ProductVersion);
        Assert.Equal("澜阅 PDF", applicationInfo.ProductName);
        Assert.Equal("C:\\settings", applicationInfo.SettingsDirectory);
        Assert.Equal("C:\\logs", applicationInfo.LogDirectory);
        Assert.Contains(".NET", applicationInfo.DotNetVersion);
        Assert.StartsWith("151.0.7920", applicationInfo.PdfiumVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAndPublishUseTheSameVersionSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var buildProperties = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Build.props"));
        var versionElements = buildProperties.Descendants("Version").ToArray();
        var publishScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "Publish-WindowsX64.ps1"));

        Assert.Single(versionElements);
        Assert.Equal("1.0.0", versionElements[0].Value);
        Assert.Contains("$buildPropertiesPath", publishScript, StringComparison.Ordinal);
        Assert.Contains("buildProperties.Load($buildPropertiesPath)", publishScript, StringComparison.Ordinal);
        Assert.Contains("product = $productName", publishScript, StringComparison.Ordinal);
        Assert.Contains("LanyuePDF-$version-win-x64.zip", publishScript, StringComparison.Ordinal);
        Assert.Contains("dotnet test $solutionPath -c Release", publishScript, StringComparison.Ordinal);
        Assert.Contains("Expand-Archive", publishScript, StringComparison.Ordinal);
        Assert.Contains("DOTNET_MULTILEVEL_LOOKUP", publishScript, StringComparison.Ordinal);
        Assert.Contains("Assert-NoSensitiveFiles", publishScript, StringComparison.Ordinal);
        Assert.Contains("e_sqlite3.dll", publishScript, StringComparison.Ordinal);
        Assert.Contains("LanyuePDF.exe", publishScript, StringComparison.Ordinal);
        Assert.Contains("--untracked-files=all -- $buildInputPaths", publishScript, StringComparison.Ordinal);

        var releaseChecklistPath = Path.Combine(
            repositoryRoot,
            "release",
            "V1.0-Release-Checklist.md");
        Assert.True(File.Exists(releaseChecklistPath));
        var releaseChecklist = File.ReadAllText(releaseChecklistPath);
        Assert.Contains("v1.0.0", releaseChecklist, StringComparison.Ordinal);
        Assert.Contains("dirtyBuild", releaseChecklist, StringComparison.Ordinal);
    }

    private static string CreateTemporaryDirectory()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"LocalPdfReader.SettingsMigration.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalPdfReader.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the LocalPdfReader repository root.");
    }
}

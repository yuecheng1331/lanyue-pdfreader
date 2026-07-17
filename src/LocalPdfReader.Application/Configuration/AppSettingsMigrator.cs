namespace LocalPdfReader.Application.Configuration;

public static class AppSettingsMigrator
{
    public static AppSettings Migrate(AppSettings settings, int? declaredSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var sourceVersion = declaredSchemaVersion ?? 1;
        if (sourceVersion > AppSettings.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Settings schema {sourceVersion} is newer than supported schema {AppSettings.CurrentSchemaVersion}.");
        }

        // 缺失分组由属性初始化器处理；显式 null 也要补齐，避免残缺旧设置导致启动失败。
        return settings with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            Translation = settings.Translation ?? new TranslationSettings(),
            TranslationStyles = settings.TranslationStyles ?? new TranslationStyleSettings(),
            Reader = settings.Reader ?? new ReaderSettings(),
            Session = settings.Session ?? new SessionSettings(),
            History = settings.History ?? new HistorySettings(),
            Search = settings.Search ?? new SearchSettings(),
            Shortcuts = settings.Shortcuts ?? new ShortcutSettings(),
            Annotations = settings.Annotations ?? new AnnotationSettings(),
            Privacy = settings.Privacy ?? new PrivacySettings(),
            Diagnostics = settings.Diagnostics ?? new DiagnosticsSettings()
        };
    }
}

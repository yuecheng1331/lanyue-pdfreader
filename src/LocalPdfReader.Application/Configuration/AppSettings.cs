namespace LocalPdfReader.Application.Configuration;

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public TranslationSettings Translation { get; init; } = new();

    public TranslationStyleSettings TranslationStyles { get; init; } = new();

    public ReaderSettings Reader { get; init; } = new();

    public SessionSettings Session { get; init; } = new();

    public HistorySettings History { get; init; } = new();

    public SearchSettings Search { get; init; } = new();

    public ShortcutSettings Shortcuts { get; init; } = new();

    public AnnotationSettings Annotations { get; init; } = new();

    public PrivacySettings Privacy { get; init; } = new();

    public DiagnosticsSettings Diagnostics { get; init; } = new();
}

public sealed record TranslationSettings
{
    public string Provider { get; init; } = "DeepSeek";

    public string BaseUrl { get; init; } = "https://api.deepseek.com";

    public string Model { get; init; } = "deepseek-chat";

    public string TargetLanguage { get; init; } = "zh-CN";

    public string DefaultPreset { get; init; } = "Academic";

    public int TimeoutSeconds { get; init; } = 60;

    public bool Stream { get; init; } = true;

    public int MaximumCacheEntries { get; init; } = 500;
}

public sealed record TranslationStyleSettings
{
    public string DefaultStyleId { get; init; } = "preset-academic";

    public IReadOnlyList<TranslationStyleDefinition> Items { get; init; } =
        TranslationStyleDefinition.CreateDefaults();
}

public sealed record TranslationStyleDefinition(
    string Id,
    string Name,
    string Preset,
    string Prompt,
    bool IsBuiltIn,
    bool IsDeleted)
{
    public static IReadOnlyList<TranslationStyleDefinition> CreateDefaults() =>
    [
        new("preset-literal", "直译", "Literal", "尽量逐句忠实翻译，不添加解释或改写。", true, false),
        new("preset-fluent", "流畅", "Fluent", "生成通顺、准确、自然的译文，并保留原有段落结构。", true, false),
        new("preset-academic", "学术论文", "Academic", "使用准确、正式的学术表达，保持专业术语一致，不得省略限定、否定、比较和因果关系。", true, false),
        new("preset-computer-science", "计算机科学", "ComputerScience", "保留算法名、模型名、数据集名、变量名和常见英文缩写，使用通用计算机术语译法。", true, false),
        new("preset-medical", "医学", "Medical", "使用规范医学术语，不得弱化诊断、风险、统计显著性和因果关系。", true, false),
        new("custom-default", "自定义", "Custom", "根据用户提供的风格提示词翻译，保持术语一致、语义准确，并保留原文结构。", false, false)
    ];
}

public sealed record ReaderSettings
{
    public string DefaultZoomMode { get; init; } = "FitWidth";

    public double MinimumZoom { get; init; } = 0.1;

    public double MaximumZoom { get; init; } = 8.0;

    public bool RememberLastFile { get; init; }
}

public sealed record SessionSettings
{
    public bool RestoreLastDocument { get; init; } = true;

    public bool SaveReadingPosition { get; init; } = true;
}

public sealed record HistorySettings
{
    public bool RecordRecentDocuments { get; init; } = true;

    public int MaximumRecentDocuments { get; init; } = 20;
}

public sealed record SearchSettings
{
    public bool MatchCase { get; init; }

    public bool WholeWord { get; init; }
}

public sealed record ShortcutSettings
{
    public string SearchShortcut { get; init; } = "Ctrl+F";

    public string TranslationShortcut { get; init; } = "Ctrl+T";
}

public sealed record AnnotationSettings
{
    public string DefaultColor { get; init; } = "Yellow";
}

public sealed record PrivacySettings
{
    public bool AllowTranslationNetworkAccess { get; init; } = true;

    public bool LogSourceText { get; init; }
}

public sealed record DiagnosticsSettings
{
    public string LogLevel { get; init; } = "Information";
}

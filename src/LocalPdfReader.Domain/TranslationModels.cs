namespace LocalPdfReader.Domain;

public sealed record TranslationRequest(
    Guid RequestId,
    string SourceText,
    string TargetLanguage,
    TranslationPreset Preset,
    string? SourceLanguage = null,
    string? CustomInstruction = null,
    string? StyleInstruction = null,
    string? GlossaryInstruction = null,
    string? PreferenceInstruction = null,
    string? SourceScope = null,
    TranslationRequestPurpose Purpose = TranslationRequestPurpose.Translate);

public sealed record TranslationChunk(
    Guid RequestId,
    string Text,
    bool IsCompleted);

public enum TranslationPreset
{
    Literal,
    Fluent,
    Academic,
    ComputerScience,
    Medical,
    Custom
}

public enum TranslationRequestPurpose
{
    Translate,
    StylePromptDraft
}

public enum TranslationSourceKind
{
    Selection,
    CurrentPage,
    PageRange
}

public enum TranslationSampleKind
{
    None,
    Preferred,
    Rejected
}

public sealed record TranslationCacheEntry(
    string CacheKey,
    string SourceText,
    string TranslatedText,
    string TargetLanguage,
    TranslationPreset Preset,
    string? CustomInstruction,
    string? GlossaryInstruction,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    int UseCount);

public sealed record TranslationCacheSegment(
    string CacheKey,
    int SegmentIndex,
    string SourceText,
    string NormalizedSourceText,
    string TranslatedText,
    string TargetLanguage,
    TranslationPreset Preset,
    string? CustomInstruction,
    string? GlossaryInstruction,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    int UseCount);

public sealed record TranslationHistoryEntry(
    Guid HistoryId,
    string SourcePreview,
    string SourceText,
    string TranslatedText,
    string TargetLanguage,
    TranslationPreset Preset,
    TranslationSourceKind SourceKind,
    string? SourceScope,
    DateTimeOffset CreatedAt,
    int CharacterCount,
    int SegmentCount,
    bool UsedCache,
    TranslationSampleKind SampleKind);

public sealed record TranslationGlossaryEntry(
    Guid EntryId,
    string SourceTerm,
    string TargetTerm,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt);

using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using LocalPdfReader.App.Commands;
using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Application.Persistence;
using LocalPdfReader.Application.Translation;
using LocalPdfReader.Domain;

namespace LocalPdfReader.App;

public sealed class TranslationPanelViewModel : INotifyPropertyChanged
{
    private const int MaximumSegmentLength = 1800;
    private const int MaximumRecentHistoryItems = 30;
    private const int MaximumSampleInstructionItems = 5;
    private const int MaximumSamplePreviewLength = 160;
    private const int MaximumSegmentContextLength = 600;
    private const int MaximumReusableCacheCandidates = 50;
    private const int MaximumReusableCacheSegmentCandidates = 500;
    private const double MinimumReusableSegmentSimilarity = 0.9;
    private const double MinimumReusableWindowSimilarity = 0.94;

    private readonly ITranslationService _translationService;
    private readonly ISettingsService _settingsService;
    private readonly IClipboardService _clipboardService;
    private readonly IUserErrorService _userErrorService;
    private readonly ITranslationMemoryRepository? _translationMemoryRepository;
    private readonly IWordTranslationService? _wordTranslationService;
    private readonly AsyncCommand[] _commands;
    private string _sourceText = string.Empty;
    private string _translatedText = string.Empty;
    private TranslationPreset _selectedPreset = TranslationPreset.Academic;
    private string _targetLanguage = "zh-CN";
    private string _customInstruction = string.Empty;
    private string _customStyleName = "自定义风格";
    private string _customStylePrompt = "根据用户提供的风格提示词翻译，保持术语一致、语义准确，并保留原文结构。";
    private string _stylePromptRequirement = string.Empty;
    private string _stylePromptStatusText = "提示词会直接影响翻译效果，修改后需要通过多次翻译自行调试。";
    private readonly List<TranslationStyleDefinition> _translationStyleDefinitions = [];
    private TranslationStyleItemViewModel? _selectedStyleOption;
    private TranslationStyleItemViewModel? _selectedManagedStyle;
    private string _managedStyleName = string.Empty;
    private string _managedStylePrompt = string.Empty;
    private bool _isGeneratingStylePrompt;
    private int _maximumCacheEntries = 500;
    private TranslationTaskState _state = TranslationTaskState.Idle;
    private string _statusText = "请先在 PDF 页面中选择文字。";
    private string _progressText = string.Empty;
    private string _glossarySourceTerm = string.Empty;
    private string _glossaryTargetTerm = string.Empty;
    private string? _errorMessage;
    private Guid? _activeRequestId;
    private CancellationTokenSource? _translationCancellationSource;
    private bool _hasAttemptedTranslation;
    private TranslationSourceKind _sourceKind = TranslationSourceKind.Selection;
    private string? _sourceScope;
    private TranslationHistoryItemViewModel? _selectedHistoryEntry;
    private TranslationGlossaryItemViewModel? _selectedGlossaryEntry;

    public TranslationPanelViewModel(
        ITranslationService translationService,
        ISettingsService settingsService,
        IClipboardService clipboardService,
        IUserErrorService userErrorService,
        ITranslationMemoryRepository? translationMemoryRepository = null,
        IWordTranslationService? wordTranslationService = null)
    {
        _translationService = translationService;
        _settingsService = settingsService;
        _clipboardService = clipboardService;
        _userErrorService = userErrorService;
        _translationMemoryRepository = translationMemoryRepository;
        _wordTranslationService = wordTranslationService;

        TranslateCommand = new AsyncCommand(TranslateAsync, () => CanTranslate);
        CancelTranslationCommand = new AsyncCommand(CancelAsync, () => CanCancel);
        RetryTranslationCommand = new AsyncCommand(TranslateAsync, () => CanRetry);
        CopySourceCommand = new AsyncCommand(CopySourceAsync, () => CanCopySource);
        CopyTranslationCommand = new AsyncCommand(CopyTranslationAsync, () => CanCopyTranslation);
        LoadSelectedHistoryCommand = new AsyncCommand(LoadSelectedHistoryAsync, () => SelectedHistoryEntry is not null);
        MarkHistoryPreferredCommand = new AsyncCommand(
            () => MarkSelectedHistorySampleAsync(TranslationSampleKind.Preferred),
            () => SelectedHistoryEntry is not null);
        MarkHistoryRejectedCommand = new AsyncCommand(
            () => MarkSelectedHistorySampleAsync(TranslationSampleKind.Rejected),
            () => SelectedHistoryEntry is not null);
        ClearHistorySampleCommand = new AsyncCommand(
            () => MarkSelectedHistorySampleAsync(TranslationSampleKind.None),
            () => SelectedHistoryEntry is not null);
        DeleteHistoryEntryCommand = new AsyncCommand(DeleteSelectedHistoryEntryAsync, () => SelectedHistoryEntry is not null);
        AddGlossaryEntryCommand = new AsyncCommand(AddGlossaryEntryAsync, () => CanAddGlossaryEntry);
        DeleteGlossaryEntryCommand = new AsyncCommand(DeleteSelectedGlossaryEntryAsync, () => SelectedGlossaryEntry is not null);
        GenerateStylePromptCommand = new AsyncCommand(GenerateStylePromptAsync, () => CanGenerateStylePrompt);
        AddTranslationStyleCommand = new AsyncCommand(AddTranslationStyleAsync, () => CanAddTranslationStyle);
        SaveTranslationStyleCommand = new AsyncCommand(SaveTranslationStyleAsync, () => CanSaveTranslationStyle);
        DeleteTranslationStyleCommand = new AsyncCommand(DeleteTranslationStyleAsync, () => CanDeleteTranslationStyle);
        RestoreTranslationStyleCommand = new AsyncCommand(RestoreTranslationStyleAsync, () => CanRestoreTranslationStyle);
        _commands =
        [
            (AsyncCommand)TranslateCommand,
            (AsyncCommand)CancelTranslationCommand,
            (AsyncCommand)RetryTranslationCommand,
            (AsyncCommand)CopySourceCommand,
            (AsyncCommand)CopyTranslationCommand,
            (AsyncCommand)LoadSelectedHistoryCommand,
            (AsyncCommand)MarkHistoryPreferredCommand,
            (AsyncCommand)MarkHistoryRejectedCommand,
            (AsyncCommand)ClearHistorySampleCommand,
            (AsyncCommand)DeleteHistoryEntryCommand,
            (AsyncCommand)AddGlossaryEntryCommand,
            (AsyncCommand)DeleteGlossaryEntryCommand,
            (AsyncCommand)GenerateStylePromptCommand,
            (AsyncCommand)AddTranslationStyleCommand,
            (AsyncCommand)SaveTranslationStyleCommand,
            (AsyncCommand)DeleteTranslationStyleCommand,
            (AsyncCommand)RestoreTranslationStyleCommand
        ];
        ReloadDefaultTranslationStyles();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand TranslateCommand { get; }

    public ICommand CancelTranslationCommand { get; }

    public ICommand RetryTranslationCommand { get; }

    public ICommand CopySourceCommand { get; }

    public ICommand CopyTranslationCommand { get; }

    public ICommand LoadSelectedHistoryCommand { get; }

    public ICommand MarkHistoryPreferredCommand { get; }

    public ICommand MarkHistoryRejectedCommand { get; }

    public ICommand ClearHistorySampleCommand { get; }

    public ICommand DeleteHistoryEntryCommand { get; }

    public ICommand AddGlossaryEntryCommand { get; }

    public ICommand DeleteGlossaryEntryCommand { get; }

    public ICommand GenerateStylePromptCommand { get; }

    public ICommand AddTranslationStyleCommand { get; }

    public ICommand SaveTranslationStyleCommand { get; }

    public ICommand DeleteTranslationStyleCommand { get; }

    public ICommand RestoreTranslationStyleCommand { get; }

    public ObservableCollection<TranslationHistoryItemViewModel> HistoryEntries { get; } = [];

    public ObservableCollection<TranslationGlossaryItemViewModel> GlossaryEntries { get; } = [];

    public ObservableCollection<TranslationComparisonSegmentViewModel> ComparisonSegments { get; } = [];

    public ObservableCollection<TranslationStyleItemViewModel> StyleOptions { get; } = [];

    public ObservableCollection<TranslationStyleItemViewModel> ManagedStyleOptions { get; } = [];

    public IReadOnlyList<TranslationPresetOption> PresetOptions { get; } =
    [
        new(TranslationPreset.Literal, "直译"),
        new(TranslationPreset.Fluent, "流畅"),
        new(TranslationPreset.Academic, "学术论文"),
        new(TranslationPreset.ComputerScience, "计算机科学"),
        new(TranslationPreset.Medical, "医学"),
        new(TranslationPreset.Custom, "自定义")
    ];

    public IReadOnlyList<TargetLanguageOption> TargetLanguageOptions { get; } =
    [
        new("zh-CN", "简体中文"),
        new("zh-TW", "繁体中文"),
        new("en", "英语"),
        new("ja", "日语"),
        new("ko", "韩语"),
        new("de", "德语"),
        new("fr", "法语")
    ];

    public string SourceText
    {
        get => _sourceText;
        set => UpdateSourceText(value ?? string.Empty);
    }

    public string SourceCharacterCountText => SourceText.Length == 0
        ? "尚未选择原文"
        : $"将发送 {SourceText.Length} 个字符，约 {CalculateSegmentCount(SourceText)} 段（点击翻译后才会联网）";

    public string EstimatedUsageText => SourceText.Length == 0
        ? "当前无待翻译内容。"
        : $"预计请求 {CalculateSegmentCount(SourceText)} 次，缓存命中时不会联网；不会自动重试。";

    public string TranslatedText
    {
        get => _translatedText;
        set
        {
            if (SetProperty(ref _translatedText, value))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public TranslationPreset SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value))
            {
                SelectStyleForPreset(value);
                OnPropertyChanged(nameof(IsCustomPreset));
                NotifyCommandStateChanged();
            }
        }
    }

    public TranslationStyleItemViewModel? SelectedStyleOption
    {
        get => _selectedStyleOption;
        set
        {
            if (SetProperty(ref _selectedStyleOption, value))
            {
                ApplySelectedStyleOption(value);
                NotifyCommandStateChanged();
            }
        }
    }

    public TranslationStyleItemViewModel? SelectedManagedStyle
    {
        get => _selectedManagedStyle;
        set
        {
            if (SetProperty(ref _selectedManagedStyle, value))
            {
                ManagedStyleName = value?.Name ?? string.Empty;
                ManagedStylePrompt = value?.Prompt ?? string.Empty;
                OnPropertyChanged(nameof(SelectedManagedStyleKindText));
                OnPropertyChanged(nameof(CanRestoreTranslationStyle));
                NotifyCommandStateChanged();
            }
        }
    }

    public string TargetLanguage
    {
        get => _targetLanguage;
        set
        {
            if (SetProperty(ref _targetLanguage, value))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public string CustomInstruction
    {
        get => _customInstruction;
        set
        {
            if (SetProperty(ref _customInstruction, value))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public string CustomStyleName
    {
        get => _customStyleName;
        set
        {
            if (SetProperty(ref _customStyleName, value ?? string.Empty) &&
                SelectedStyleOption?.Preset == TranslationPreset.Custom)
            {
                OnPropertyChanged(nameof(SelectedStyleOption));
            }
        }
    }

    public string CustomStylePrompt
    {
        get => _customStylePrompt;
        set
        {
            if (SetProperty(ref _customStylePrompt, value ?? string.Empty))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public string ManagedStyleName
    {
        get => _managedStyleName;
        set
        {
            if (SetProperty(ref _managedStyleName, value ?? string.Empty))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public string ManagedStylePrompt
    {
        get => _managedStylePrompt;
        set
        {
            if (SetProperty(ref _managedStylePrompt, value ?? string.Empty))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public string StylePromptRequirement
    {
        get => _stylePromptRequirement;
        set
        {
            if (SetProperty(ref _stylePromptRequirement, value ?? string.Empty))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public string StylePromptStatusText
    {
        get => _stylePromptStatusText;
        private set => SetProperty(ref _stylePromptStatusText, value);
    }

    public bool IsCustomPreset => SelectedPreset == TranslationPreset.Custom;

    public string SelectedManagedStyleKindText => SelectedManagedStyle switch
    {
        null => "选择一个风格后可编辑名称和提示词。",
        { IsBuiltIn: true, IsDeleted: true } => "内置预设已隐藏，可恢复为默认值。",
        { IsBuiltIn: true } => "内置预设：可改名、修改提示词或隐藏；恢复会回到默认提示词。",
        _ => "自定义风格：保存后会出现在翻译风格下拉框。"
    };

    public TranslationSourceKind SourceKind
    {
        get => _sourceKind;
        private set => SetProperty(ref _sourceKind, value);
    }

    public string SourceScopeText => SourceKind switch
    {
        TranslationSourceKind.CurrentPage => _sourceScope ?? "当前页",
        TranslationSourceKind.PageRange => _sourceScope ?? "页码范围",
        _ => "选中文字"
    };

    public TranslationTaskState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsTranslating));
                NotifyCommandStateChanged();
            }
        }
    }

    public bool IsTranslating => State == TranslationTaskState.Translating;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string GlossarySourceTerm
    {
        get => _glossarySourceTerm;
        set
        {
            if (SetProperty(ref _glossarySourceTerm, value ?? string.Empty))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public string GlossaryTargetTerm
    {
        get => _glossaryTargetTerm;
        set
        {
            if (SetProperty(ref _glossaryTargetTerm, value ?? string.Empty))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public TranslationHistoryItemViewModel? SelectedHistoryEntry
    {
        get => _selectedHistoryEntry;
        set
        {
            if (SetProperty(ref _selectedHistoryEntry, value))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public TranslationGlossaryItemViewModel? SelectedGlossaryEntry
    {
        get => _selectedGlossaryEntry;
        set
        {
            if (SetProperty(ref _selectedGlossaryEntry, value))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public bool CanTranslate =>
        !IsTranslating
        && !string.IsNullOrWhiteSpace(SourceText)
        && !string.IsNullOrWhiteSpace(TargetLanguage)
        && (!IsCustomPreset || !string.IsNullOrWhiteSpace(CustomStylePrompt));

    public bool CanCancel => IsTranslating;

    public bool CanRetry => CanTranslate && _hasAttemptedTranslation;

    public bool CanCopySource => !string.IsNullOrWhiteSpace(SourceText);

    public bool CanCopyTranslation => !string.IsNullOrWhiteSpace(TranslatedText);

    public bool CanAddGlossaryEntry =>
        !string.IsNullOrWhiteSpace(GlossarySourceTerm)
        && !string.IsNullOrWhiteSpace(GlossaryTargetTerm);

    public bool CanGenerateStylePrompt =>
        !IsTranslating && !_isGeneratingStylePrompt && !string.IsNullOrWhiteSpace(StylePromptRequirement);

    public bool CanAddTranslationStyle =>
        !string.IsNullOrWhiteSpace(ManagedStyleName)
        && !string.IsNullOrWhiteSpace(ManagedStylePrompt);

    public bool CanSaveTranslationStyle =>
        SelectedManagedStyle is not null
        && !string.IsNullOrWhiteSpace(ManagedStyleName)
        && !string.IsNullOrWhiteSpace(ManagedStylePrompt);

    public bool CanDeleteTranslationStyle => SelectedManagedStyle is not null;

    public bool CanRestoreTranslationStyle => SelectedManagedStyle?.IsBuiltIn == true;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settingsService.LoadAsync(cancellationToken);
            TargetLanguage = settings.Translation.TargetLanguage;
            _maximumCacheEntries = Math.Clamp(settings.Translation.MaximumCacheEntries, 0, 10000);
            var savedPreset = Enum.TryParse<TranslationPreset>(
                settings.Translation.DefaultPreset,
                ignoreCase: true,
                out var preset)
                ? preset
                : TranslationPreset.Academic;
            LoadTranslationStyles(settings, savedPreset);
            await RefreshTranslationMemoryAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = _userErrorService.Report(
                UserErrorCode.TranslationSettingsReadFailed,
                exception).InlineText;
            StatusText = "翻译设置加载失败。";
        }
    }

    public void SetSourceText(string sourceText)
    {
        SetSourceText(sourceText, TranslationSourceKind.Selection, null);
    }

    public void SetSourceText(
        string sourceText,
        TranslationSourceKind sourceKind,
        string? sourceScope)
    {
        SourceKind = sourceKind;
        _sourceScope = sourceScope;
        OnPropertyChanged(nameof(SourceScopeText));
        UpdateSourceText(sourceText ?? string.Empty);
    }

    public TranslationPanelSnapshot CaptureSnapshot() => new(
        SourceText,
        TranslatedText,
        SelectedPreset,
        TargetLanguage,
        CustomInstruction,
        CustomStyleName,
        CustomStylePrompt,
        State,
        StatusText,
        ErrorMessage,
        _hasAttemptedTranslation);

    public TranslationPanelSnapshot CreateEmptySnapshot() => new(
        string.Empty,
        string.Empty,
        SelectedPreset,
        TargetLanguage,
        CustomInstruction,
        CustomStyleName,
        CustomStylePrompt,
        TranslationTaskState.Idle,
        "请先在 PDF 页面中选择文字。",
        null,
        false);

    public void RestoreSnapshot(TranslationPanelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        StopCurrentRequestForSourceChange();
        SetProperty(ref _sourceText, snapshot.SourceText, nameof(SourceText));
        OnPropertyChanged(nameof(SourceCharacterCountText));
        OnPropertyChanged(nameof(EstimatedUsageText));
        TranslatedText = snapshot.TranslatedText;
        RefreshComparisonSegments(snapshot.SourceText, snapshot.TranslatedText);
        SelectedPreset = snapshot.SelectedPreset;
        TargetLanguage = snapshot.TargetLanguage;
        CustomInstruction = snapshot.CustomInstruction;
        CustomStyleName = snapshot.CustomStyleName;
        CustomStylePrompt = snapshot.CustomStylePrompt;
        _hasAttemptedTranslation = snapshot.HasAttemptedTranslation;
        State = snapshot.State == TranslationTaskState.Translating
            ? TranslationTaskState.Cancelled
            : snapshot.State;
        StatusText = snapshot.StatusText;
        ErrorMessage = snapshot.ErrorMessage;
        NotifyCommandStateChanged();
    }

    private void ReloadDefaultTranslationStyles() =>
        LoadTranslationStyles(
            new AppSettings
            {
                TranslationStyles = new TranslationStyleSettings
                {
                    Items = TranslationStyleDefinition.CreateDefaults()
                }
            },
            TranslationPreset.Academic);

    private void LoadTranslationStyles(AppSettings settings, TranslationPreset fallbackPreset)
    {
        var defaultDefinitions = TranslationStyleDefinition.CreateDefaults();
        var existing = settings.TranslationStyles?.Items ?? defaultDefinitions;
        var merged = MergeTranslationStyleDefinitions(existing, defaultDefinitions);
        _translationStyleDefinitions.Clear();
        _translationStyleDefinitions.AddRange(merged);
        var preferredStyleId = settings.TranslationStyles?.DefaultStyleId;
        if (fallbackPreset != TranslationPreset.Academic &&
            string.Equals(preferredStyleId, "preset-academic", StringComparison.OrdinalIgnoreCase))
        {
            preferredStyleId = defaultDefinitions.FirstOrDefault(style =>
                string.Equals(style.Preset, fallbackPreset.ToString(), StringComparison.OrdinalIgnoreCase))?.Id;
        }

        RefreshTranslationStyleCollections(preferredStyleId, fallbackPreset);
    }

    private static IReadOnlyList<TranslationStyleDefinition> MergeTranslationStyleDefinitions(
        IEnumerable<TranslationStyleDefinition>? existing,
        IReadOnlyList<TranslationStyleDefinition> defaults)
    {
        var merged = new List<TranslationStyleDefinition>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in existing ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.Name) ||
                string.IsNullOrWhiteSpace(item.Preset) ||
                string.IsNullOrWhiteSpace(item.Prompt) ||
                !Enum.TryParse<TranslationPreset>(item.Preset, ignoreCase: true, out _))
            {
                continue;
            }

            if (ids.Add(item.Id))
            {
                merged.Add(item);
            }
        }

        foreach (var defaultItem in defaults)
        {
            if (!ids.Contains(defaultItem.Id))
            {
                merged.Add(defaultItem);
            }
        }

        return merged;
    }

    private static TranslationPreset ParseStylePreset(string preset) =>
        Enum.TryParse<TranslationPreset>(preset, ignoreCase: true, out var parsed)
            ? parsed
            : TranslationPreset.Custom;

    private void RefreshTranslationStyleCollections(
        string? preferredStyleId = null,
        TranslationPreset? fallbackPreset = null)
    {
        var previouslySelectedId = preferredStyleId ?? SelectedStyleOption?.Id;
        StyleOptions.Clear();
        ManagedStyleOptions.Clear();

        foreach (var definition in _translationStyleDefinitions)
        {
            var item = new TranslationStyleItemViewModel(definition);
            ManagedStyleOptions.Add(item);
            if (!definition.IsDeleted)
            {
                StyleOptions.Add(item);
            }
        }

        var selected = StyleOptions.FirstOrDefault(item =>
                string.Equals(item.Id, previouslySelectedId, StringComparison.OrdinalIgnoreCase))
            ?? (fallbackPreset is { } preset
                ? StyleOptions.FirstOrDefault(item => item.Preset == preset)
                : null)
            ?? StyleOptions.FirstOrDefault(item => item.Preset == TranslationPreset.Academic)
            ?? StyleOptions.FirstOrDefault();

        SetSelectedStyleOption(selected);
        SelectedManagedStyle = ManagedStyleOptions.FirstOrDefault(item =>
                string.Equals(item.Id, selected?.Id, StringComparison.OrdinalIgnoreCase))
            ?? ManagedStyleOptions.FirstOrDefault();
    }

    private void SetSelectedStyleOption(TranslationStyleItemViewModel? item)
    {
        if (Equals(_selectedStyleOption, item))
        {
            ApplySelectedStyleOption(item);
            return;
        }

        _selectedStyleOption = item;
        OnPropertyChanged(nameof(SelectedStyleOption));
        ApplySelectedStyleOption(item);
    }

    private void ApplySelectedStyleOption(TranslationStyleItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (_selectedPreset != item.Preset)
        {
            _selectedPreset = item.Preset;
            OnPropertyChanged(nameof(SelectedPreset));
            OnPropertyChanged(nameof(IsCustomPreset));
        }

        CustomStyleName = item.Name;
        CustomStylePrompt = item.Prompt;
    }

    private void SelectStyleForPreset(TranslationPreset preset)
    {
        var selected = StyleOptions.FirstOrDefault(item =>
                item.Preset == preset &&
                (preset != TranslationPreset.Custom || string.Equals(item.Id, SelectedStyleOption?.Id, StringComparison.OrdinalIgnoreCase)))
            ?? StyleOptions.FirstOrDefault(item => item.Preset == preset);
        if (selected is not null && !Equals(selected, SelectedStyleOption))
        {
            SetSelectedStyleOption(selected);
        }
    }

    private void UpdateSourceText(string sourceText)
    {
        sourceText = NormalizeTranslationSourceText(sourceText);
        if (string.Equals(SourceText, sourceText, StringComparison.Ordinal))
        {
            return;
        }

        StopCurrentRequestForSourceChange();
        SetProperty(ref _sourceText, sourceText, nameof(SourceText));
        OnPropertyChanged(nameof(SourceCharacterCountText));
        OnPropertyChanged(nameof(EstimatedUsageText));
        TranslatedText = string.Empty;
        ComparisonSegments.Clear();
        ErrorMessage = null;
        ProgressText = string.Empty;
        _hasAttemptedTranslation = false;
        State = sourceText.Length == 0 ? TranslationTaskState.Idle : TranslationTaskState.Ready;
        StatusText = sourceText.Length == 0
            ? "请先在 PDF 页面中选择文字。"
            : "原文可在发送前编辑；只有点击“开始翻译”才会调用 API。";
        NotifyCommandStateChanged();
    }

    public async Task TranslateAsync()
    {
        if (!CanTranslate)
        {
            return;
        }

        var requestId = Guid.NewGuid();
        var sourceSnapshot = NormalizeTranslationSourceText(SourceText);
        var taskInstruction = string.IsNullOrWhiteSpace(CustomInstruction)
            ? null
            : CustomInstruction.Trim();
        var styleInstruction = CreateStyleInstructionForRequest();
        var cacheInstruction = CombineInstructions(
            styleInstruction is null ? null : $"风格：{styleInstruction}",
            taskInstruction is null ? null : $"本次要求：{taskInstruction}");
        var glossaryInstruction = CreateGlossaryInstruction();
        var preferenceInstruction = CreateSamplePreferenceInstruction();
        var memoryInstruction = CombineInstructions(
            cacheInstruction,
            preferenceInstruction is null ? null : $"偏好样本：{preferenceInstruction}");
        var cacheKey = CreateCacheKey(
            NormalizeForCacheIdentity(sourceSnapshot),
            TargetLanguage,
            SelectedPreset,
            cacheInstruction,
            glossaryInstruction,
            preferenceInstruction);
        if (await TryUseCachedTranslationAsync(
                cacheKey,
                sourceSnapshot,
                memoryInstruction,
                glossaryInstruction,
                CancellationToken.None))
        {
            return;
        }

        if (await TryUseWordTranslationAsync(
                cacheKey,
                sourceSnapshot,
                memoryInstruction,
                glossaryInstruction,
                preferenceInstruction,
                CancellationToken.None))
        {
            return;
        }

        var segments = SplitSourceText(sourceSnapshot).ToArray();
        using var cancellationSource = new CancellationTokenSource();
        _translationCancellationSource = cancellationSource;
        _activeRequestId = requestId;
        _hasAttemptedTranslation = true;
        TranslatedText = string.Empty;
        ComparisonSegments.Clear();
        ErrorMessage = null;
        State = TranslationTaskState.Translating;
        ProgressText = $"准备发送 {segments.Length} 段。";
        StatusText = "正在翻译；此请求已经开始使用 API。";
        var completedSegments = 0;
        var previousSegmentContext = string.Empty;
        var alignmentSegments = new List<TranslationAlignmentSegment>();

        try
        {
            for (var segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
            {
                var segment = segments[segmentIndex];
                var segmentPreferenceInstruction = CombineInstructions(
                    preferenceInstruction,
                    CreateSegmentContextInstruction(previousSegmentContext));
                ProgressText = $"正在翻译第 {segmentIndex + 1}/{segments.Length} 段，已完成 {completedSegments} 段。";
                if (TranslatedText.Length > 0)
                {
                    TranslatedText += Environment.NewLine + Environment.NewLine;
                }
                var rawResponse = new StringBuilder();

                var request = new TranslationRequest(
                    requestId,
                    segment,
                    TargetLanguage,
                    SelectedPreset,
                    SourceLanguage: null,
                    CustomInstruction: taskInstruction,
                    StyleInstruction: styleInstruction,
                    GlossaryInstruction: glossaryInstruction,
                    PreferenceInstruction: segmentPreferenceInstruction,
                    SourceScope: SourceScopeText);

                await foreach (var chunk in _translationService.TranslateAsync(request, cancellationSource.Token))
                {
                    if (!IsCurrentRequest(requestId) || chunk.RequestId != requestId)
                    {
                        continue;
                    }

                    rawResponse.Append(chunk.Text);
                    if (!chunk.IsCompleted)
                    {
                        ProgressText = $"正在翻译第 {segmentIndex + 1}/{segments.Length} 段，正在等待语义对照结果。";
                    }
                }

                var parsed = ParseAlignmentResponse(rawResponse.ToString(), segment);
                alignmentSegments.AddRange(parsed);
                TranslatedText += CreateTranslatedText(parsed);
                completedSegments++;
                previousSegmentContext = CreateSegmentContext(TranslatedText);
            }

            if (IsCurrentRequest(requestId))
            {
                State = TranslationTaskState.Completed;
                ProgressText = $"已完成 {completedSegments}/{segments.Length} 段。";
                StatusText = "翻译完成。你可以编辑、复制或从历史中再次载入。";
                RefreshComparisonSegments(alignmentSegments);
                await SaveTranslationMemoryAsync(
                    cacheKey,
                    sourceSnapshot,
                    TranslatedText,
                    memoryInstruction,
                    glossaryInstruction,
                    segments.Length,
                    alignmentSegments,
                    usedCache: false,
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            if (IsCurrentRequest(requestId))
            {
                State = TranslationTaskState.Cancelled;
                ProgressText = $"已停止，完成 {completedSegments}/{segments.Length} 段。";
                StatusText = "翻译已停止；停止前已经生成的内容可能仍会计费。";
            }
        }
        catch (TranslationException exception)
        {
            if (IsCurrentRequest(requestId))
            {
                State = TranslationTaskState.Failed;
                ErrorMessage = _userErrorService.Report(GetErrorCode(exception.Error), exception).InlineText;
                ProgressText = $"失败前完成 {completedSegments}/{segments.Length} 段。";
                StatusText = "翻译失败，未自动重试。";
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentRequest(requestId))
            {
                State = TranslationTaskState.Failed;
                ErrorMessage = _userErrorService.Report(
                    UserErrorCode.TranslationUnexpectedFailure,
                    exception).InlineText;
                ProgressText = $"失败前完成 {completedSegments}/{segments.Length} 段。";
                StatusText = "翻译失败，未自动重试。";
            }
        }
        finally
        {
            if (IsCurrentRequest(requestId))
            {
                _activeRequestId = null;
                _translationCancellationSource = null;
                NotifyCommandStateChanged();
            }
        }
    }

    public async Task CancelAsync()
    {
        if (_activeRequestId is not { } requestId || _translationCancellationSource is not { } cancellationSource)
        {
            return;
        }

        StatusText = "正在停止翻译…";
        cancellationSource.Cancel();
        await _translationService.CancelAsync(requestId, CancellationToken.None);
    }

    public async Task CopyTranslationAsync()
    {
        if (!CanCopyTranslation)
        {
            return;
        }

        try
        {
            await _clipboardService.SetTextAsync(TranslatedText, CancellationToken.None);
            StatusText = "译文已复制到剪贴板。";
            ErrorMessage = null;
        }
        catch (Exception exception)
        {
            ErrorMessage = _userErrorService.Report(UserErrorCode.ClipboardWriteFailed, exception).InlineText;
        }
    }

    private async Task CopySourceAsync()
    {
        if (!CanCopySource)
        {
            return;
        }

        try
        {
            await _clipboardService.SetTextAsync(SourceText, CancellationToken.None);
            StatusText = "原文已复制到剪贴板。";
            ErrorMessage = null;
        }
        catch (Exception exception)
        {
            ErrorMessage = _userErrorService.Report(UserErrorCode.ClipboardWriteFailed, exception).InlineText;
        }
    }

    private async Task GenerateStylePromptAsync()
    {
        if (!CanGenerateStylePrompt)
        {
            return;
        }

        var requestId = Guid.NewGuid();
        var builder = new StringBuilder();
        _isGeneratingStylePrompt = true;
        StylePromptStatusText = "正在调用 DeepSeek 起草风格提示词，可能产生 API 费用。";
        NotifyCommandStateChanged();
        try
        {
            var request = new TranslationRequest(
                requestId,
                StylePromptRequirement.Trim(),
                "zh-CN",
                TranslationPreset.Fluent,
                Purpose: TranslationRequestPurpose.StylePromptDraft);
            await foreach (var chunk in _translationService.TranslateAsync(request, CancellationToken.None))
            {
                builder.Append(chunk.Text);
            }

            var prompt = builder.ToString().Trim();
            if (prompt.Length > 0)
            {
                ManagedStylePrompt = prompt;
                StylePromptStatusText = "已生成提示词。请阅读并按实际翻译效果继续调整。";
            }
            else
            {
                StylePromptStatusText = "DeepSeek 未返回可用提示词，请换一种要求再试。";
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = _userErrorService.Report(
                UserErrorCode.TranslationUnexpectedFailure,
                exception).InlineText;
            StylePromptStatusText = "生成失败，请检查 API 设置或稍后重试。";
        }
        finally
        {
            _isGeneratingStylePrompt = false;
            NotifyCommandStateChanged();
        }
    }

    private async Task AddTranslationStyleAsync()
    {
        if (!CanAddTranslationStyle)
        {
            return;
        }

        var definition = new TranslationStyleDefinition(
            $"custom-{Guid.NewGuid():N}",
            ManagedStyleName.Trim(),
            TranslationPreset.Custom.ToString(),
            ManagedStylePrompt.Trim(),
            IsBuiltIn: false,
            IsDeleted: false);
        _translationStyleDefinitions.Add(definition);
        await PersistTranslationStylesAsync(definition.Id, CancellationToken.None);
        RefreshTranslationStyleCollections(definition.Id, TranslationPreset.Custom);
        StylePromptStatusText = "已新增自定义翻译风格，可在翻译风格中选择。";
    }

    private async Task SaveTranslationStyleAsync()
    {
        if (!CanSaveTranslationStyle || SelectedManagedStyle is null)
        {
            return;
        }

        var index = _translationStyleDefinitions.FindIndex(style =>
            string.Equals(style.Id, SelectedManagedStyle.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        var updated = _translationStyleDefinitions[index] with
        {
            Name = ManagedStyleName.Trim(),
            Prompt = ManagedStylePrompt.Trim(),
            IsDeleted = false
        };
        _translationStyleDefinitions[index] = updated;
        await PersistTranslationStylesAsync(updated.Id, CancellationToken.None);
        RefreshTranslationStyleCollections(updated.Id, ParseStylePreset(updated.Preset));
        StylePromptStatusText = "已保存翻译风格。提示词效果需要结合实际翻译继续微调。";
    }

    private async Task DeleteTranslationStyleAsync()
    {
        if (!CanDeleteTranslationStyle || SelectedManagedStyle is null)
        {
            return;
        }

        var index = _translationStyleDefinitions.FindIndex(style =>
            string.Equals(style.Id, SelectedManagedStyle.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        var selected = _translationStyleDefinitions[index];
        if (selected.IsBuiltIn)
        {
            _translationStyleDefinitions[index] = selected with { IsDeleted = true };
            StylePromptStatusText = "已隐藏内置预设，可通过恢复预设重新启用。";
        }
        else
        {
            _translationStyleDefinitions.RemoveAt(index);
            StylePromptStatusText = "已删除自定义翻译风格。";
        }

        await PersistTranslationStylesAsync(SelectedStyleOption?.Id, CancellationToken.None);
        RefreshTranslationStyleCollections(fallbackPreset: TranslationPreset.Academic);
    }

    private async Task RestoreTranslationStyleAsync()
    {
        if (!CanRestoreTranslationStyle || SelectedManagedStyle is null)
        {
            return;
        }

        var defaultDefinition = TranslationStyleDefinition.CreateDefaults()
            .FirstOrDefault(style => string.Equals(
                style.Id,
                SelectedManagedStyle.Id,
                StringComparison.OrdinalIgnoreCase));
        if (defaultDefinition is null)
        {
            return;
        }

        var index = _translationStyleDefinitions.FindIndex(style =>
            string.Equals(style.Id, SelectedManagedStyle.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            _translationStyleDefinitions.Add(defaultDefinition);
        }
        else
        {
            _translationStyleDefinitions[index] = defaultDefinition;
        }

        await PersistTranslationStylesAsync(defaultDefinition.Id, CancellationToken.None);
        RefreshTranslationStyleCollections(defaultDefinition.Id, ParseStylePreset(defaultDefinition.Preset));
        StylePromptStatusText = "已恢复内置预设的默认名称和提示词。";
    }

    private async Task PersistTranslationStylesAsync(string? selectedStyleId, CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settingsService.LoadAsync(cancellationToken);
            var styleId = selectedStyleId
                ?? SelectedStyleOption?.Id
                ?? settings.TranslationStyles?.DefaultStyleId
                ?? "preset-academic";
            await _settingsService.SaveAsync(
                settings with
                {
                    TranslationStyles = new TranslationStyleSettings
                    {
                        DefaultStyleId = styleId,
                        Items = _translationStyleDefinitions.ToArray()
                    }
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            ErrorMessage = _userErrorService.Report(
                UserErrorCode.TranslationUnexpectedFailure,
                exception).InlineText;
            StylePromptStatusText = "保存翻译风格失败，请稍后重试。";
        }
    }

    private async Task LoadSelectedHistoryAsync()
    {
        if (SelectedHistoryEntry is not { } selected)
        {
            return;
        }

        SetSourceText(selected.Entry.SourceText, selected.Entry.SourceKind, selected.Entry.SourceScope);
        TranslatedText = selected.Entry.TranslatedText;
        RefreshComparisonSegments(selected.Entry.SourceText, selected.Entry.TranslatedText);
        State = TranslationTaskState.Completed;
        ErrorMessage = null;
        ProgressText = $"已载入历史：{selected.ScopeText}。";
        StatusText = "历史译文已载入，可继续编辑或复制。";
        await Task.CompletedTask;
    }

    private async Task MarkSelectedHistorySampleAsync(TranslationSampleKind sampleKind)
    {
        if (SelectedHistoryEntry is not { } selected || !Enum.IsDefined(sampleKind))
        {
            return;
        }

        try
        {
            if (_translationMemoryRepository is not null)
            {
                await _translationMemoryRepository.UpdateHistorySampleKindAsync(
                    selected.Entry.HistoryId,
                    sampleKind,
                    CancellationToken.None);
            }

            await RefreshHistoryOnlyAsync(CancellationToken.None);
            SelectedHistoryEntry = HistoryEntries.FirstOrDefault(item =>
                item.Entry.HistoryId == selected.Entry.HistoryId);
            StatusText = sampleKind switch
            {
                TranslationSampleKind.Preferred => "已标记为参考样本，后续翻译会参考这种表达。",
                TranslationSampleKind.Rejected => "已标记为避开样本，后续翻译会减少类似表达。",
                _ => "已取消样本标记。"
            };
        }
        catch (Exception exception)
        {
            ErrorMessage = _userErrorService.Report(
                UserErrorCode.TranslationUnexpectedFailure,
                exception).InlineText;
        }
    }

    private async Task AddGlossaryEntryAsync()
    {
        if (!CanAddGlossaryEntry)
        {
            return;
        }

        try
        {
            var entry = _translationMemoryRepository is not null
                ? await _translationMemoryRepository.UpsertGlossaryEntryAsync(
                    GlossarySourceTerm,
                    GlossaryTargetTerm,
                    CancellationToken.None)
                : new TranslationGlossaryEntry(
                    Guid.NewGuid(),
                    GlossarySourceTerm.Trim(),
                    GlossaryTargetTerm.Trim(),
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow);
            UpsertGlossaryItem(entry);
            GlossarySourceTerm = string.Empty;
            GlossaryTargetTerm = string.Empty;
            StatusText = "术语已保存；后续翻译会优先遵循术语表。";
        }
        catch (Exception exception)
        {
            ErrorMessage = _userErrorService.Report(
                UserErrorCode.TranslationUnexpectedFailure,
                exception).InlineText;
        }
    }

    private async Task DeleteSelectedGlossaryEntryAsync()
    {
        if (SelectedGlossaryEntry is not { } selected)
        {
            return;
        }

        try
        {
            if (_translationMemoryRepository is not null)
            {
                await _translationMemoryRepository.DeleteGlossaryEntryAsync(
                    selected.Entry.EntryId,
                    CancellationToken.None);
            }

            GlossaryEntries.Remove(selected);
            SelectedGlossaryEntry = null;
            StatusText = "术语已删除。";
        }
        catch (Exception exception)
        {
            ErrorMessage = _userErrorService.Report(
                UserErrorCode.TranslationUnexpectedFailure,
                exception).InlineText;
        }
    }

    private async Task DeleteSelectedHistoryEntryAsync()
    {
        if (SelectedHistoryEntry is not { } selected || _translationMemoryRepository is null)
        {
            return;
        }

        try
        {
            await _translationMemoryRepository.DeleteHistoryAndRelatedCacheAsync(
                selected.Entry,
                CancellationToken.None);
            await RefreshHistoryOnlyAsync(CancellationToken.None);
            SelectedHistoryEntry = HistoryEntries.FirstOrDefault();
            ProgressText = "已删除选中的翻译历史，并清理对应本地缓存。";
            StatusText = "错误译文不会再从这条本地记忆中复用。";
            NotifyCommandStateChanged();
        }
        catch (Exception exception)
        {
            ErrorMessage = _userErrorService.Report(
                UserErrorCode.TranslationUnexpectedFailure,
                exception).InlineText;
        }
    }

    public async Task RefreshTranslationMemoryAsync(CancellationToken cancellationToken)
    {
        if (_translationMemoryRepository is null)
        {
            return;
        }

        HistoryEntries.Clear();
        foreach (var entry in await _translationMemoryRepository.GetRecentHistoryAsync(
                     MaximumRecentHistoryItems,
                     cancellationToken))
        {
            HistoryEntries.Add(new TranslationHistoryItemViewModel(entry));
        }

        GlossaryEntries.Clear();
        foreach (var entry in await _translationMemoryRepository.GetGlossaryAsync(cancellationToken))
        {
            GlossaryEntries.Add(new TranslationGlossaryItemViewModel(entry));
        }
    }

    private async Task<bool> TryUseCachedTranslationAsync(
        string cacheKey,
        string sourceText,
        string? customInstruction,
        string? glossaryInstruction,
        CancellationToken cancellationToken)
    {
        if (_translationMemoryRepository is null)
        {
            return false;
        }

        try
        {
            var cached = await _translationMemoryRepository.FindCacheAsync(cacheKey, cancellationToken);
            if (cached is not null)
            {
                _hasAttemptedTranslation = true;
                TranslatedText = cached.TranslatedText;
                RefreshComparisonSegments(sourceText, cached.TranslatedText);
                ErrorMessage = null;
                State = TranslationTaskState.Completed;
                ProgressText = "已命中本地缓存，未调用 API。";
                StatusText = "已从本地缓存载入译文。";
                NotifyCommandStateChanged();
                return true;
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = _userErrorService.Report(
                UserErrorCode.TranslationUnexpectedFailure,
                exception).InlineText;
            return false;
        }

        if (await TryUseSemanticSegmentCacheAsync(
                sourceText,
                customInstruction,
                glossaryInstruction,
                cancellationToken))
        {
            return true;
        }

        return await TryUseReusableCacheAsync(
            sourceText,
            customInstruction,
            glossaryInstruction,
            cancellationToken);
    }

    private async Task<bool> TryUseSemanticSegmentCacheAsync(
        string sourceText,
        string? customInstruction,
        string? glossaryInstruction,
        CancellationToken cancellationToken)
    {
        try
        {
            var segments = await _translationMemoryRepository!.GetReusableCacheSegmentsAsync(
                TargetLanguage,
                SelectedPreset,
                customInstruction,
                glossaryInstruction,
                MaximumReusableCacheSegmentCandidates,
                cancellationToken);
            if (TryComposeSemanticCacheTranslation(sourceText, segments, out var reused, out var reusedSegments))
            {
                _hasAttemptedTranslation = true;
                TranslatedText = reused;
                RefreshComparisonSegments(reusedSegments);
                ErrorMessage = null;
                State = TranslationTaskState.Completed;
                ProgressText = "已命中语义片段缓存，未调用 API。";
                StatusText = "已从较短的本地语义对照片段中复用译文。";
                NotifyCommandStateChanged();
                return true;
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = _userErrorService.Report(
                UserErrorCode.TranslationUnexpectedFailure,
                exception).InlineText;
        }

        return false;
    }

    private async Task<bool> TryUseWordTranslationAsync(
        string cacheKey,
        string sourceText,
        string? customInstruction,
        string? glossaryInstruction,
        string? preferenceInstruction,
        CancellationToken cancellationToken)
    {
        var wordTranslationService = _wordTranslationService;
        if (wordTranslationService is null || !IsSingleWordQuery(sourceText))
        {
            return false;
        }

        var result = await wordTranslationService.TryTranslateAsync(
            sourceText,
            TargetLanguage,
            cancellationToken);
        if (result is null)
        {
            return false;
        }

        _hasAttemptedTranslation = true;
        TranslatedText = result.TranslatedText;
        RefreshComparisonSegments([new TranslationAlignmentSegment(result.SourceText, result.TranslatedText)]);
        ErrorMessage = null;
        State = TranslationTaskState.Completed;
        ProgressText = $"已使用{result.ProviderName}，未调用 DeepSeek。";
        StatusText = "已完成单词翻译。";
        await SaveTranslationMemoryAsync(
            cacheKey,
            sourceText,
            result.TranslatedText,
            customInstruction,
            glossaryInstruction,
            segmentCount: 1,
            [new TranslationAlignmentSegment(result.SourceText, result.TranslatedText)],
            usedCache: false,
            cancellationToken);
        NotifyCommandStateChanged();
        return true;
    }

    private static bool IsSingleWordQuery(string sourceText)
    {
        var text = NormalizeTranslationWhitespace(sourceText);
        return text.Length is >= 2 and <= 40 &&
               text.All(character => char.IsAsciiLetter(character) || character is '-' or '\'') &&
               text.Any(char.IsAsciiLetter);
    }

    private string? CreateStyleInstructionForRequest()
    {
        if (IsCustomPreset)
        {
            return string.IsNullOrWhiteSpace(CustomStylePrompt)
                ? null
                : CustomStylePrompt.Trim();
        }

        var selected = SelectedStyleOption;
        if (selected is null || string.IsNullOrWhiteSpace(selected.Prompt))
        {
            return null;
        }

        var defaultPrompt = TranslationStyleDefinition.CreateDefaults()
            .FirstOrDefault(item =>
                string.Equals(item.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
            ?.Prompt;
        return string.Equals(selected.Prompt.Trim(), defaultPrompt, StringComparison.Ordinal)
            ? null
            : selected.Prompt.Trim();
    }

    private async Task<bool> TryUseReusableCacheAsync(
        string sourceText,
        string? customInstruction,
        string? glossaryInstruction,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await _translationMemoryRepository!.GetReusableCacheCandidatesAsync(
                TargetLanguage,
                SelectedPreset,
                customInstruction,
                glossaryInstruction,
                MaximumReusableCacheCandidates,
                cancellationToken);
            foreach (var candidate in candidates)
            {
                if (TryExtractAlignedTranslation(sourceText, candidate.SourceText, candidate.TranslatedText, out var reused))
                {
                    _hasAttemptedTranslation = true;
                    TranslatedText = reused;
                    RefreshComparisonSegments(sourceText, reused);
                    ErrorMessage = null;
                    State = TranslationTaskState.Completed;
                    ProgressText = "已命中句级缓存，未调用 API。";
                    StatusText = "已从较长的本地缓存中复用对应句段。";
                    NotifyCommandStateChanged();
                    return true;
                }
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = _userErrorService.Report(
                UserErrorCode.TranslationUnexpectedFailure,
                exception).InlineText;
        }

        return false;
    }

    private async Task SaveTranslationMemoryAsync(
        string cacheKey,
        string sourceText,
        string translatedText,
        string? customInstruction,
        string? glossaryInstruction,
        int segmentCount,
        IReadOnlyList<TranslationAlignmentSegment> alignmentSegments,
        bool usedCache,
        CancellationToken cancellationToken)
    {
        if (_translationMemoryRepository is null || string.IsNullOrWhiteSpace(translatedText))
        {
            return;
        }

        try
        {
            if (_maximumCacheEntries > 0)
            {
                var now = DateTimeOffset.UtcNow;
                await _translationMemoryRepository.SaveCacheAsync(
                    new TranslationCacheEntry(
                        cacheKey,
                        NormalizeForCacheIdentity(sourceText),
                        translatedText,
                        TargetLanguage,
                        SelectedPreset,
                        customInstruction,
                        glossaryInstruction,
                        now,
                        now,
                        1),
                    cancellationToken);
                await SaveTranslationCacheSegmentsAsync(
                    cacheKey,
                    alignmentSegments,
                    customInstruction,
                    glossaryInstruction,
                    cancellationToken);
                await _translationMemoryRepository.PruneCacheAsync(_maximumCacheEntries, cancellationToken);
            }

            await AddHistoryOnlyAsync(sourceText, translatedText, segmentCount, usedCache, cancellationToken);
            await RefreshHistoryOnlyAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            ErrorMessage = _userErrorService.Report(
                UserErrorCode.TranslationUnexpectedFailure,
                exception).InlineText;
        }
    }

    private async Task AddHistoryOnlyAsync(
        string sourceText,
        string translatedText,
        int segmentCount,
        bool usedCache,
        CancellationToken cancellationToken)
    {
        if (_translationMemoryRepository is null)
        {
            return;
        }

        await _translationMemoryRepository.AddHistoryAsync(
            new TranslationHistoryEntry(
                Guid.NewGuid(),
                CreatePreview(sourceText),
                NormalizeForCacheIdentity(sourceText),
                translatedText,
                TargetLanguage,
                SelectedPreset,
                SourceKind,
                SourceScopeText,
                DateTimeOffset.UtcNow,
                sourceText.Length,
                Math.Max(1, segmentCount),
                usedCache,
                TranslationSampleKind.None),
            cancellationToken);
    }

    private async Task SaveTranslationCacheSegmentsAsync(
        string cacheKey,
        IReadOnlyList<TranslationAlignmentSegment> alignmentSegments,
        string? customInstruction,
        string? glossaryInstruction,
        CancellationToken cancellationToken)
    {
        if (_translationMemoryRepository is null || alignmentSegments.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var segments = alignmentSegments
            .Select((segment, index) => new TranslationCacheSegment(
                cacheKey,
                index,
                NormalizeForCacheIdentity(segment.SourceText),
                NormalizeForCacheReuse(segment.SourceText),
                segment.TranslatedText.Trim(),
                TargetLanguage,
                SelectedPreset,
                customInstruction,
                glossaryInstruction,
                now,
                now,
                1))
            .Where(segment =>
                segment.SourceText.Length > 0 &&
                segment.NormalizedSourceText.Length > 0 &&
                segment.TranslatedText.Length > 0)
            .ToArray();
        if (segments.Length > 0)
        {
            await _translationMemoryRepository.SaveCacheSegmentsAsync(segments, cancellationToken);
        }
    }

    private async Task RefreshHistoryOnlyAsync(CancellationToken cancellationToken)
    {
        if (_translationMemoryRepository is null)
        {
            return;
        }

        HistoryEntries.Clear();
        foreach (var entry in await _translationMemoryRepository.GetRecentHistoryAsync(
                     MaximumRecentHistoryItems,
                     cancellationToken))
        {
            HistoryEntries.Add(new TranslationHistoryItemViewModel(entry));
        }
    }

    private void StopCurrentRequestForSourceChange()
    {
        _translationCancellationSource?.Cancel();
        _translationCancellationSource = null;
        _activeRequestId = null;
    }

    private bool IsCurrentRequest(Guid requestId) => _activeRequestId == requestId;

    private void FlushPendingText(StringBuilder pendingText)
    {
        if (pendingText.Length == 0)
        {
            return;
        }

        TranslatedText += pendingText.ToString();
        pendingText.Clear();
    }

    private void RefreshComparisonSegments(string sourceText, string translatedText)
    {
        ComparisonSegments.Clear();
        if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(translatedText))
        {
            return;
        }

        var sourceSegments = SplitSourceText(sourceText).ToArray();
        var translatedSegments = translatedText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (sourceSegments.Length != translatedSegments.Length)
        {
            sourceSegments = SplitBySentenceBoundary(sourceText)
                .Select(segment => segment.Trim())
                .Where(segment => segment.Length > 0)
                .ToArray();
            translatedSegments = SplitBySentenceBoundary(translatedText)
                .Select(segment => segment.Trim())
                .Where(segment => segment.Length > 0)
                .ToArray();
        }

        var count = Math.Max(sourceSegments.Length, translatedSegments.Length);
        for (var index = 0; index < count; index++)
        {
            ComparisonSegments.Add(new TranslationComparisonSegmentViewModel(
                index + 1,
                index < sourceSegments.Length ? sourceSegments[index] : string.Empty,
                index < translatedSegments.Length ? translatedSegments[index] : string.Empty));
        }
    }

    private void RefreshComparisonSegments(IReadOnlyList<TranslationAlignmentSegment> alignmentSegments)
    {
        ComparisonSegments.Clear();
        var index = 1;
        foreach (var segment in alignmentSegments)
        {
            var source = NormalizeForComparisonDisplay(segment.SourceText);
            var translated = NormalizeForComparisonDisplay(segment.TranslatedText);
            if (source.Length == 0 && translated.Length == 0)
            {
                continue;
            }

            ComparisonSegments.Add(new TranslationComparisonSegmentViewModel(index++, source, translated));
        }
    }

    private static IReadOnlyList<TranslationAlignmentSegment> ParseAlignmentResponse(
        string rawResponse,
        string fallbackSource)
    {
        var compact = ExtractJsonPayload(rawResponse);
        if (!string.IsNullOrWhiteSpace(compact))
        {
            try
            {
                using var document = JsonDocument.Parse(compact);
                var root = document.RootElement;
                var segmentsElement = root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("segments", out var segments)
                    ? segments
                    : root;
                if (segmentsElement.ValueKind == JsonValueKind.Array)
                {
                    var parsed = new List<TranslationAlignmentSegment>();
                    foreach (var item in segmentsElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var source = TryGetStringProperty(item, "source", "src", "original")?.Trim();
                        var target = TryGetStringProperty(item, "target", "translation", "translated")?.Trim();
                        if (!string.IsNullOrWhiteSpace(target))
                        {
                            parsed.Add(new TranslationAlignmentSegment(
                                string.IsNullOrWhiteSpace(source) ? fallbackSource : source,
                                target));
                        }
                    }

                    if (parsed.Count > 0)
                    {
                        return parsed;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        var text = rawResponse.Trim();
        return string.IsNullOrWhiteSpace(text)
            ? []
            : [new TranslationAlignmentSegment(fallbackSource, StripCommonJsonFence(text))];
    }

    private static string CreateTranslatedText(IReadOnlyList<TranslationAlignmentSegment> segments) =>
        string.Join(
            Environment.NewLine,
            segments
                .Select(segment => segment.TranslatedText.Trim())
                .Where(text => text.Length > 0));

    private static string NormalizeForComparisonDisplay(string text) =>
        string.Join(
            ' ',
            NormalizeTranslationWhitespace(text)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? TryGetStringProperty(JsonElement item, params string[] names)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static string ExtractJsonPayload(string text)
    {
        text = StripCommonJsonFence(text);
        var objectStart = text.IndexOf('{');
        var objectEnd = text.LastIndexOf('}');
        if (objectStart >= 0 && objectEnd > objectStart)
        {
            return text[objectStart..(objectEnd + 1)];
        }

        var arrayStart = text.IndexOf('[');
        var arrayEnd = text.LastIndexOf(']');
        return arrayStart >= 0 && arrayEnd > arrayStart
            ? text[arrayStart..(arrayEnd + 1)]
            : text;
    }

    private static string StripCommonJsonFence(string text)
    {
        text = (text ?? string.Empty).Trim();
        if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            text = text[7..].Trim();
        }
        else if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = text[3..].Trim();
        }

        return text.EndsWith("```", StringComparison.Ordinal)
            ? text[..^3].Trim()
            : text;
    }

    private static IEnumerable<string> SplitSourceText(string sourceText)
    {
        var blocks = sourceText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .SelectMany(SplitOversizedNaturalBlock);
        var builder = new StringBuilder();
        foreach (var block in blocks)
        {
            var normalized = block.TrimEnd();
            if (normalized.Length == 0)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString().Trim();
                    builder.Clear();
                }

                continue;
            }

            if (builder.Length > 0 &&
                builder.Length + normalized.Length + Environment.NewLine.Length > MaximumSegmentLength)
            {
                yield return builder.ToString().Trim();
                builder.Clear();
            }

            if (normalized.Length > MaximumSegmentLength)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString().Trim();
                    builder.Clear();
                }

                for (var offset = 0; offset < normalized.Length; offset += MaximumSegmentLength)
                {
                    yield return normalized.Substring(
                        offset,
                        Math.Min(MaximumSegmentLength, normalized.Length - offset));
                }

                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(normalized);
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString().Trim();
        }
    }

    private static IEnumerable<string> SplitOversizedNaturalBlock(string block)
    {
        if (block.Length <= MaximumSegmentLength)
        {
            yield return block;
            yield break;
        }

        var sentences = SplitBySentenceBoundary(block).ToArray();
        if (sentences.Length <= 1)
        {
            foreach (var chunk in SplitByLength(block))
            {
                yield return chunk;
            }

            yield break;
        }

        var builder = new StringBuilder();
        foreach (var sentence in sentences)
        {
            if (sentence.Length > MaximumSegmentLength)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString().Trim();
                    builder.Clear();
                }

                foreach (var chunk in SplitByLength(sentence))
                {
                    yield return chunk;
                }

                continue;
            }

            if (builder.Length > 0 && builder.Length + sentence.Length + 1 > MaximumSegmentLength)
            {
                yield return builder.ToString().Trim();
                builder.Clear();
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(sentence.Trim());
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString().Trim();
        }
    }

    private static IEnumerable<string> SplitBySentenceBoundary(string text)
    {
        var builder = new StringBuilder();
        foreach (var character in text)
        {
            builder.Append(character);
            if (character is '.' or '!' or '?' or ';' or '。' or '！' or '？' or '；')
            {
                yield return builder.ToString().Trim();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString().Trim();
        }
    }

    private static IEnumerable<string> SplitByLength(string text)
    {
        for (var offset = 0; offset < text.Length; offset += MaximumSegmentLength)
        {
            yield return text.Substring(
                offset,
                Math.Min(MaximumSegmentLength, text.Length - offset));
        }
    }

    private static bool TryExtractAlignedTranslation(
        string requestedSource,
        string cachedSource,
        string cachedTranslation,
        out string reusedTranslation)
    {
        reusedTranslation = string.Empty;
        var requestedSentences = SplitForReusableCache(requestedSource)
            .Select(NormalizeForCacheReuse)
            .Where(sentence => sentence.Length > 0)
            .ToArray();
        var cachedSourceSentences = SplitForReusableCache(cachedSource)
            .Select(NormalizeForCacheReuse)
            .Where(sentence => sentence.Length > 0)
            .ToArray();
        var cachedTranslationSentences = SplitForReusableCache(cachedTranslation)
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToArray();

        if (requestedSentences.Length == 0 ||
            cachedSourceSentences.Length != cachedTranslationSentences.Length)
        {
            return false;
        }

        for (var start = 0; start <= cachedSourceSentences.Length - requestedSentences.Length; start++)
        {
            var totalSimilarity = 0.0;
            var matches = true;
            for (var offset = 0; offset < requestedSentences.Length; offset++)
            {
                var similarity = CalculateCacheReuseSimilarity(
                    requestedSentences[offset],
                    cachedSourceSentences[start + offset]);
                if (similarity < MinimumReusableSegmentSimilarity)
                {
                    matches = false;
                    break;
                }

                totalSimilarity += similarity;
            }

            if (!matches ||
                totalSimilarity / requestedSentences.Length < MinimumReusableWindowSimilarity)
            {
                continue;
            }

            reusedTranslation = string.Join(
                Environment.NewLine,
                cachedTranslationSentences.Skip(start).Take(requestedSentences.Length));
            return !string.IsNullOrWhiteSpace(reusedTranslation);
        }

        return false;
    }

    private static bool TryComposeSemanticCacheTranslation(
        string requestedSource,
        IReadOnlyList<TranslationCacheSegment> candidateSegments,
        out string reusedTranslation,
        out IReadOnlyList<TranslationAlignmentSegment> reusedSegments)
    {
        reusedTranslation = string.Empty;
        reusedSegments = [];
        var requested = NormalizeForCacheReuse(requestedSource);
        if (requested.Length == 0 || candidateSegments.Count == 0)
        {
            return false;
        }

        foreach (var group in candidateSegments
                     .GroupBy(segment => segment.CacheKey)
                     .OrderByDescending(group => group.Max(segment => segment.LastUsedAt)))
        {
            var segments = group
                .OrderBy(segment => segment.SegmentIndex)
                .Where(segment =>
                    !string.IsNullOrWhiteSpace(segment.NormalizedSourceText) &&
                    !string.IsNullOrWhiteSpace(segment.TranslatedText))
                .ToArray();
            for (var start = 0; start < segments.Length; start++)
            {
                var combinedSource = new StringBuilder();
                var combinedTranslation = new List<string>();
                for (var end = start; end < segments.Length; end++)
                {
                    if (combinedSource.Length > 0)
                    {
                        combinedSource.Append(' ');
                    }

                    combinedSource.Append(segments[end].NormalizedSourceText);
                    combinedTranslation.Add(segments[end].TranslatedText.Trim());
                    var candidate = combinedSource.ToString();
                    var similarity = CalculateCacheReuseSimilarity(requested, candidate);
                    if (similarity >= MinimumReusableWindowSimilarity &&
                        HasCompatibleLength(requested, candidate))
                    {
                        reusedTranslation = string.Join(
                            Environment.NewLine,
                            combinedTranslation.Where(text => text.Length > 0));
                        reusedSegments = segments
                            .Skip(start)
                            .Take(end - start + 1)
                            .Select(segment => new TranslationAlignmentSegment(
                                segment.SourceText,
                                segment.TranslatedText))
                            .ToArray();
                        return !string.IsNullOrWhiteSpace(reusedTranslation);
                    }

                    if (candidate.Length > requested.Length * 1.8 && end > start)
                    {
                        break;
                    }
                }
            }
        }

        return false;
    }

    private static bool HasCompatibleLength(string requested, string candidate)
    {
        var shorter = Math.Min(requested.Length, candidate.Length);
        var longer = Math.Max(requested.Length, candidate.Length);
        return longer == 0 || (double)shorter / longer >= 0.72;
    }

    private static string NormalizeTranslationSourceText(string text)
    {
        var paragraphs = new List<string>();
        var lines = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\u0002", "-\n", StringComparison.Ordinal)
            .Split('\n');
        var paragraphLines = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                FlushParagraph(paragraphLines, paragraphs);
                continue;
            }

            paragraphLines.Add(trimmed);
        }

        FlushParagraph(paragraphLines, paragraphs);
        return string.Join(Environment.NewLine + Environment.NewLine, paragraphs);
    }

    private static void FlushParagraph(List<string> lines, List<string> paragraphs)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var paragraph = string.Join(' ', lines);
        paragraph = NormalizeForCacheIdentity(paragraph);
        if (paragraph.Length > 0)
        {
            paragraphs.Add(paragraph);
        }

        lines.Clear();
    }

    private static IEnumerable<string> SplitForReusableCache(string text)
    {
        var sentenceSegments = SplitBySentenceBoundary(text)
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .ToArray();
        if (sentenceSegments.Length > 1)
        {
            foreach (var segment in sentenceSegments)
            {
                yield return segment;
            }

            yield break;
        }

        foreach (var segment in NormalizeTranslationSourceText(text)
                     .Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return segment;
        }
    }

    private static string NormalizeForCacheIdentity(string text) =>
        string.Join(
            ' ',
            NormalizeTranslationWhitespace(text)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeTranslationWhitespace(string text) =>
        (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\u0002", "-\n", StringComparison.Ordinal)
            .Trim();

    private static string NormalizeForCacheReuse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousWasSpace = false;
        foreach (var character in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (char.IsWhiteSpace(character))
            {
                AppendSingleSpace(builder, ref previousWasSpace);
                continue;
            }

            if (category is UnicodeCategory.Control
                or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation
                or UnicodeCategory.ClosePunctuation
                or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation
                or UnicodeCategory.OtherPunctuation)
            {
                AppendSingleSpace(builder, ref previousWasSpace);
                continue;
            }

            builder.Append(character);
            previousWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    private static void AppendSingleSpace(StringBuilder builder, ref bool previousWasSpace)
    {
        if (builder.Length == 0 || previousWasSpace)
        {
            return;
        }

        builder.Append(' ');
        previousWasSpace = true;
    }

    private static double CalculateCacheReuseSimilarity(string requested, string cached)
    {
        if (requested.Length == 0 || cached.Length == 0)
        {
            return 0;
        }

        if (string.Equals(requested, cached, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        var shorterLength = Math.Min(requested.Length, cached.Length);
        if (shorterLength >= 20 &&
            (requested.Contains(cached, StringComparison.OrdinalIgnoreCase) ||
             cached.Contains(requested, StringComparison.OrdinalIgnoreCase)))
        {
            return 0.97;
        }

        return Math.Max(
            CalculateTokenJaccardSimilarity(requested, cached),
            CalculateBigramDiceSimilarity(requested, cached));
    }

    private static double CalculateTokenJaccardSimilarity(string left, string right)
    {
        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        var intersection = leftTokens.Count(token => rightTokens.Contains(token));
        var union = leftTokens.Count + rightTokens.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static double CalculateBigramDiceSimilarity(string left, string right)
    {
        var leftBigrams = CreateBigrams(left);
        var rightBigrams = CreateBigrams(right);
        if (leftBigrams.Count == 0 || rightBigrams.Count == 0)
        {
            return 0;
        }

        var intersection = 0;
        foreach (var (bigram, leftCount) in leftBigrams)
        {
            if (rightBigrams.TryGetValue(bigram, out var rightCount))
            {
                intersection += Math.Min(leftCount, rightCount);
            }
        }

        return (2.0 * intersection) / (leftBigrams.Values.Sum() + rightBigrams.Values.Sum());
    }

    private static Dictionary<string, int> CreateBigrams(string text)
    {
        var compact = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        var bigrams = new Dictionary<string, int>(StringComparer.Ordinal);
        if (compact.Length == 1)
        {
            bigrams[compact] = 1;
            return bigrams;
        }

        for (var index = 0; index < compact.Length - 1; index++)
        {
            var bigram = compact.Substring(index, 2);
            bigrams[bigram] = bigrams.TryGetValue(bigram, out var count) ? count + 1 : 1;
        }

        return bigrams;
    }

    private static int CalculateSegmentCount(string sourceText) =>
        string.IsNullOrWhiteSpace(sourceText) ? 0 : SplitSourceText(sourceText).Count();

    private static string CreateCacheKey(
        string sourceText,
        string targetLanguage,
        TranslationPreset preset,
        string? customInstruction,
        string? glossaryInstruction,
        string? preferenceInstruction)
    {
        var payload = string.Join(
            "\u001f",
            sourceText,
            targetLanguage,
            preset.ToString(),
            customInstruction ?? string.Empty,
            glossaryInstruction ?? string.Empty,
            preferenceInstruction ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    private string? CreateSamplePreferenceInstruction()
    {
        var preferred = CreateSampleLines(TranslationSampleKind.Preferred, "参考表达").ToArray();
        var rejected = CreateSampleLines(TranslationSampleKind.Rejected, "避免表达").ToArray();
        if (preferred.Length == 0 && rejected.Length == 0)
        {
            return null;
        }

        return string.Join("；", preferred.Concat(rejected));
    }

    private static string? CombineInstructions(params string?[] instructions)
    {
        var activeInstructions = instructions
            .Where(instruction => !string.IsNullOrWhiteSpace(instruction))
            .Select(instruction => instruction!.Trim())
            .ToArray();
        return activeInstructions.Length == 0
            ? null
            : string.Join("；", activeInstructions);
    }

    private static string? CreateSegmentContextInstruction(string previousSegmentContext) =>
        string.IsNullOrWhiteSpace(previousSegmentContext)
            ? null
            : $"延续上一段译文的术语和语气，上一段译文片段：{previousSegmentContext}";

    private static string CreateSegmentContext(string translatedText)
    {
        var compact = string.Join(
            ' ',
            translatedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length <= MaximumSegmentContextLength)
        {
            return compact;
        }

        return compact[^MaximumSegmentContextLength..];
    }

    private IEnumerable<string> CreateSampleLines(TranslationSampleKind sampleKind, string label) =>
        HistoryEntries
            .Select(item => item.Entry)
            .Where(entry => entry.SampleKind == sampleKind)
            .Take(MaximumSampleInstructionItems)
            .Select(entry =>
                $"{label}：原文「{TrimForInstruction(entry.SourcePreview)}」译为「{TrimForInstruction(entry.TranslatedText)}」");

    private static string TrimForInstruction(string text)
    {
        var compact = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= MaximumSamplePreviewLength
            ? compact
            : compact[..MaximumSamplePreviewLength] + "...";
    }

    private string? CreateGlossaryInstruction()
    {
        var matchingTerms = GlossaryEntries
            .Select(item => item.Entry)
            .Where(entry => SourceText.Contains(entry.SourceTerm, StringComparison.OrdinalIgnoreCase))
            .Take(30)
            .Select(entry => $"{entry.SourceTerm} => {entry.TargetTerm}")
            .ToArray();
        return matchingTerms.Length == 0 ? null : string.Join("；", matchingTerms);
    }

    private void UpsertGlossaryItem(TranslationGlossaryEntry entry)
    {
        for (var index = 0; index < GlossaryEntries.Count; index++)
        {
            if (string.Equals(
                    GlossaryEntries[index].Entry.SourceTerm,
                    entry.SourceTerm,
                    StringComparison.OrdinalIgnoreCase))
            {
                GlossaryEntries[index] = new TranslationGlossaryItemViewModel(entry);
                return;
            }
        }

        GlossaryEntries.Add(new TranslationGlossaryItemViewModel(entry));
    }

    private static string CreatePreview(string sourceText)
    {
        var preview = string.Join(
            ' ',
            sourceText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return preview.Length <= 120 ? preview : preview[..120] + "...";
    }

    private static UserErrorCode GetErrorCode(TranslationError error) => error switch
    {
        TranslationError.AuthenticationFailed => UserErrorCode.TranslationAuthenticationFailed,
        TranslationError.RateLimited => UserErrorCode.TranslationRateLimited,
        TranslationError.Timeout => UserErrorCode.TranslationTimeout,
        TranslationError.NetworkUnavailable => UserErrorCode.TranslationNetworkUnavailable,
        TranslationError.NetworkAccessDisabled => UserErrorCode.TranslationNetworkDisabled,
        TranslationError.MissingCredential => UserErrorCode.TranslationCredentialMissing,
        TranslationError.InvalidResponse => UserErrorCode.TranslationInvalidResponse,
        _ => UserErrorCode.TranslationUnexpectedFailure
    };

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void NotifyCommandStateChanged()
    {
        OnPropertyChanged(nameof(CanTranslate));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanCopySource));
        OnPropertyChanged(nameof(CanCopyTranslation));
        OnPropertyChanged(nameof(CanAddGlossaryEntry));
        OnPropertyChanged(nameof(CanGenerateStylePrompt));
        OnPropertyChanged(nameof(CanAddTranslationStyle));
        OnPropertyChanged(nameof(CanSaveTranslationStyle));
        OnPropertyChanged(nameof(CanDeleteTranslationStyle));
        OnPropertyChanged(nameof(CanRestoreTranslationStyle));
        foreach (var command in _commands)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum TranslationTaskState
{
    Idle,
    Ready,
    Translating,
    Completed,
    Cancelled,
    Failed
}

public sealed class TranslationStyleItemViewModel(TranslationStyleDefinition definition)
{
    public TranslationStyleDefinition Definition { get; } = definition;

    public string Id => Definition.Id;

    public string Name => Definition.Name;

    public TranslationPreset Preset =>
        Enum.TryParse<TranslationPreset>(Definition.Preset, ignoreCase: true, out var preset)
            ? preset
            : TranslationPreset.Custom;

    public string Prompt => Definition.Prompt;

    public bool IsBuiltIn => Definition.IsBuiltIn;

    public bool IsDeleted => Definition.IsDeleted;

    public string DisplayName => IsDeleted ? $"{Name}（已隐藏）" : Name;

    public string KindText => IsBuiltIn ? "内置预设" : "自定义";
}

public sealed record TranslationPresetOption(TranslationPreset Value, string Label);

public sealed record TargetLanguageOption(string Value, string Label);

public sealed record TranslationPanelSnapshot(
    string SourceText,
    string TranslatedText,
    TranslationPreset SelectedPreset,
    string TargetLanguage,
    string CustomInstruction,
    string CustomStyleName,
    string CustomStylePrompt,
    TranslationTaskState State,
    string StatusText,
    string? ErrorMessage,
    bool HasAttemptedTranslation);

public sealed class TranslationHistoryItemViewModel(TranslationHistoryEntry entry)
{
    public TranslationHistoryEntry Entry { get; } = entry;

    public string Preview => Entry.SourcePreview;

    public string ScopeText => Entry.SourceScope ?? Entry.SourceKind switch
    {
        TranslationSourceKind.CurrentPage => "当前页",
        TranslationSourceKind.PageRange => "页码范围",
        _ => "选中文字"
    };

    public string Summary =>
        $"{Entry.CreatedAt.LocalDateTime:g} · {ScopeText} · {Entry.CharacterCount} 字 · {Entry.SegmentCount} 段"
        + (Entry.SampleKind switch
        {
            TranslationSampleKind.Preferred => " · 参考样本",
            TranslationSampleKind.Rejected => " · 避开样本",
            _ => string.Empty
        });
}

public sealed class TranslationGlossaryItemViewModel(TranslationGlossaryEntry entry)
{
    public TranslationGlossaryEntry Entry { get; } = entry;

    public string SourceTerm => Entry.SourceTerm;

    public string TargetTerm => Entry.TargetTerm;

    public string DisplayText => $"{Entry.SourceTerm} → {Entry.TargetTerm}";
}

public sealed record TranslationComparisonSegmentViewModel(
    int Index,
    string SourceText,
    string TranslatedText);

internal sealed record TranslationAlignmentSegment(
    string SourceText,
    string TranslatedText);

using System.ComponentModel;
using System.Runtime.CompilerServices;
using LocalPdfReader.Application.Configuration;
using LocalPdfReader.Application.Translation;

namespace LocalPdfReader.App;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly ISettingsService _settingsService;
    private readonly ICredentialStore _credentialStore;
    private readonly ITranslationConnectionTester _connectionTester;
    private readonly IUserErrorService _userErrorService;
    private string _baseUrl = "https://api.deepseek.com";
    private string _model = "deepseek-chat";
    private string _targetLanguage = "zh-CN";
    private string _defaultPreset = "Academic";
    private int _timeoutSeconds = 60;
    private int _maximumCacheEntries = 500;
    private bool _stream = true;
    private bool _allowTranslationNetworkAccess = true;
    private bool _saveReadingPosition = true;
    private bool _recordRecentDocuments = true;
    private int _maximumRecentDocuments = 20;
    private string _searchShortcut = "Ctrl+F";
    private string _translationShortcut = "Ctrl+T";
    private string _statusText = "API 密钥不会显示，也不会写入设置文件。";
    private bool _isBusy;
    private AppSettings _loadedSettings = new();

    public SettingsViewModel(
        ISettingsService settingsService,
        ICredentialStore credentialStore,
        ITranslationConnectionTester connectionTester,
        IUserErrorService userErrorService)
    {
        _settingsService = settingsService;
        _credentialStore = credentialStore;
        _connectionTester = connectionTester;
        _userErrorService = userErrorService;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string BaseUrl { get => _baseUrl; set => SetProperty(ref _baseUrl, value); }

    public string Model { get => _model; set => SetProperty(ref _model, value); }

    public string TargetLanguage { get => _targetLanguage; set => SetProperty(ref _targetLanguage, value); }

    public string DefaultPreset { get => _defaultPreset; set => SetProperty(ref _defaultPreset, value); }

    public int TimeoutSeconds { get => _timeoutSeconds; set => SetProperty(ref _timeoutSeconds, value); }

    public int MaximumCacheEntries { get => _maximumCacheEntries; set => SetProperty(ref _maximumCacheEntries, value); }

    public bool Stream { get => _stream; set => SetProperty(ref _stream, value); }

    public bool AllowTranslationNetworkAccess
    {
        get => _allowTranslationNetworkAccess;
        set => SetProperty(ref _allowTranslationNetworkAccess, value);
    }

    public bool SaveReadingPosition
    {
        get => _saveReadingPosition;
        set => SetProperty(ref _saveReadingPosition, value);
    }

    public bool RecordRecentDocuments
    {
        get => _recordRecentDocuments;
        set => SetProperty(ref _recordRecentDocuments, value);
    }

    public int MaximumRecentDocuments
    {
        get => _maximumRecentDocuments;
        set => SetProperty(ref _maximumRecentDocuments, value);
    }

    public string SearchShortcut
    {
        get => _searchShortcut;
        set => SetProperty(ref _searchShortcut, value ?? string.Empty);
    }

    public string TranslationShortcut
    {
        get => _translationShortcut;
        set => SetProperty(ref _translationShortcut, value ?? string.Empty);
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanEdit));
            }
        }
    }

    public bool CanEdit => !IsBusy;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            var settings = await _settingsService.LoadAsync(cancellationToken);
            _loadedSettings = settings;
            var baseUrlWasNormalized = DeepSeekApiAddress.TryNormalizeOpenAiBaseUri(
                settings.Translation.BaseUrl,
                out var normalizedBaseUri)
                && !string.Equals(
                    settings.Translation.BaseUrl.TrimEnd('/'),
                    normalizedBaseUri.AbsoluteUri.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase);
            BaseUrl = baseUrlWasNormalized
                ? normalizedBaseUri.AbsoluteUri.TrimEnd('/')
                : settings.Translation.BaseUrl;
            Model = settings.Translation.Model;
            TargetLanguage = settings.Translation.TargetLanguage;
            DefaultPreset = settings.Translation.DefaultPreset;
            TimeoutSeconds = settings.Translation.TimeoutSeconds;
            MaximumCacheEntries = settings.Translation.MaximumCacheEntries;
            Stream = settings.Translation.Stream;
            AllowTranslationNetworkAccess = settings.Privacy.AllowTranslationNetworkAccess;
            SaveReadingPosition = settings.Session.SaveReadingPosition;
            RecordRecentDocuments = settings.History.RecordRecentDocuments;
            MaximumRecentDocuments = settings.History.MaximumRecentDocuments;
            SearchShortcut = settings.Shortcuts.SearchShortcut;
            TranslationShortcut = settings.Shortcuts.TranslationShortcut;
            StatusText = baseUrlWasNormalized
                ? "检测到 Anthropic 协议地址，已切换为本程序使用的 OpenAI 兼容地址；请点击保存。"
                : "设置已加载。已保存的 API 密钥不会回显。";
        }
        catch (Exception exception)
        {
            StatusText = _userErrorService.Report(UserErrorCode.SettingsReadFailed, exception).InlineText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> SaveAsync(string? newApiKey, CancellationToken cancellationToken)
    {
        if (!TryCreateSettings(out var settings, out var validationMessage))
        {
            StatusText = validationMessage;
            return false;
        }

        IsBusy = true;
        try
        {
            await _settingsService.SaveAsync(settings, cancellationToken);
            if (!string.IsNullOrWhiteSpace(newApiKey))
            {
                await _credentialStore.SaveSecretAsync(CredentialKeys.DeepSeekApiKey, newApiKey.Trim(), cancellationToken);
            }

            StatusText = string.IsNullOrWhiteSpace(newApiKey)
                ? "设置已保存，原有 API 密钥保持不变。"
                : "设置和 API 密钥已安全保存。";
            return true;
        }
        catch (Exception exception)
        {
            StatusText = _userErrorService.Report(UserErrorCode.SettingsWriteFailed, exception).InlineText;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task TestConnectionAsync(string? enteredApiKey, CancellationToken cancellationToken)
    {
        if (!AllowTranslationNetworkAccess)
        {
            StatusText = "隐私设置已禁止翻译网络访问。";
            return;
        }

        if (!TryCreateSettings(out var settings, out var validationMessage))
        {
            StatusText = validationMessage;
            return;
        }

        IsBusy = true;
        try
        {
            var apiKey = string.IsNullOrWhiteSpace(enteredApiKey)
                ? await _credentialStore.ReadSecretAsync(CredentialKeys.DeepSeekApiKey, cancellationToken)
                : enteredApiKey.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                StatusText = "请先输入 API 密钥，或保存一个 API 密钥。";
                return;
            }

            StatusText = "正在测试 DeepSeek 连接…";
            var result = await _connectionTester.TestAsync(settings.Translation, apiKey, cancellationToken);
            StatusText = result.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "连接测试已取消。";
        }
        catch (Exception exception)
        {
            StatusText = _userErrorService.Report(UserErrorCode.ConnectionTestFailed, exception).InlineText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteApiKeyAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            await _credentialStore.DeleteSecretAsync(CredentialKeys.DeepSeekApiKey, cancellationToken);
            StatusText = "已删除 Windows 凭据管理器中的 API 密钥。";
        }
        catch (Exception exception)
        {
            StatusText = _userErrorService.Report(UserErrorCode.CredentialDeleteFailed, exception).InlineText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryCreateSettings(out AppSettings settings, out string validationMessage)
    {
        settings = new AppSettings();
        if (!DeepSeekApiAddress.TryNormalizeOpenAiBaseUri(BaseUrl, out var uri))
        {
            validationMessage = "API 地址必须是有效的 HTTPS 地址。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Model) || string.IsNullOrWhiteSpace(TargetLanguage))
        {
            validationMessage = "模型名称和目标语言不能为空。";
            return false;
        }

        if (TimeoutSeconds is < 5 or > 300)
        {
            validationMessage = "超时时间必须在 5 至 300 秒之间。";
            return false;
        }

        if (MaximumRecentDocuments is < 1 or > 100)
        {
            validationMessage = "最近文件数量必须在 1 至 100 之间。";
            return false;
        }

        if (MaximumCacheEntries is < 0 or > 10000)
        {
            validationMessage = "翻译缓存上限必须在 0 至 10000 条之间，0 表示不保存缓存。";
            return false;
        }

        if (!TryNormalizeShortcut(SearchShortcut, out var normalizedSearchShortcut, out validationMessage))
        {
            validationMessage = $"搜索快捷键无效：{validationMessage}";
            return false;
        }

        if (!TryNormalizeShortcut(TranslationShortcut, out var normalizedTranslationShortcut, out validationMessage))
        {
            validationMessage = $"翻译快捷键无效：{validationMessage}";
            return false;
        }

        if (string.Equals(normalizedSearchShortcut, normalizedTranslationShortcut, StringComparison.OrdinalIgnoreCase))
        {
            validationMessage = "搜索快捷键和翻译快捷键不能相同。";
            return false;
        }

        settings = _loadedSettings with
        {
            Translation = new TranslationSettings
            {
                Provider = "DeepSeek",
                BaseUrl = uri.AbsoluteUri.TrimEnd('/'),
                Model = Model.Trim(),
                TargetLanguage = TargetLanguage.Trim(),
                DefaultPreset = DefaultPreset,
                TimeoutSeconds = TimeoutSeconds,
                MaximumCacheEntries = MaximumCacheEntries,
                Stream = Stream
            },
            Privacy = _loadedSettings.Privacy with
            {
                AllowTranslationNetworkAccess = AllowTranslationNetworkAccess,
                LogSourceText = false
            },
            Session = _loadedSettings.Session with
            {
                SaveReadingPosition = SaveReadingPosition
            },
            History = _loadedSettings.History with
            {
                RecordRecentDocuments = RecordRecentDocuments,
                MaximumRecentDocuments = MaximumRecentDocuments
            },
            Shortcuts = _loadedSettings.Shortcuts with
            {
                SearchShortcut = normalizedSearchShortcut,
                TranslationShortcut = normalizedTranslationShortcut
            }
        };
        validationMessage = string.Empty;
        return true;
    }

    private static bool TryNormalizeShortcut(
        string shortcut,
        out string normalized,
        out string validationMessage)
    {
        normalized = string.Empty;
        shortcut = (shortcut ?? string.Empty).Trim();
        if (shortcut.Length == 0)
        {
            validationMessage = "快捷键不能为空。";
            return false;
        }

        var parts = shortcut
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Equals("Control", StringComparison.OrdinalIgnoreCase) ? "Ctrl" : part)
            .ToArray();
        if (parts.Length is < 2 or > 4 || !parts.Any(part => part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)))
        {
            validationMessage = "请使用 Ctrl+按键 或 Ctrl+Shift+按键 的格式。";
            return false;
        }

        var hasShift = false;
        var hasAlt = false;
        string? key = null;
        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                hasShift = true;
                continue;
            }

            if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                hasAlt = true;
                continue;
            }

            if (key is not null)
            {
                validationMessage = "只能包含一个主按键。";
                return false;
            }

            key = NormalizeKeyName(part);
        }

        if (key is null)
        {
            validationMessage = "缺少主按键。";
            return false;
        }

        var reservedShortcut = hasAlt
            ? $"Ctrl+Alt+{key}"
            : hasShift
                ? $"Ctrl+Shift+{key}"
                : $"Ctrl+{key}";
        if (reservedShortcut is "Ctrl+C" or "Ctrl+V" or "Ctrl+S")
        {
            validationMessage = "Ctrl+C、Ctrl+V 和 Ctrl+S 已保留给复制、粘贴和保存批注。";
            return false;
        }

        var normalizedParts = new List<string> { "Ctrl" };
        if (hasAlt)
        {
            normalizedParts.Add("Alt");
        }

        if (hasShift)
        {
            normalizedParts.Add("Shift");
        }

        normalizedParts.Add(key);
        normalized = string.Join('+', normalizedParts);
        validationMessage = string.Empty;
        return true;
    }

    private static string NormalizeKeyName(string key)
    {
        key = key.Trim();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return key.ToUpperInvariant();
        }

        return key.ToUpperInvariant() switch
        {
            "ESC" => "Escape",
            _ => key.ToUpperInvariant()
        };
    }

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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

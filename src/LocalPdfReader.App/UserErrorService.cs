using Microsoft.Extensions.Logging;

namespace LocalPdfReader.App;

public interface IUserErrorService
{
    UserFacingError Report(UserErrorCode code, Exception exception);
}

public sealed class UserErrorService(ILogger<UserErrorService> logger) : IUserErrorService
{
    public UserFacingError Report(UserErrorCode code, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var error = Create(code);
        logger.LogError(
            new EventId(4000, error.Code),
            exception,
            "Operation failed with user-facing error code {ErrorCode}.",
            error.Code);
        return error;
    }

    private static UserFacingError Create(UserErrorCode code) => code switch
    {
        UserErrorCode.AppStartFailed => new("APP_START_FAILED", "应用启动失败", "应用无法完成启动。", "请重新启动应用；如果问题持续出现，请查看本地日志。"),
        UserErrorCode.WorkerRestartFailed => new("WORKER_RESTART_FAILED", "PDF 工作进程恢复失败", "PDF 工作进程无法重新启动。", "请关闭并重新启动应用。"),
        UserErrorCode.DocumentOpenFailed => new("PDF_OPEN_FAILED", "打开 PDF 失败", "无法打开或读取所选 PDF。", "请确认文件存在、未被占用且格式有效。"),
        UserErrorCode.DocumentRecoveryFailed => new("PDF_RECOVERY_FAILED", "PDF 恢复失败", "工作进程已重启，但无法恢复当前 PDF。", "请重新打开该文件。"),
        UserErrorCode.PageRenderFailed => new("PDF_PAGE_RENDER_FAILED", "页面渲染失败", "当前 PDF 页面无法显示。", "请重试当前操作；如果问题持续出现，请重新打开文件。"),
        UserErrorCode.TextExtractionFailed => new("PDF_TEXT_EXTRACTION_FAILED", "文字提取失败", "无法读取当前页面的文字层。", "请确认该页面包含可选择文字，或切换页面后重试。"),
        UserErrorCode.ClipboardWriteFailed => new("CLIPBOARD_WRITE_FAILED", "复制失败", "无法写入 Windows 剪贴板。", "请稍后重试，或关闭可能占用剪贴板的程序。"),
        UserErrorCode.SettingsReadFailed => new("SETTINGS_READ_FAILED", "读取设置失败", "无法读取本地设置。", "程序将继续使用当前或默认设置。"),
        UserErrorCode.SettingsWriteFailed => new("SETTINGS_WRITE_FAILED", "保存设置失败", "无法保存本地设置或凭据。", "请检查当前用户是否有本地数据目录写入权限。"),
        UserErrorCode.DatabaseUnavailable => new("DATABASE_UNAVAILABLE", "本地阅读数据不可用", "无法初始化本地阅读数据库。PDF 阅读和翻译仍可继续使用。", "最近文件、阅读位置和批注暂时不可用；请查看本地日志。"),
        UserErrorCode.AnnotationWriteFailed => new("ANNOTATION_WRITE_FAILED", "保存批注失败", "批注操作未能写入本地数据库。", "原 PDF 未被修改；请检查本地数据目录后重试。"),
        UserErrorCode.CredentialDeleteFailed => new("CREDENTIAL_WRITE_FAILED", "删除凭据失败", "无法删除 Windows 凭据管理器中的 API 密钥。", "请稍后重试或在凭据管理器中手动检查。"),
        UserErrorCode.ConnectionTestFailed => new("TRANSLATION_PROVIDER_ERROR", "连接测试失败", "无法完成翻译服务连接测试。", "请检查网络、API 地址和模型设置后重试。"),
        UserErrorCode.TranslationSettingsReadFailed => new("SETTINGS_READ_FAILED", "翻译设置加载失败", "无法读取翻译设置。", "请打开设置页面检查配置。"),
        UserErrorCode.TranslationAuthenticationFailed => new("TRANSLATION_AUTHENTICATION_FAILED", "翻译失败", "API 身份验证失败。", "请在设置中检查 API 密钥。"),
        UserErrorCode.TranslationRateLimited => new("TRANSLATION_RATE_LIMITED", "翻译失败", "API 请求受到限流。", "请稍后手动重试。"),
        UserErrorCode.TranslationTimeout => new("TRANSLATION_REQUEST_TIMEOUT", "翻译失败", "翻译请求超时。", "请缩短原文或提高超时时间后重试。"),
        UserErrorCode.TranslationNetworkUnavailable => new("TRANSLATION_PROVIDER_ERROR", "翻译失败", "无法连接翻译服务。", "请检查网络和 API 地址。"),
        UserErrorCode.TranslationNetworkDisabled => new("TRANSLATION_NETWORK_DISABLED", "翻译失败", "隐私设置已禁止翻译联网。", "请在设置中允许翻译网络访问。"),
        UserErrorCode.TranslationCredentialMissing => new("TRANSLATION_API_KEY_MISSING", "翻译失败", "尚未保存 DeepSeek API 密钥。", "请先在设置中保存密钥。"),
        UserErrorCode.TranslationInvalidResponse => new("TRANSLATION_RESPONSE_INVALID", "翻译失败", "翻译服务返回了无法识别的内容。", "请稍后重试或检查模型设置。"),
        UserErrorCode.TranslationUnexpectedFailure => new("TRANSLATION_PROVIDER_ERROR", "翻译失败", "翻译过程中发生未预期错误。", "请稍后手动重试。"),
        _ => new("UNEXPECTED_ERROR", "操作失败", "操作未能完成。", "请稍后重试；如果问题持续出现，请查看本地日志。")
    };
}

public enum UserErrorCode
{
    AppStartFailed,
    WorkerRestartFailed,
    DocumentOpenFailed,
    DocumentRecoveryFailed,
    PageRenderFailed,
    TextExtractionFailed,
    ClipboardWriteFailed,
    SettingsReadFailed,
    SettingsWriteFailed,
    DatabaseUnavailable,
    AnnotationWriteFailed,
    CredentialDeleteFailed,
    ConnectionTestFailed,
    TranslationSettingsReadFailed,
    TranslationAuthenticationFailed,
    TranslationRateLimited,
    TranslationTimeout,
    TranslationNetworkUnavailable,
    TranslationNetworkDisabled,
    TranslationCredentialMissing,
    TranslationInvalidResponse,
    TranslationUnexpectedFailure
}

public sealed record UserFacingError(
    string Code,
    string Title,
    string Message,
    string SuggestedAction)
{
    public string InlineText => $"{Message} {SuggestedAction}（错误代码：{Code}）";

    public string DialogText => $"{Message}\n\n{SuggestedAction}\n\n错误代码：{Code}";
}

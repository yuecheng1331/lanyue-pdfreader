using LocalPdfReader.Application.Translation;

namespace LocalPdfReader.Infrastructure.Translation;

public sealed class LocalWordTranslationService : IWordTranslationService
{
    private static readonly IReadOnlyDictionary<string, string> EnglishToSimplifiedChinese =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["architecture"] = "结构；体系架构",
            ["algorithm"] = "算法",
            ["annotation"] = "批注；标注",
            ["classification"] = "分类",
            ["consistency"] = "一致性",
            ["detection"] = "检测",
            ["framework"] = "框架",
            ["image"] = "图像",
            ["learning"] = "学习",
            ["method"] = "方法",
            ["model"] = "模型",
            ["prediction"] = "预测",
            ["regularization"] = "正则化",
            ["segmentation"] = "分割",
            ["strategy"] = "策略",
            ["training"] = "训练",
            ["translation"] = "翻译"
        };

    public Task<WordTranslationResult?> TryTranslateAsync(
        string sourceText,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var word = (sourceText ?? string.Empty).Trim();
        if (!IsSingleEnglishWord(word) ||
            targetLanguage is not ("zh-CN" or "zh" or "zh-Hans"))
        {
            return Task.FromResult<WordTranslationResult?>(null);
        }

        return Task.FromResult(
            EnglishToSimplifiedChinese.TryGetValue(word, out var translated)
                ? new WordTranslationResult(word, translated, "本地开放词典")
                : null);
    }

    private static bool IsSingleEnglishWord(string text) =>
        text.Length is >= 2 and <= 40 &&
        text.All(character => char.IsAsciiLetter(character) || character is '-' or '\'') &&
        text.Any(char.IsAsciiLetter);
}

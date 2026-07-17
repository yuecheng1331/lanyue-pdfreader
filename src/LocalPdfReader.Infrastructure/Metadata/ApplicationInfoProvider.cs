using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using LocalPdfReader.Application.Metadata;

namespace LocalPdfReader.Infrastructure.Metadata;

public sealed class ApplicationInfoProvider(
    Assembly applicationAssembly,
    string applicationDirectory,
    string settingsDirectory,
    string logDirectory) : IApplicationInfoProvider
{
    public ApplicationInfo GetApplicationInfo()
    {
        var informationalVersion = applicationAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? applicationAssembly.GetName().Version?.ToString() ?? "未知";
        var metadata = applicationAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(item => item.Key, item => item.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        var productVersion = informationalVersion.Split('+', 2)[0];
        var buildCommit = ReadMetadata(metadata, "BuildCommit", "local") is "local" or "unknown"
            ? "本地构建"
            : ReadMetadata(metadata, "BuildCommit", "未知");
        var buildTime = ReadMetadata(metadata, "BuildTimestampUtc", "local");

        if (buildTime is "local" or "unknown")
        {
            buildTime = File.Exists(applicationAssembly.Location)
                ? File.GetLastWriteTimeUtc(applicationAssembly.Location).ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
                : "未知";
        }

        var productName = applicationAssembly
            .GetCustomAttribute<AssemblyProductAttribute>()?
            .Product ?? "澜阅 PDF";

        return new ApplicationInfo(
            productName,
            productVersion,
            buildCommit,
            buildTime,
            ReadPdfiumVersion(),
            RuntimeInformation.FrameworkDescription,
            logDirectory,
            settingsDirectory);
    }

    private string ReadPdfiumVersion()
    {
        var runtimeIdentifier = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => string.Empty
        };
        var candidatePaths = new[]
        {
            // 自包含发布会将原生库放在程序根目录。
            Path.Combine(applicationDirectory, "pdfium.dll"),
            // 普通开发构建保留 NuGet 的 runtimes/<RID>/native 目录结构。
            Path.Combine(applicationDirectory, "runtimes", runtimeIdentifier, "native", "pdfium.dll")
        };
        var pdfiumPath = candidatePaths.FirstOrDefault(File.Exists);
        if (pdfiumPath is null)
        {
            return "未找到 pdfium.dll";
        }

        var versionInfo = FileVersionInfo.GetVersionInfo(pdfiumPath);
        return versionInfo.ProductVersion ?? versionInfo.FileVersion ?? "原生库未提供版本信息";
    }

    private static string ReadMetadata(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        string fallback) =>
        metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
}

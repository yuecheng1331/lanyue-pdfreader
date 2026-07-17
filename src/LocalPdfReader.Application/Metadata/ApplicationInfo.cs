namespace LocalPdfReader.Application.Metadata;

public sealed record ApplicationInfo(
    string ProductName,
    string ProductVersion,
    string BuildCommit,
    string BuildTime,
    string PdfiumVersion,
    string DotNetVersion,
    string LogDirectory,
    string SettingsDirectory);

public interface IApplicationInfoProvider
{
    ApplicationInfo GetApplicationInfo();
}

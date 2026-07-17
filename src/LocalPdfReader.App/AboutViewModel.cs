using LocalPdfReader.Application.Metadata;

namespace LocalPdfReader.App;

public sealed class AboutViewModel(IApplicationInfoProvider applicationInfoProvider)
{
    public ApplicationInfo ApplicationInfo { get; } = applicationInfoProvider.GetApplicationInfo();
}

using System.Windows;

namespace LocalPdfReader.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e) =>
        await _viewModel.InitializeAsync(CancellationToken.None);

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (await _viewModel.SaveAsync(ApiKeyBox.Password, CancellationToken.None))
        {
            ApiKeyBox.Clear();
        }
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.TestConnectionAsync(ApiKeyBox.Password, CancellationToken.None);

    private async void DeleteApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "确定删除 Windows 凭据管理器中保存的 DeepSeek API 密钥吗？",
            "删除 API 密钥",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            await _viewModel.DeleteApiKeyAsync(CancellationToken.None);
            ApiKeyBox.Clear();
        }
    }
}

using System.Windows;

namespace LocalPdfReader.App;

public partial class TranslationPreferencesWindow : Window
{
    public TranslationPreferencesWindow(TranslationPanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

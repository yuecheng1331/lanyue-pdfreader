using System.Windows;

namespace LocalPdfReader.App;

public partial class TranslationHistoryWindow : Window
{
    public TranslationHistoryWindow(TranslationPanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

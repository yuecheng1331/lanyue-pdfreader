using System.Windows;

namespace LocalPdfReader.App;

public partial class TranslationComparisonWindow : Window
{
    public TranslationComparisonWindow(TranslationPanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

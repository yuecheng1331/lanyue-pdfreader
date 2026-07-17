using System.Windows;

namespace LocalPdfReader.App;

public partial class TranslationTextEditorWindow : Window
{
    public TranslationTextEditorWindow(string title, string text)
    {
        InitializeComponent();
        Title = title;
        TitleTextBlock.Text = title;
        EditorTextBox.Text = text ?? string.Empty;
        EditorTextBox.Focus();
    }

    public string Text => EditorTextBox.Text;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}

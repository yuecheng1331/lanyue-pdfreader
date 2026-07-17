using System.Windows;
using System.Windows.Input;

namespace LocalPdfReader.App;

public partial class PasswordPromptWindow : Window
{
    public PasswordPromptWindow(string fileName, bool isRetry)
    {
        InitializeComponent();
        PromptTitle = isRetry ? "PDF 密码不正确" : "此 PDF 需要密码";
        PromptMessage = isRetry
            ? $"请重新输入“{fileName}”的打开密码。"
            : $"请输入“{fileName}”的打开密码。";
        DataContext = this;
    }

    public string PromptTitle { get; }

    public string PromptMessage { get; }

    public string Password => DocumentPasswordBox.Password;

    private void PasswordPromptWindow_Loaded(object sender, RoutedEventArgs e) =>
        DocumentPasswordBox.Focus();

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void DocumentPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
            Close();
        }
    }
}

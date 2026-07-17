using System.Windows;
using System.Windows.Controls;

namespace LocalPdfReader.App;

/// <summary>
/// Isolates the home-page visual tree. The main window keeps the document-opening
/// workflow while this view only reports user intent.
/// </summary>
public partial class RecentDocumentsPanel : UserControl
{
    public RecentDocumentsPanel()
    {
        InitializeComponent();
    }

    public event EventHandler? OpenDocumentRequested;

    public event EventHandler? RestoreSessionRequested;

    public event EventHandler<RecentDocumentRequestedEventArgs>? RecentDocumentRequested;

    public event EventHandler<RecentDocumentRequestedEventArgs>? RemoveRecentDocumentRequested;

    public event EventHandler? ClearRecentDocumentsRequested;

    private void OpenDocumentButton_Click(object sender, RoutedEventArgs e) =>
        OpenDocumentRequested?.Invoke(this, EventArgs.Empty);

    private void RestoreSessionButton_Click(object sender, RoutedEventArgs e) =>
        RestoreSessionRequested?.Invoke(this, EventArgs.Empty);

    private void RecentDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecentDocumentItemViewModel document })
        {
            RecentDocumentRequested?.Invoke(this, new RecentDocumentRequestedEventArgs(document));
        }
    }

    private void RemoveRecentDocumentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecentDocumentItemViewModel document })
        {
            RemoveRecentDocumentRequested?.Invoke(this, new RecentDocumentRequestedEventArgs(document));
        }
    }

    private void ClearRecentDocumentsButton_Click(object sender, RoutedEventArgs e) =>
        ClearRecentDocumentsRequested?.Invoke(this, EventArgs.Empty);
}

public sealed class RecentDocumentRequestedEventArgs(RecentDocumentItemViewModel document) : EventArgs
{
    public RecentDocumentItemViewModel Document { get; } = document;
}

using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using LocalPdfReader.Application.Reader;
using LocalPdfReader.Domain;

namespace LocalPdfReader.App;

/// <summary>
/// Holds state that belongs to exactly one open document tab.  Keeping this
/// separate prevents later reading modes from accidentally sharing state between
/// documents.
/// </summary>
public sealed class DocumentTabItemViewModel : INotifyPropertyChanged
{
    private bool _isActive;
    private bool _isExternallyModified;
    private bool _hasUnsavedAnnotations;
    private string _externalModificationMessage = string.Empty;

    internal DocumentTabItemViewModel(
        string fullPath,
        ReaderState readerState,
        DocumentRecord? persistenceRecord,
        double horizontalScrollOffset,
        double verticalScrollOffset,
        TranslationPanelSnapshot translationSnapshot,
        DocumentSearchViewModel search,
        DocumentNavigationViewModel navigation,
        DocumentAnnotationViewModel annotations)
    {
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        ReaderState = readerState;
        PersistenceRecord = persistenceRecord;
        ViewportState.UpdateScrollOffsets(horizontalScrollOffset, verticalScrollOffset);
        TranslationSnapshot = translationSnapshot;
        Search = search;
        Navigation = navigation;
        Annotations = annotations;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FileName { get; }

    public string FullPath { get; }

    public DocumentSearchViewModel Search { get; }

    public DocumentNavigationViewModel Navigation { get; }

    public DocumentAnnotationViewModel Annotations { get; }

    public ObservableCollection<DocumentPageViewModel> Pages { get; } = [];

    public bool IsActive
    {
        get => _isActive;
        internal set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    internal ReaderState ReaderState { get; }

    internal ReadingViewportState ViewportState { get; } = new();

    internal DocumentRecord? PersistenceRecord { get; set; }

    internal double HorizontalScrollOffset
    {
        get => ViewportState.HorizontalOffset;
        set => ViewportState.UpdateScrollOffsets(value, ViewportState.VerticalOffset);
    }

    public bool IsExternallyModified
    {
        get => _isExternallyModified;
        internal set
        {
            if (_isExternallyModified == value)
            {
                return;
            }

            _isExternallyModified = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExternallyModified)));
        }
    }

    public bool HasUnsavedAnnotations
    {
        get => _hasUnsavedAnnotations;
        internal set
        {
            if (_hasUnsavedAnnotations == value)
            {
                return;
            }

            _hasUnsavedAnnotations = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUnsavedAnnotations)));
        }
    }

    public string ExternalModificationMessage
    {
        get => _externalModificationMessage;
        internal set
        {
            if (_externalModificationMessage == value)
            {
                return;
            }

            _externalModificationMessage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExternalModificationMessage)));
        }
    }

    internal double VerticalScrollOffset
    {
        get => ViewportState.VerticalOffset;
        set => ViewportState.UpdateScrollOffsets(ViewportState.HorizontalOffset, value);
    }

    internal TranslationPanelSnapshot TranslationSnapshot { get; set; }

    internal DocumentFileSnapshot? FileSnapshot { get; set; }
}

internal sealed record DocumentFileSnapshot(long Length, DateTime LastWriteTimeUtc);

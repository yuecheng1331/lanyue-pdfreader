using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LocalPdfReader.Domain;

namespace LocalPdfReader.App;

public sealed class DocumentAnnotationViewModel : INotifyPropertyChanged
{
    private bool _sortByModifiedTime;
    private AnnotationItemViewModel? _selectedItem;

    public DocumentAnnotationViewModel(
        bool isAvailable,
        string availabilityMessage,
        IEnumerable<TextHighlightAnnotation>? annotations = null)
    {
        IsAvailable = isAvailable;
        AvailabilityMessage = availabilityMessage;
        ReplaceAll(annotations ?? []);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AnnotationItemViewModel> Items { get; } = [];

    public bool IsAvailable { get; private set; }

    public string AvailabilityMessage { get; private set; }

    public string CountText => $"共 {Items.Count} 条批注";

    public bool SortByModifiedTime
    {
        get => _sortByModifiedTime;
        set
        {
            if (_sortByModifiedTime == value)
            {
                return;
            }

            _sortByModifiedTime = value;
            OnPropertyChanged();
            Resort();
        }
    }

    public AnnotationItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value))
            {
                return;
            }

            _selectedItem = value;
            OnPropertyChanged();
        }
    }

    public void SetAvailability(bool isAvailable, string message)
    {
        IsAvailable = isAvailable;
        AvailabilityMessage = message;
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(AvailabilityMessage));
    }

    public void ReplaceAll(IEnumerable<TextHighlightAnnotation> annotations)
    {
        var selectedId = SelectedItem?.Annotation.AnnotationId;
        Items.Clear();
        foreach (var annotation in annotations)
        {
            Items.Add(new AnnotationItemViewModel(annotation));
        }

        Resort();
        SelectedItem = selectedId is { } id
            ? Items.FirstOrDefault(item => item.Annotation.AnnotationId == id)
            : null;
        OnPropertyChanged(nameof(CountText));
    }

    public AnnotationItemViewModel Add(TextHighlightAnnotation annotation)
    {
        var item = new AnnotationItemViewModel(annotation);
        Items.Add(item);
        Resort();
        SelectedItem = item;
        OnPropertyChanged(nameof(CountText));
        return item;
    }

    public bool Remove(AnnotationItemViewModel item)
    {
        if (!Items.Remove(item))
        {
            return false;
        }

        if (ReferenceEquals(SelectedItem, item))
        {
            SelectedItem = null;
        }

        OnPropertyChanged(nameof(CountText));
        return true;
    }

    public void Resort()
    {
        var ordered = SortByModifiedTime
            ? Items.OrderByDescending(item => item.Annotation.ModifiedAt).ToArray()
            : Items.OrderBy(item => item.Annotation.PageIndex)
                .ThenBy(item => item.Annotation.CharacterStart)
                .ToArray();
        for (var targetIndex = 0; targetIndex < ordered.Length; targetIndex++)
        {
            var currentIndex = Items.IndexOf(ordered[targetIndex]);
            if (currentIndex != targetIndex)
            {
                Items.Move(currentIndex, targetIndex);
            }
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AnnotationItemViewModel : INotifyPropertyChanged
{
    private AnnotationColor _selectedColor;
    private string _draftNote;

    public AnnotationItemViewModel(TextHighlightAnnotation annotation)
    {
        Annotation = annotation;
        _selectedColor = annotation.Color;
        _draftNote = annotation.Note ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TextHighlightAnnotation Annotation { get; private set; }

    public IReadOnlyList<AnnotationColorOptionViewModel> ColorOptions => AnnotationColorOptionViewModel.All;

    public string PageLabel => $"第 {Annotation.PageIndex + 1} 页";

    public string SelectedTextPreview => Annotation.SelectedTextPreview;

    public string ModifiedAtText => Annotation.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public AnnotationColor SelectedColor
    {
        get => _selectedColor;
        set
        {
            if (_selectedColor == value)
            {
                return;
            }

            _selectedColor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
    }

    public string DraftNote
    {
        get => _draftNote;
        set
        {
            value ??= string.Empty;
            if (_draftNote == value)
            {
                return;
            }

            _draftNote = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
    }

    public bool HasUnsavedChanges =>
        SelectedColor != Annotation.Color ||
        !string.Equals(DraftNote, Annotation.Note ?? string.Empty, StringComparison.Ordinal);

    public void Apply(TextHighlightAnnotation annotation)
    {
        Annotation = annotation;
        _selectedColor = annotation.Color;
        _draftNote = annotation.Note ?? string.Empty;
        OnPropertyChanged(nameof(Annotation));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(SelectedTextPreview));
        OnPropertyChanged(nameof(ModifiedAtText));
        OnPropertyChanged(nameof(SelectedColor));
        OnPropertyChanged(nameof(DraftNote));
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record AnnotationColorOptionViewModel(AnnotationColor Value, string Label)
{
    public static IReadOnlyList<AnnotationColorOptionViewModel> All { get; } =
    [
        new(AnnotationColor.Yellow, "黄色"),
        new(AnnotationColor.Green, "绿色"),
        new(AnnotationColor.Blue, "蓝色"),
        new(AnnotationColor.Pink, "粉色")
    ];
}

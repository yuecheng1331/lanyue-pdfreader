using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using LocalPdfReader.Domain;

namespace LocalPdfReader.App;

public sealed class DocumentNavigationViewModel : INotifyPropertyChanged
{
    private string _outlineStatusText = "正在读取 PDF 目录...";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DocumentOutlineItemViewModel> OutlineItems { get; } = [];

    public ObservableCollection<DocumentThumbnailViewModel> Thumbnails { get; } = [];

    public string OutlineStatusText
    {
        get => _outlineStatusText;
        private set => SetProperty(ref _outlineStatusText, value);
    }

    public bool HasOutlineItems => OutlineItems.Count > 0;

    public void ReplaceOutline(IReadOnlyList<PdfOutlineItem> items)
    {
        OutlineItems.Clear();
        foreach (var item in items)
        {
            OutlineItems.Add(new DocumentOutlineItemViewModel(item, depth: 0));
        }

        OutlineStatusText = OutlineItems.Count == 0
            ? "当前 PDF 未提供目录。"
            : $"已读取 {CountOutlineItems(OutlineItems)} 个目录项。";
        OnPropertyChanged(nameof(HasOutlineItems));
    }

    public void SetOutlineUnavailable(string message)
    {
        OutlineItems.Clear();
        OutlineStatusText = message;
        OnPropertyChanged(nameof(HasOutlineItems));
    }

    public void EnsureThumbnails(int pageCount)
    {
        while (Thumbnails.Count < pageCount)
        {
            Thumbnails.Add(new DocumentThumbnailViewModel(Thumbnails.Count));
        }

        while (Thumbnails.Count > pageCount)
        {
            Thumbnails.RemoveAt(Thumbnails.Count - 1);
        }
    }

    public void SetCurrentPage(int pageIndex)
    {
        foreach (var thumbnail in Thumbnails)
        {
            thumbnail.IsCurrentPage = thumbnail.PageIndex == pageIndex;
        }
    }

    public void ClearThumbnailImages()
    {
        foreach (var thumbnail in Thumbnails)
        {
            thumbnail.ClearImage();
        }
    }

    public void TrimThumbnailCache(int maximumImages, DocumentThumbnailViewModel? keep = null)
    {
        var cached = Thumbnails
            .Where(thumbnail => thumbnail.HasImage && !ReferenceEquals(thumbnail, keep))
            .OrderByDescending(thumbnail => thumbnail.LastAccessedUtc)
            .Skip(Math.Max(0, maximumImages - (keep?.HasImage == true ? 1 : 0)))
            .ToArray();

        foreach (var thumbnail in cached)
        {
            thumbnail.ClearImage();
        }
    }

    private static int CountOutlineItems(IEnumerable<DocumentOutlineItemViewModel> items) =>
        items.Sum(item => 1 + CountOutlineItems(item.Children));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class DocumentOutlineItemViewModel
{
    internal DocumentOutlineItemViewModel(PdfOutlineItem item, int depth)
    {
        Title = string.IsNullOrWhiteSpace(item.Title) ? "未命名目录项" : item.Title;
        PageIndex = item.PageIndex;
        Depth = depth;
        PageLabel = item.PageIndex is { } pageIndex ? $"第 {pageIndex + 1} 页" : "无页码目标";
        foreach (var child in item.Children)
        {
            Children.Add(new DocumentOutlineItemViewModel(child, depth + 1));
        }
    }

    public string Title { get; }

    public int? PageIndex { get; }

    public int Depth { get; }

    public string PageLabel { get; }

    public ObservableCollection<DocumentOutlineItemViewModel> Children { get; } = [];

    public bool CanNavigate => PageIndex is not null;

    public double Indent => Depth * 14;
}

public sealed class DocumentThumbnailViewModel : INotifyPropertyChanged
{
    private ImageSource? _image;
    private bool _isRendering;
    private bool _isCurrentPage;

    internal DocumentThumbnailViewModel(int pageIndex)
    {
        PageIndex = pageIndex;
        PageNumber = pageIndex + 1;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int PageIndex { get; }

    public int PageNumber { get; }

    public string PageLabel => $"第 {PageNumber} 页";

    public DateTime LastAccessedUtc { get; private set; } = DateTime.MinValue;

    public ImageSource? Image
    {
        get => _image;
        private set
        {
            if (SetProperty(ref _image, value))
            {
                OnPropertyChanged(nameof(HasImage));
            }
        }
    }

    public bool HasImage => Image is not null;

    public bool IsRendering
    {
        get => _isRendering;
        set => SetProperty(ref _isRendering, value);
    }

    public bool IsCurrentPage
    {
        get => _isCurrentPage;
        set => SetProperty(ref _isCurrentPage, value);
    }

    public void SetImage(ImageSource image)
    {
        LastAccessedUtc = DateTime.UtcNow;
        Image = image;
    }

    public void Touch() => LastAccessedUtc = DateTime.UtcNow;

    public void ClearImage() => Image = null;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

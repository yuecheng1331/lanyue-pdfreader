using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using LocalPdfReader.Application.Reader;
using LocalPdfReader.Domain;

namespace LocalPdfReader.App;

public sealed class DocumentPageViewModel : INotifyPropertyChanged
{
    private PdfSize _pdfPageSize;
    private ViewRect _bounds;
    private ImageSource? _image;
    private bool _isCurrentPage;
    private bool _isSelectionPage;
    private bool _isRendering;

    public DocumentPageViewModel(int pageIndex, PdfSize pdfPageSize)
    {
        PageIndex = pageIndex;
        PageNumber = pageIndex + 1;
        _pdfPageSize = pdfPageSize;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int PageIndex { get; }

    public int PageNumber { get; }

    public PdfSize PdfPageSize => _pdfPageSize;

    public ViewRect Bounds => _bounds;

    public ObservableCollection<SelectionRectangleViewModel> SelectionRectangles { get; } = [];

    public double DisplayLeft => _bounds.X;

    public double DisplayTop => _bounds.Y;

    public double DisplayWidth => _bounds.Width;

    public double DisplayHeight => _bounds.Height;

    public ImageSource? Image
    {
        get => _image;
        private set => SetProperty(ref _image, value);
    }

    public bool HasImage => Image is not null;

    public bool IsCurrentPage
    {
        get => _isCurrentPage;
        set => SetProperty(ref _isCurrentPage, value);
    }

    public bool IsSelectionPage
    {
        get => _isSelectionPage;
        set => SetProperty(ref _isSelectionPage, value);
    }

    public bool IsRendering
    {
        get => _isRendering;
        set => SetProperty(ref _isRendering, value);
    }

    public void UpdateLayout(
        PdfSize pdfPageSize,
        PageRotation rotation,
        double zoomFactor,
        double pageLeft,
        double pageTop)
    {
        _pdfPageSize = pdfPageSize;
        _bounds = ReadingViewLayout.CalculatePageBounds(pdfPageSize, rotation, zoomFactor, pageLeft, pageTop);
        OnPropertyChanged(nameof(PdfPageSize));
        OnPropertyChanged(nameof(Bounds));
        OnPropertyChanged(nameof(DisplayLeft));
        OnPropertyChanged(nameof(DisplayTop));
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
    }

    public void SetImage(ImageSource image)
    {
        Image = image;
        OnPropertyChanged(nameof(HasImage));
    }

    public void ClearImage()
    {
        Image = null;
        OnPropertyChanged(nameof(HasImage));
    }

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

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LocalPdfReader.App.Commands;
using LocalPdfReader.Application.PdfWorker;
using LocalPdfReader.Domain;
using LocalPdfReader.PdfProtocol;
using Microsoft.Extensions.Logging;

namespace LocalPdfReader.App;

public sealed class DocumentSearchViewModel : INotifyPropertyChanged
{
    private readonly IPdfWorkerClient _pdfWorkerClient;
    private readonly Func<DocumentId?> _documentIdProvider;
    private readonly ILogger? _logger;
    private readonly AsyncCommand _searchCommand;
    private readonly AsyncCommand _cancelCommand;
    private string _query = string.Empty;
    private bool _matchCase;
    private bool _wholeWord;
    private bool _isSearching;
    private int _pagesSearched;
    private int _totalPages;
    private int _resultCount;
    private string _statusText = "输入关键词后搜索当前 PDF。";
    private SearchResult? _selectedResult;
    private Guid? _activeSearchSessionId;
    private CancellationTokenSource? _searchCancellationSource;
    private Task? _activeSearchTask;

    internal DocumentSearchViewModel(
        IPdfWorkerClient pdfWorkerClient,
        Func<DocumentId?> documentIdProvider,
        ILogger? logger = null)
    {
        _pdfWorkerClient = pdfWorkerClient;
        _documentIdProvider = documentIdProvider;
        _logger = logger;
        _searchCommand = new AsyncCommand(StartSearchAsync, () => CanSearch);
        _cancelCommand = new AsyncCommand(CancelAndWaitAsync, () => IsSearching);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? SelectedResultChanged;

    public ObservableCollection<SearchResultItemViewModel> Results { get; } = [];

    public ICommand SearchCommand => _searchCommand;

    public ICommand CancelCommand => _cancelCommand;

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                OnPropertyChanged(nameof(CanSearch));
                _searchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool MatchCase
    {
        get => _matchCase;
        set => SetProperty(ref _matchCase, value);
    }

    public bool WholeWord
    {
        get => _wholeWord;
        set => SetProperty(ref _wholeWord, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
            {
                OnPropertyChanged(nameof(CanSearch));
                _searchCommand.RaiseCanExecuteChanged();
                _cancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanSearch =>
        _documentIdProvider() is not null &&
        !string.IsNullOrWhiteSpace(Query);

    public int PagesSearched
    {
        get => _pagesSearched;
        private set => SetProperty(ref _pagesSearched, value);
    }

    public int TotalPages
    {
        get => _totalPages;
        private set => SetProperty(ref _totalPages, value);
    }

    public int ResultCount
    {
        get => _resultCount;
        private set => SetProperty(ref _resultCount, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public SearchResult? SelectedResult
    {
        get => _selectedResult;
        internal set
        {
            if (SetProperty(ref _selectedResult, value))
            {
                SelectedResultChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    internal Task StartSearchAsync()
    {
        if (_documentIdProvider() is not { } documentId || string.IsNullOrWhiteSpace(Query))
        {
            return Task.CompletedTask;
        }

        Cancel();
        var searchSessionId = Guid.NewGuid();
        var cancellationSource = new CancellationTokenSource();
        _activeSearchSessionId = searchSessionId;
        _searchCancellationSource = cancellationSource;
        Results.Clear();
        SelectedResult = null;
        ResultCount = 0;
        PagesSearched = 0;
        TotalPages = 0;
        IsSearching = true;
        StatusText = "正在搜索当前 PDF…";

        var task = RunSearchAsync(
            documentId,
            searchSessionId,
            Query.Trim(),
            MatchCase,
            WholeWord,
            cancellationSource);
        _activeSearchTask = task;
        return task;
    }

    internal void Cancel() => _searchCancellationSource?.Cancel();

    internal async Task CancelAndWaitAsync()
    {
        var activeTask = _activeSearchTask;
        Cancel();
        if (activeTask is null)
        {
            return;
        }

        try
        {
            await activeTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task CancelAndResetAsync()
    {
        await CancelAndWaitAsync();
        Results.Clear();
        SelectedResult = null;
        ResultCount = 0;
        PagesSearched = 0;
        TotalPages = 0;
        StatusText = "输入关键词后搜索当前 PDF。";
    }

    private async Task RunSearchAsync(
        DocumentId documentId,
        Guid searchSessionId,
        string query,
        bool matchCase,
        bool wholeWord,
        CancellationTokenSource cancellationSource)
    {
        // Ensure StartSearchAsync stores the task before even an in-memory test stream can complete.
        await Task.Yield();

        try
        {
            await foreach (var update in _pdfWorkerClient.SearchDocumentAsync(
                documentId,
                searchSessionId,
                query,
                matchCase,
                wholeWord,
                SearchProtocolLimits.DefaultBatchSize,
                cancellationSource.Token))
            {
                if (_activeSearchSessionId != searchSessionId)
                {
                    continue;
                }

                ApplyUpdate(update);
            }
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            if (_activeSearchSessionId == searchSessionId)
            {
                StatusText = "搜索已取消。";
            }
        }
        catch (Exception exception)
        {
            if (_activeSearchSessionId == searchSessionId)
            {
                StatusText = "搜索失败，请重试。";
                _logger?.LogWarning(exception, "PDF document search failed.");
            }
        }
        finally
        {
            if (_activeSearchSessionId == searchSessionId)
            {
                _activeSearchSessionId = null;
                _activeSearchTask = null;
                _searchCancellationSource = null;
                IsSearching = false;
            }

            cancellationSource.Dispose();
        }
    }

    private void ApplyUpdate(SearchUpdate update)
    {
        switch (update)
        {
            case SearchStartedUpdate started:
                TotalPages = started.TotalPages;
                StatusText = $"正在搜索 0/{started.TotalPages} 页…";
                break;

            case SearchProgressUpdate progress:
                PagesSearched = progress.PagesSearched;
                TotalPages = progress.TotalPages;
                ResultCount = progress.ResultsFound;
                StatusText = $"正在搜索 {progress.PagesSearched}/{progress.TotalPages} 页，已找到 {progress.ResultsFound} 处。";
                break;

            case SearchResultsUpdate results:
                foreach (var result in results.Results)
                {
                    Results.Add(new SearchResultItemViewModel(result));
                }

                ResultCount = Results.Count;
                break;

            case SearchCompletedUpdate completed:
                ResultCount = completed.TotalResults;
                PagesSearched = TotalPages;
                StatusText = completed.TotalResults == 0
                    ? "搜索完成，没有找到匹配内容。"
                    : $"搜索完成，共找到 {completed.TotalResults} 处。";
                break;

            case SearchCancelledUpdate:
                StatusText = "搜索已取消。";
                break;
        }
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

public sealed class SearchResultItemViewModel(SearchResult result)
{
    public SearchResult Result { get; } = result;

    public int PageNumber => Result.PageIndex + 1;

    public string PageLabel => $"第 {PageNumber} 页";

    public string ContextText => Result.ContextText;
}

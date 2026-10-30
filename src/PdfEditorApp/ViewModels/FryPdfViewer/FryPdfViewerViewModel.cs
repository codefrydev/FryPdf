using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PdfEditorApp.Core.Models;
using PdfEditorApp.Messages;
using PdfEditorApp.Models;
using PdfEditorApp.Services;

namespace PdfEditorApp.ViewModels.FryPdfViewer;

/// <summary>
/// Result item for text searches across pages in a .frypdf document.
/// </summary>
public record FryPdfSearchResult(int PageIndex, int PageNumber, string Snippet, string ElementId);

/// <summary>
/// Master ViewModel for the dedicated .frypdf Interactive Document Viewer & Presentation Reader.
/// Provides a distraction-free, read-only presentation experience with dynamic capabilities
/// impossible in static binary PDFs (scrolling data tables, animated charts, live forms, slide show mode).
/// </summary>
public partial class FryPdfViewerViewModel : ViewModelBase
{
    private readonly IProjectPersistenceService _persistenceService;
    private readonly IPdfExportService _exportService;

    public IStorageProvider? StorageProvider { get; set; }

    [ObservableProperty]
    private string _documentTitle = "Document.frypdf";

    [ObservableProperty]
    private string _filePath = "";

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalPagesCount))]
    [NotifyPropertyChangedFor(nameof(HasPages))]
    [NotifyPropertyChangedFor(nameof(ShowHeaderFooterInView))]
    private FryPdfPageViewerViewModel? _currentPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageNumber))]
    [NotifyPropertyChangedFor(nameof(CanGoNextPage))]
    [NotifyPropertyChangedFor(nameof(CanGoPreviousPage))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    private int _currentPageIndex = 0;

    [ObservableProperty]
    private double _zoomLevel = 1.0;

    [ObservableProperty]
    private bool _isContinuousScroll = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNextPage))]
    [NotifyPropertyChangedFor(nameof(CanGoPreviousPage))]
    [NotifyPropertyChangedFor(nameof(IsSidebarVisibleInView))]
    [NotifyPropertyChangedFor(nameof(ViewportBackgroundBrush))]
    [NotifyPropertyChangedFor(nameof(ViewportPadding))]
    [NotifyPropertyChangedFor(nameof(ViewportScrollVisibility))]
    [NotifyPropertyChangedFor(nameof(PageBorderThickness))]
    [NotifyPropertyChangedFor(nameof(PageCornerRadius))]
    [NotifyPropertyChangedFor(nameof(PageBoxShadow))]
    [NotifyPropertyChangedFor(nameof(ShowHeaderFooterInView))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    private bool _isPresentationMode = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarVisibleInView))]
    private bool _isThumbnailSidebarOpen = true;

    [ObservableProperty]
    private PdfReaderTheme _readingTheme = PdfReaderTheme.Default;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private int _activeSearchResultIndex = 0;

    public ObservableCollection<FryPdfPageViewerViewModel> Pages { get; } = new();
    public ObservableCollection<FryPdfSearchResult> SearchResults { get; } = new();

    public int TotalPagesCount => Pages.Count;
    public bool HasPages => Pages.Count > 0;
    public int CurrentPageNumber => CurrentPageIndex + 1;
    public bool CanGoNextPage => IsPresentationMode ? Pages.Count > 1 : (Pages.Count > 0 && CurrentPageIndex < Pages.Count - 1);
    public bool CanGoPreviousPage => IsPresentationMode ? Pages.Count > 1 : (Pages.Count > 0 && CurrentPageIndex > 0);

    public bool IsSidebarVisibleInView => IsThumbnailSidebarOpen && !IsPresentationMode;
    public string ViewportBackgroundBrush => IsPresentationMode ? "#000000" : "Transparent";
    public Thickness ViewportPadding => IsPresentationMode ? new Thickness(0) : new Thickness(36);
    public ScrollBarVisibility ViewportScrollVisibility => IsPresentationMode ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    public double PageBorderThickness => IsPresentationMode ? 0 : 1;
    public CornerRadius PageCornerRadius => IsPresentationMode ? new CornerRadius(0) : new CornerRadius(4);
    public BoxShadows PageBoxShadow => IsPresentationMode ? new BoxShadows(new BoxShadow()) : BoxShadows.Parse("0 8 32 #18000000");
    public bool ShowHeaderFooterInView => CurrentPage != null && CurrentPage.ShowHeaderFooter && !IsPresentationMode;

    public PdfDocumentModel? CurrentDocumentModel { get; private set; }

    public FryPdfViewerViewModel(
        IProjectPersistenceService? persistenceService = null,
        IPdfExportService? exportService = null)
    {
        _persistenceService = persistenceService ?? new ProjectPersistenceService();
        _exportService = exportService ?? new PdfExportService();
    }

    /// <summary>
    /// Loads a .frypdf document file from disk into the interactive viewer.
    /// </summary>
    public async Task LoadDocumentAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        IsLoading = true;
        FilePath = filePath;
        DocumentTitle = Path.GetFileName(filePath);

        try
        {
            var model = await _persistenceService.LoadProjectAsync(filePath);
            if (model != null)
            {
                LoadFromModel(model, filePath);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Loads an in-memory PdfDocumentModel into the viewer.
    /// </summary>
    public void LoadFromModel(PdfDocumentModel model, string? filePath = null)
    {
        CurrentDocumentModel = model;
        FilePath = filePath ?? string.Empty;
        DocumentTitle = !string.IsNullOrWhiteSpace(filePath)
            ? Path.GetFileName(filePath)
            : (!string.IsNullOrWhiteSpace(model.Title)
                ? (model.Title.EndsWith(".frypdf", StringComparison.OrdinalIgnoreCase)
                    ? model.Title
                    : $"{Path.GetFileNameWithoutExtension(model.Title)}.frypdf")
                : "Document.frypdf");

        Pages.Clear();
        foreach (var pageModel in model.Pages)
        {
            var pageVm = FryPdfPageViewerViewModel.FromPageModel(pageModel);
            Pages.Add(pageVm);
        }

        CurrentPageIndex = 0;
        CurrentPage = Pages.FirstOrDefault();

        // Trigger chart entry animations on first page
        CurrentPage?.ReplayChartAnimations();

        OnPropertyChanged(nameof(TotalPagesCount));
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(CurrentPageNumber));
        OnPropertyChanged(nameof(CanGoNextPage));
        OnPropertyChanged(nameof(CanGoPreviousPage));
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        if (value >= 0 && value < Pages.Count)
        {
            CurrentPage = Pages[value];
            CurrentPage.ReplayChartAnimations();
        }
    }

    // --- PAGE NAVIGATION COMMANDS ---

    [RelayCommand(CanExecute = nameof(CanGoNextPage))]
    public void NextPage()
    {
        if (Pages.Count <= 1) return;

        if (CurrentPageIndex < Pages.Count - 1)
        {
            CurrentPageIndex++;
        }
        else if (IsPresentationMode)
        {
            // Smooth wrap-around in presentation mode
            CurrentPageIndex = 0;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoPreviousPage))]
    public void PreviousPage()
    {
        if (Pages.Count <= 1) return;

        if (CurrentPageIndex > 0)
        {
            CurrentPageIndex--;
        }
        else if (IsPresentationMode)
        {
            // Smooth wrap-around in presentation mode
            CurrentPageIndex = Pages.Count - 1;
        }
    }

    [RelayCommand]
    public void FirstPage()
    {
        if (Pages.Count > 0)
        {
            CurrentPageIndex = 0;
        }
    }

    [RelayCommand]
    public void LastPage()
    {
        if (Pages.Count > 0)
        {
            CurrentPageIndex = Pages.Count - 1;
        }
    }

    [RelayCommand]
    public void GoToPage(int pageNumber)
    {
        int targetIdx = pageNumber - 1;
        if (targetIdx >= 0 && targetIdx < Pages.Count)
        {
            CurrentPageIndex = targetIdx;
        }
    }

    // --- ZOOM COMMANDS ---

    [RelayCommand]
    public void ZoomIn()
    {
        ZoomLevel = Math.Min(5.0, Math.Round(ZoomLevel + 0.15, 2));
    }

    [RelayCommand]
    public void ZoomOut()
    {
        ZoomLevel = Math.Max(0.25, Math.Round(ZoomLevel - 0.15, 2));
    }

    [RelayCommand]
    public void ResetZoom()
    {
        ZoomLevel = 1.0;
    }

    [RelayCommand]
    public void FitToWidth()
    {
        ZoomLevel = 1.25;
    }

    public void FitToViewport(double viewportWidth, double viewportHeight)
    {
        if (CurrentPage == null || CurrentPage.Width <= 0 || CurrentPage.Height <= 0)
            return;

        double availWidth = Math.Max(100, viewportWidth);
        double availHeight = Math.Max(100, viewportHeight);

        double scaleX = availWidth / CurrentPage.Width;
        double scaleY = availHeight / CurrentPage.Height;
        double fitScale = Math.Min(scaleX, scaleY);

        if (fitScale > 0.1 && fitScale < 10.0)
        {
            ZoomLevel = Math.Round(fitScale, 3);
        }
    }

    [RelayCommand]
    public void FitToPage()
    {
        ZoomLevel = 0.85;
    }

    // --- INTERACTIVE FEATURES COMMANDS ---

    [RelayCommand]
    public void TogglePresentationMode()
    {
        IsPresentationMode = !IsPresentationMode;
    }

    [RelayCommand]
    public void ToggleThumbnailSidebar()
    {
        IsThumbnailSidebarOpen = !IsThumbnailSidebarOpen;
    }

    [RelayCommand]
    public void ReplayAnimations()
    {
        CurrentPage?.ReplayChartAnimations();
    }

    [RelayCommand]
    public void SetReadingTheme(PdfReaderTheme theme)
    {
        ReadingTheme = theme;
    }

    // --- SEARCH COMMANDS ---

    partial void OnSearchQueryChanged(string value)
    {
        PerformSearch();
    }

    private void PerformSearch()
    {
        SearchResults.Clear();
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            ActiveSearchResultIndex = 0;
            return;
        }

        string q = SearchQuery.Trim();
        for (int p = 0; p < Pages.Count; p++)
        {
            var page = Pages[p];
            foreach (var el in page.Elements)
            {
                if (el is ElementViewModels.TextElementViewModel textEl &&
                    !string.IsNullOrEmpty(textEl.Text) &&
                    textEl.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    string snippet = textEl.Text.Length > 60 ? textEl.Text.Substring(0, 57) + "..." : textEl.Text;
                    SearchResults.Add(new FryPdfSearchResult(p, p + 1, snippet, textEl.Id));
                }
            }

            foreach (var table in page.InteractiveTables)
            {
                if (table.AllRows.Any(r => r.MatchesFilter(q)))
                {
                    SearchResults.Add(new FryPdfSearchResult(p, p + 1, $"Table match on page {p + 1}", string.Empty));
                }
            }
        }

        if (SearchResults.Count > 0)
        {
            ActiveSearchResultIndex = 0;
            GoToSearchResult(0);
        }
    }

    [RelayCommand]
    public void NextSearchResult()
    {
        if (SearchResults.Count == 0) return;
        ActiveSearchResultIndex = (ActiveSearchResultIndex + 1) % SearchResults.Count;
        GoToSearchResult(ActiveSearchResultIndex);
    }

    [RelayCommand]
    public void PreviousSearchResult()
    {
        if (SearchResults.Count == 0) return;
        ActiveSearchResultIndex = (ActiveSearchResultIndex - 1 + SearchResults.Count) % SearchResults.Count;
        GoToSearchResult(ActiveSearchResultIndex);
    }

    private void GoToSearchResult(int index)
    {
        if (index >= 0 && index < SearchResults.Count)
        {
            CurrentPageIndex = SearchResults[index].PageIndex;
        }
    }

    // --- APPLICATION WORKFLOW COMMANDS ---

    /// <summary>
    /// Closes the interactive viewer and returns to the workspace shell.
    /// </summary>
    [RelayCommand]
    public void CloseViewer()
    {
        WeakReferenceMessenger.Default.Send(new CloseViewerMessage());
    }

    /// <summary>
    /// Alias for CloseViewer for backward compatibility.
    /// </summary>
    [RelayCommand]
    public void BackToHome() => CloseViewer();

    /// <summary>
    /// Switches from read-only Interactive Viewer into full Studio Editor for the active document.
    /// </summary>
    [RelayCommand]
    public void OpenInEditor()
    {
        if (!string.IsNullOrWhiteSpace(FilePath))
        {
            WeakReferenceMessenger.Default.Send(new OpenInEditorMessage(FilePath));
        }
    }

    /// <summary>
    /// Synchronizes live user-filled form values and modifications from all viewer pages back into <see cref="CurrentDocumentModel"/>.
    /// </summary>
    public void SyncCurrentDocumentModel()
    {
        if (CurrentDocumentModel == null) return;
        CurrentDocumentModel.Pages.Clear();
        foreach (var pageVm in Pages)
        {
            CurrentDocumentModel.Pages.Add(pageVm.ToPageModel());
        }
    }

    /// <summary>
    /// Exports the current document to standard binary PDF via QuestPDF.
    /// </summary>
    [RelayCommand]
    public async Task ExportPdfAsync()
    {
        if (CurrentDocumentModel == null) return;
        SyncCurrentDocumentModel();

        try
        {
            if (StorageProvider != null)
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export .frypdf to Vector PDF",
                    SuggestedFileName = Path.ChangeExtension(DocumentTitle, ".pdf"),
                    DefaultExtension = "pdf",
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("PDF Document (*.pdf)") { Patterns = new[] { "*.pdf" } }
                    }
                });

                if (file != null)
                {
                    string targetPath = file.Path.LocalPath;
                    await _exportService.ExportToFileAsync(CurrentDocumentModel, targetPath);
                    WeakReferenceMessenger.Default.Send(new ShowToastMessage($"Exported PDF: {Path.GetFileName(targetPath)}"));
                }
            }
        }
        catch (Exception ex)
        {
            WeakReferenceMessenger.Default.Send(new ShowToastMessage($"Export error: {ex.Message}", ToastNotificationType.Danger));
        }
    }
}

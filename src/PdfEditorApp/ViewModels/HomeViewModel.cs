using PdfEditorApp.Services.Tools.Core;
using PdfEditorApp.ViewModels.Tools.Core;
using PdfEditorApp.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PdfEditorApp.Core.Models;
using PdfEditorApp.Messages;
using PdfEditorApp.Models;
using PdfEditorApp.Services;
using PdfEditorApp.Services.Tools;
using PdfEditorApp.Templates;
using PdfEditorApp.Templates.General;
using PdfEditorApp.ViewModels.Tools;

namespace PdfEditorApp.ViewModels;

/// <summary>
/// ViewModel for the Google Docs, Canva, and Adobe Acrobat inspired Home / Tools Dashboard.
/// Provides a comprehensive PDF Tools Studio (all 32 tools), expandable template gallery, and recent document management.
/// </summary>
public partial class HomeViewModel : ViewModelBase, IServiceProvider, IDisposable
{
    private readonly IRecentDocumentsService _recentService;
    private readonly ITemplateService _templateService;
    private readonly IProjectPersistenceService _persistenceService;
    private readonly IPdfToolRegistry _toolRegistry = null!;
    private readonly IPdfToolViewModelFactory? _toolViewModelFactory;
    private readonly IThemeService? _themeService;
    private readonly IUiSettingsService? _uiSettingsService;
    private readonly PdfEditorApp.Core.Plugins.Descriptors.INavigationRegistry? _navigationRegistry;
    private readonly IServiceProvider? _serviceProvider;

    public ObservableCollection<NavigationItemViewModel> DynamicNavigationItems { get; } = new();

    /// <summary>
    /// Fully plugin-driven, grouped navigation items rendered in the sidebar.
    /// Populated from <see cref="INavigationRegistry"/> by <see cref="RefreshDynamicNavigationItems"/>.
    /// </summary>
    public ObservableCollection<NavGroupViewModel> NavGroups { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DynamicFullViewportPageView))]
    [NotifyPropertyChangedFor(nameof(DynamicScrollablePageView))]
    private object? _dynamicPageView;

    private readonly Dictionary<string, object?> _dynamicViewCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when navigation is fully driven by the plugin registry (NavGroups is populated).</summary>
    public bool IsPluginDrivenNavigation => NavGroups.Count > 0;

    // --- Events to tell the shell what to do ---

    public event Action<string?>? OpenTemplateRequested;   // templateName (null = blank)
    public event Action? OpenFileRequested;
    public event Action? OpenWorkflowBuilderRequested;
    public event Action? OpenBatchGenerationRequested;
    public event Action<string, string>? DocumentRenamed;  // oldPath, newPath
    public event Action<string>? DocumentDeleted;          // deletedPath

    // --- Observable State ---

    // Rename Dialog State
    [ObservableProperty]
    private bool _isRenameDialogOpen;

    [ObservableProperty]
    private string _renameTargetFilePath = "";

    [ObservableProperty]
    private string _renameNewName = "";

    [ObservableProperty]
    private string _renameExtension = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenameError))]
    private string _renameErrorMessage = "";

    public bool HasRenameError => !string.IsNullOrWhiteSpace(RenameErrorMessage);

    // Delete Confirmation Dialog State
    [ObservableProperty]
    private bool _isDeleteConfirmDialogOpen;

    [ObservableProperty]
    private string _deleteTargetFilePath = "";

    [ObservableProperty]
    private string _deleteTargetFileName = "";

    [ObservableProperty]
    private bool _isDarkMode;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private string _selectedToolCategory = "All";

    [ObservableProperty]
    private string _selectedTemplateCategory = "All";

    [ObservableProperty]
    private string _selectedLicenseCategory = "All";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSection))]
    [NotifyPropertyChangedFor(nameof(IsNewDocumentSection))]
    [NotifyPropertyChangedFor(nameof(IsPdfReaderSection))]
    [NotifyPropertyChangedFor(nameof(IsToolsSection))]
    [NotifyPropertyChangedFor(nameof(IsStarredSection))]
    [NotifyPropertyChangedFor(nameof(IsTrashSection))]
    [NotifyPropertyChangedFor(nameof(IsLicensingSection))]
    [NotifyPropertyChangedFor(nameof(IsFontPackagesSection))]
    [NotifyPropertyChangedFor(nameof(IsTesseractDataSection))]
    [NotifyPropertyChangedFor(nameof(IsHelpSection))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSection))]
    [NotifyPropertyChangedFor(nameof(IsPluginsSection))]
    [NotifyPropertyChangedFor(nameof(IsNavigationSidebarVisible))]
    [NotifyPropertyChangedFor(nameof(TopSearchPlaceholder))]
    private HomeNavSection _selectedNavSection = HomeNavSection.Home;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPluginsWorkspaceActive))]
    [NotifyPropertyChangedFor(nameof(IsFullViewportActive))]
    [NotifyPropertyChangedFor(nameof(IsStandardScrollableContentActive))]
    [NotifyPropertyChangedFor(nameof(IsTopSearchBarVisible))]
    [NotifyPropertyChangedFor(nameof(IsSidebarCompactRail))]
    [NotifyPropertyChangedFor(nameof(IsNavigationSidebarVisible))]
    [NotifyPropertyChangedFor(nameof(DynamicFullViewportPageView))]
    [NotifyPropertyChangedFor(nameof(DynamicScrollablePageView))]
    private bool _isToolPageActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPluginsWorkspaceActive))]
    [NotifyPropertyChangedFor(nameof(IsFullViewportActive))]
    [NotifyPropertyChangedFor(nameof(IsStandardScrollableContentActive))]
    [NotifyPropertyChangedFor(nameof(IsTopSearchBarVisible))]
    [NotifyPropertyChangedFor(nameof(IsSidebarCompactRail))]
    [NotifyPropertyChangedFor(nameof(IsNavigationSidebarVisible))]
    [NotifyPropertyChangedFor(nameof(DynamicFullViewportPageView))]
    [NotifyPropertyChangedFor(nameof(DynamicScrollablePageView))]
    private PdfEditorApp.Core.Plugins.Descriptors.NavigationItemDescriptor? _activeNavDescriptor;

    [ObservableProperty]
    private PdfToolCardViewModel? _activeToolCard;

    [ObservableProperty]
    private PdfToolViewModelBase? _activeToolViewModel;

    [ObservableProperty]
    private bool _isTemplateGalleryExpanded;

    public ObservableCollection<PdfToolCardViewModel> AllTools { get; } = new();
    public ObservableCollection<PdfToolCardViewModel> QuickTools { get; } = new();
    public ObservableCollection<PdfToolCardViewModel> FilteredTools { get; } = new();
    public ObservableCollection<PdfToolCardViewModel> StarredTools { get; } = new();
    public ObservableCollection<TemplateCardViewModel> AllTemplates { get; } = new();
    public ObservableCollection<TemplateCardViewModel> FilteredTemplates { get; } = new();
    public ObservableCollection<TemplateCardViewModel> FeaturedTemplates { get; } = new();
    public ObservableCollection<RecentDocumentItem> RecentDocuments { get; } = new();
    public ObservableCollection<ThirdPartyToolLicense> AllLicenses { get; } = new();
    public ObservableCollection<ThirdPartyToolLicense> FilteredLicenses { get; } = new();

    public PdfToolCardViewModel? WorkflowBannerCard { get; private set; }

    public PdfToolRunnerViewModel? ToolRunner { get; }

    public bool IsHomeSection => SelectedNavSection == HomeNavSection.Home;
    public bool IsNewDocumentSection => SelectedNavSection == HomeNavSection.NewDocument;
    public bool IsPdfReaderSection => SelectedNavSection == HomeNavSection.PdfReader;
    public bool IsToolsSection => SelectedNavSection is >= HomeNavSection.AllTools and <= HomeNavSection.AiAndAutomation;
    public bool IsStarredSection => SelectedNavSection == HomeNavSection.Starred;
    public bool IsTrashSection => SelectedNavSection == HomeNavSection.Trash;
    public bool IsLicensingSection => SelectedNavSection == HomeNavSection.Licensing;
    public bool IsFontPackagesSection => SelectedNavSection == HomeNavSection.FontPackages;
    public bool IsTesseractDataSection => SelectedNavSection == HomeNavSection.TesseractData;
    public bool IsHelpSection => SelectedNavSection == HomeNavSection.Help;
    public bool IsSettingsSection => SelectedNavSection == HomeNavSection.Settings;
    public bool IsPluginsSection => SelectedNavSection == HomeNavSection.Plugins;
    public bool IsPluginsWorkspaceActive => SelectedNavSection == HomeNavSection.Plugins && !IsToolPageActive && (ActiveNavDescriptor == null || string.Equals(ActiveNavDescriptor.Id, "Plugins", StringComparison.OrdinalIgnoreCase));
    public bool IsFullViewportActive => !IsToolPageActive && (ActiveNavDescriptor?.DisplayMode is PdfEditorApp.Core.Plugins.Descriptors.NavigationDisplayMode.FullViewport or PdfEditorApp.Core.Plugins.Descriptors.NavigationDisplayMode.ImmersiveStudio || IsPluginsWorkspaceActive);
    public bool IsStandardScrollableContentActive => !IsToolPageActive && !IsFullViewportActive;
    public bool IsTopSearchBarVisible => !IsToolPageActive && ActiveNavDescriptor != null && !ActiveNavDescriptor.HideTopSearchBar;

    public string TopSearchPlaceholder => string.Empty;

    public string AppVersion
    {
        get
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var infoVer = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(infoVer))
                {
                    return infoVer.Split('+')[0];
                }

                var ver = assembly.GetName().Version;
                if (ver != null)
                {
                    return $"{ver.Major}.{ver.Minor}.{ver.Build}";
                }
            }
            catch
            {
                // fallback
            }

            return "0.2.0";
        }
    }

    public string AppVersionShort => $"v{AppVersion}";

    public string AppVersionDisplay => $"FryPDF {AppVersionShort} · Privacy-first PDF Studio";

    public bool IsSidebarCompactRail => !IsToolPageActive && ActiveNavDescriptor?.DisplayMode == PdfEditorApp.Core.Plugins.Descriptors.NavigationDisplayMode.ImmersiveStudio;

    /// <summary>
    /// Controls visibility of the main navigation sidebar rail.
    /// Hidden when a tool is open or when an edge-to-edge full-viewport studio (like Image Studio) is active,
    /// giving the studio 100% of the window width without the outer sidebar squeezing it.
    /// </summary>
    public bool IsNavigationSidebarVisible =>
        !IsToolPageActive &&
        (ActiveNavDescriptor == null ||
         ActiveNavDescriptor.DisplayMode != PdfEditorApp.Core.Plugins.Descriptors.NavigationDisplayMode.FullViewport ||
         SelectedNavSection == HomeNavSection.Plugins);

    public object? DynamicFullViewportPageView => IsFullViewportActive && !IsPluginsWorkspaceActive ? DynamicPageView : null;
    public object? DynamicScrollablePageView => IsStandardScrollableContentActive ? DynamicPageView : null;

    public FontManagerViewModel FontManager { get; } = new();
    public TesseractManagerViewModel TesseractManager { get; } = new();
    public HelpGuideViewModel HelpGuide { get; } = new();
    public SettingsViewModel Settings { get; }
    public PluginsManagerViewModel PluginsManager { get; }

    public void TriggerToast(string msg, ToastNotificationType type = ToastNotificationType.Primary, string? icon = null)
    {
        WeakReferenceMessenger.Default.Send(new ShowToastMessage(msg, type, icon));
    }

    public int MatchingToolsCount => FilteredTools.Count;
    public bool HasNoMatchingTools => FilteredTools.Count == 0;
    public int MatchingTemplatesCount => FilteredTemplates.Count;
    public bool HasNoMatchingTemplates => FilteredTemplates.Count == 0;
    public int MatchingLicensesCount => FilteredLicenses.Count;
    public bool HasNoMatchingLicenses => FilteredLicenses.Count == 0;
    public bool HasRecentDocuments => RecentDocuments.Count > 0;
    public bool HasStarredTools => StarredTools.Count > 0;

    // --- Constructor ---

    public HomeViewModel() : this(new RecentDocumentsService(), new TemplateService(), new ProjectPersistenceService(), new PdfToolRegistry(), null, null, new ThemeService(), new UiSettingsService(), null, null) { }

    public HomeViewModel(
        IRecentDocumentsService recentService,
        ITemplateService templateService,
        IProjectPersistenceService persistenceService,
        IPdfToolRegistry toolRegistry,
        PdfToolRunnerViewModel? toolRunner = null,
        IPdfToolViewModelFactory? toolViewModelFactory = null,
        IThemeService? themeService = null,
        IUiSettingsService? uiSettingsService = null,
        PdfEditorApp.Core.Plugins.Descriptors.INavigationRegistry? navigationRegistry = null,
        IServiceProvider? serviceProvider = null)
    {
        _recentService = recentService;
        _templateService = templateService;
        _persistenceService = persistenceService;
        _toolRegistry = toolRegistry;
        _toolViewModelFactory = toolViewModelFactory;
        _themeService = themeService;
        _uiSettingsService = uiSettingsService;
        _navigationRegistry = navigationRegistry;
        _serviceProvider = serviceProvider;
        ToolRunner = toolRunner;

        var shortcutRegistry = (serviceProvider?.GetService(typeof(PdfEditorApp.Core.Plugins.Descriptors.IShortcutRegistry)) as PdfEditorApp.Core.Plugins.Descriptors.IShortcutRegistry)
            ?? (App.Services?.GetService(typeof(PdfEditorApp.Core.Plugins.Descriptors.IShortcutRegistry)) as PdfEditorApp.Core.Plugins.Descriptors.IShortcutRegistry);
        Settings = new SettingsViewModel(_uiSettingsService ?? new UiSettingsService(), _themeService, shortcutRegistry);
        PluginsManager = (serviceProvider?.GetService(typeof(PluginsManagerViewModel)) as PluginsManagerViewModel)
            ?? new PluginsManagerViewModel(
                serviceProvider?.GetService(typeof(PluginHost)) as PluginHost,
                serviceProvider?.GetService(typeof(PdfEditorApp.Core.Plugins.Marketplace.IPluginMarketplaceService)) as PdfEditorApp.Core.Plugins.Marketplace.IPluginMarketplaceService,
                _toolRegistry);
        PluginsManager.ShowToastCallback = msg => TriggerToast(msg);

        if (_themeService != null)
        {
            IsDarkMode = _themeService.IsDarkMode;
            _themeService.ThemeChanged += OnThemeServiceThemeChanged;
        }

        if (ToolRunner != null)
        {
            ToolRunner.BackRequested += BackToTools;
        }

        HelpGuide.ToolLaunchRequested += OnHelpGuideToolLaunchRequested;

        if (_navigationRegistry != null)
        {
            _navigationRegistry.RegistryChanged += RefreshDynamicNavigationItems;
            RefreshDynamicNavigationItems();
            ActiveNavDescriptor = _navigationRegistry.GetItem(SelectedNavSection.ToString());
        }

        if (_toolRegistry != null)
        {
            _toolRegistry.RegistryChanged += InitializeTools;
        }

        InitializeTools();
        InitializeTemplates();
        InitializeLicenses();
        RefreshRecent();
    }


    [RelayCommand]
    public void ToggleTheme()
    {
        if (_themeService != null)
        {
            _themeService.ToggleTheme();
            IsDarkMode = _themeService.IsDarkMode;
        }
        else
        {
            IsDarkMode = !IsDarkMode;
        }
    }

    // --- Tools Initialization ---

    private void InitializeTools()
    {
        AllTools.Clear();
        var toolDefs = _toolRegistry.GetAllTools();

        foreach (var def in toolDefs)
        {
            var cardVm = new PdfToolCardViewModel(def);
            cardVm.ToolSelected += (id) =>
            {
                if (id == PdfToolId.WorkflowBuilder)
                {
                    WeakReferenceMessenger.Default.Send(new OpenWorkflowStudioMessage());
                    OpenWorkflowBuilderRequested?.Invoke();
                }
                else
                {
                    OpenToolPage(id);
                }
            };
            cardVm.StarToggled += (id) =>
            {
                RefreshStarredTools();
            };

            if (def.IsWorkflowBanner)
            {
                WorkflowBannerCard = cardVm;
            }
            else
            {
                AllTools.Add(cardVm);
            }
        }

        UpdateFilteredTools();

        QuickTools.Clear();
        var quickIds = new[] { PdfToolId.MergePdf, PdfToolId.CompressPdf, PdfToolId.PdfToWord, PdfToolId.SignPdf, PdfToolId.OcrPdf, PdfToolId.AiSummarizer };
        foreach (var qid in quickIds)
        {
            var match = AllTools.FirstOrDefault(t => t.Id == qid);
            if (match != null) QuickTools.Add(match);
        }
    }

    // --- Template Initialization ---

    private void InitializeTemplates()
    {
        AllTemplates.Clear();

        // 1. Blank Canvas
        var blankDoc = _templateService.CreateBlankDocument();
        var blankPageVm = new PageViewModel();
        blankPageVm.LoadFromModel(blankDoc.Pages[0]);
        AllTemplates.Add(new TemplateCardViewModel
        {
            Id = "",
            Name = "Blank Canvas",
            Category = "General",
            Subtitle = "Clean Canvas",
            Description = "Start fresh with a clean customizable canvas",
            Badge = "Blank",
            AccentColorHex = "#0F6CBD",
            IconKind = "FilePlusOutline",
            PagePreview = blankPageVm,
            IsBlank = true,
            IsFeatured = true,
            IsLandscape = false
        });

        // 2. Templates from ITemplateService
        var templateDefs = _templateService.GetAllTemplates();
        foreach (var def in templateDefs)
        {
            if (def is BlankDocumentTemplate) continue;

            try
            {
                var doc = def.Create();
                var page = doc.Pages.FirstOrDefault() ?? new PdfPageModel();
                var pageVm = new PageViewModel();
                pageVm.LoadFromModel(page);

                bool isFeatured = def.Id is "interactive_executive_deck" or "annualreport" or "invoice" or "resume" or "resumemodern" or "academic" or "mathresearch" or "physicsresearch" or "certificate" or "typographyshowcase"
                    or "bodmas_worksheet" or "bilingual_exam_paper" or "states_of_matter_notes" or "arduino_cheatsheet" or "factorization_worksheet";
                string subtitle = def.Id switch
                {
                    "interactive_executive_deck" => "Interactive Landscape Deck",
                    "annualreport" => "Executive Summary & Charts",
                    "invoice" => "Itemized Billing & Terms",
                    "resume" => "Executive CV with Live QR",
                    "resumemodern" => "Two-Column Tech & Product",
                    "resumecreative" => "Creative Director & Portfolio",
                    "resumeacademic" => "Faculty Dossier & Grants",
                    "academic" => "2-Column Systems Research",
                    "mathresearch" => "Discrete Hodge Theory",
                    "physicsresearch" => "Cavity QED & Quantum Spin",
                    "historyresearch" => "Mediterranean Trade Ledgers",
                    "financeresearch" => "Jump-Diffusion Econometrics",
                    "certificate" => "Crimson & Gold Award",
                    "certificatenavygold" => "Navy & Gold Crest",
                    "diploma" => "Collegiate Degree",
                    "weddingtraditional" => "Marigold & Ganesha Crest",
                    "weddingroyalfloral" => "Botanical Laurel Wreath",
                    "galainvitation" => "Black-Tie Art Deco",
                    "typographyshowcase" => "Bézier Waves & Ink Specimen",
                    "bodmas_worksheet" => "BODMAS & Student Header",
                    "factorization_worksheet" => "150 Questions & Identities",
                    "bilingual_exam_paper" => "Hindi & English Exam Paper",
                    "states_of_matter_notes" => "Phase Cycle & Particle Models",
                    "quadrilaterals_guide" => "Geometric Properties & Formulas",
                    "arduino_cheatsheet" => "Embedded C & Pinout Map",
                    _ => def.Category
                };

                string badge = def.Id switch
                {
                    "interactive_executive_deck" => "Live Deck",
                    "annualreport" => "Popular",
                    "invoice" => "Finance",
                    "resume" => "Executive",
                    "resumemodern" => "Tech",
                    "resumecreative" => "Design",
                    "resumeacademic" => "Academic",
                    "academic" => "Systems",
                    "mathresearch" => "Pure Math",
                    "physicsresearch" => "Quantum",
                    "historyresearch" => "History",
                    "financeresearch" => "Quant",
                    "certificate" => "Award",
                    "certificatenavygold" => "Executive",
                    "diploma" => "Degree",
                    "weddingtraditional" => "Festive",
                    "weddingroyalfloral" => "Luxury",
                    "galainvitation" => "Art Deco",
                    "typographyshowcase" => "Featured",
                    "bodmas_worksheet" => "Worksheet",
                    "factorization_worksheet" => "Algebra",
                    "bilingual_exam_paper" => "Bilingual",
                    "states_of_matter_notes" => "Science",
                    "quadrilaterals_guide" => "Geometry",
                    "arduino_cheatsheet" => "Cheat Sheet",
                    _ => def.Category
                };

                AllTemplates.Add(new TemplateCardViewModel
                {
                    Id = def.Id,
                    Name = def.Name,
                    Category = def.Category,
                    Subtitle = subtitle,
                    Description = def.Description,
                    Badge = badge,
                    AccentColorHex = def.AccentColorHex,
                    IconKind = def.IconKind,
                    PagePreview = pageVm,
                    IsBlank = false,
                    IsFeatured = isFeatured,
                    IsLandscape = page.Orientation == PageOrientation.Landscape || page.Width > page.Height
                });
            }
            catch
            {
                // Fallback for safe startup
            }
        }

        UpdateFilteredTemplates();
    }

    // --- Search & Filter Synchronization ---

    partial void OnSearchQueryChanged(string value)
    {
        UpdateFilteredTools();
        UpdateFilteredTemplates();
        UpdateFilteredLicenses();
    }

    partial void OnSelectedToolCategoryChanged(string value) => UpdateFilteredTools();
    partial void OnSelectedTemplateCategoryChanged(string value) => UpdateFilteredTemplates();
    partial void OnSelectedLicenseCategoryChanged(string value) => UpdateFilteredLicenses();

    partial void OnSelectedNavSectionChanged(HomeNavSection value)
    {
        var sectionName = value.ToString();
        foreach (var item in DynamicNavigationItems)
        {
            item.IsActive = string.Equals(item.Id, sectionName, StringComparison.OrdinalIgnoreCase);
        }

        if (_navigationRegistry != null)
        {
            ActiveNavDescriptor = _navigationRegistry.GetItem(sectionName);
        }

        if (ActiveNavDescriptor?.ViewFactory == null)
        {
            DynamicPageView = null;
        }

        OnPropertyChanged(nameof(IsHomeSection));
        OnPropertyChanged(nameof(IsNewDocumentSection));
        OnPropertyChanged(nameof(IsPdfReaderSection));
        OnPropertyChanged(nameof(IsToolsSection));
        OnPropertyChanged(nameof(IsStarredSection));
        OnPropertyChanged(nameof(IsTrashSection));
        OnPropertyChanged(nameof(IsLicensingSection));
        OnPropertyChanged(nameof(IsFontPackagesSection));
        OnPropertyChanged(nameof(IsTesseractDataSection));
        OnPropertyChanged(nameof(IsHelpSection));
        OnPropertyChanged(nameof(IsSettingsSection));
        OnPropertyChanged(nameof(IsPluginsSection));
        OnPropertyChanged(nameof(IsPluginsWorkspaceActive));
        OnPropertyChanged(nameof(IsFullViewportActive));
        OnPropertyChanged(nameof(IsStandardScrollableContentActive));
        OnPropertyChanged(nameof(IsTopSearchBarVisible));
        OnPropertyChanged(nameof(IsSidebarCompactRail));
        OnPropertyChanged(nameof(IsNavigationSidebarVisible));
        OnPropertyChanged(nameof(DynamicFullViewportPageView));
        OnPropertyChanged(nameof(DynamicScrollablePageView));

        if (value == HomeNavSection.TesseractData)
        {
            _ = TesseractManager.RefreshStatsAsync();
        }
    }

    [RelayCommand]
    public void OpenHelpGuide(string? topicId = null)
    {
        SelectNavSection("Help");
        if (!string.IsNullOrWhiteSpace(topicId))
        {
            HelpGuide.OpenTopicById(topicId);
        }
        else
        {
            HelpGuide.BackToGrid();
        }
    }

    [RelayCommand]
    public void OpenHelpForTool(PdfToolId toolId)
    {
        SelectNavSection("Help");
        HelpGuide.OpenGuideForTool(toolId);
    }

    private void UpdateFilteredTools()
    {
        FilteredTools.Clear();
        foreach (var tool in AllTools)
        {
            if (MatchesToolCategory(tool) && MatchesToolSearch(tool))
            {
                FilteredTools.Add(tool);
            }
        }

        OnPropertyChanged(nameof(MatchingToolsCount));
        OnPropertyChanged(nameof(HasNoMatchingTools));
    }

    private bool MatchesToolCategory(PdfToolCardViewModel card)
    {
        if (SelectedToolCategory == "All") return true;

        return SelectedToolCategory switch
        {
            "Organize & Page" => card.Category == PdfToolCategory.OrganizeAndPage,
            "Optimize & Security" => card.Category == PdfToolCategory.OptimizeAndSecurity,
            "Convert from PDF" => card.Category == PdfToolCategory.ConvertFromPdf,
            "Convert to PDF" => card.Category == PdfToolCategory.ConvertToPdf,
            "Edit & Forms" => card.Category == PdfToolCategory.EditAndForms,
            "AI & Automation" => card.Category == PdfToolCategory.AiAndAutomation,
            _ => true
        };
    }

    private bool MatchesToolSearch(PdfToolCardViewModel card)
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return true;
        var q = SearchQuery.Trim();
        return card.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || card.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
            || card.CategoryDisplayName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateFilteredTemplates()
    {
        FilteredTemplates.Clear();
        FeaturedTemplates.Clear();

        foreach (var t in AllTemplates)
        {
            if (MatchesTemplateFilter(t))
            {
                FilteredTemplates.Add(t);
                if (t.IsFeatured)
                {
                    FeaturedTemplates.Add(t);
                }
            }
        }

        if (FeaturedTemplates.Count == 0 && FilteredTemplates.Count > 0)
        {
            foreach (var t in FilteredTemplates)
            {
                FeaturedTemplates.Add(t);
            }
        }

        OnPropertyChanged(nameof(MatchingTemplatesCount));
        OnPropertyChanged(nameof(HasNoMatchingTemplates));
    }

    private bool MatchesTemplateFilter(TemplateCardViewModel card)
    {
        if (SelectedTemplateCategory != "All" &&
            !string.Equals(SelectedTemplateCategory, card.Category, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(SearchQuery)) return true;

        var q = SearchQuery.Trim();
        return card.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || card.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
            || card.Category.Contains(q, StringComparison.OrdinalIgnoreCase)
            || card.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    // --- Commands ---

    [RelayCommand]
    public void SetToolCategory(string? category)
    {
        SelectedToolCategory = string.IsNullOrWhiteSpace(category) ? "All" : category;
    }

    [RelayCommand]
    public void SetTemplateCategory(string? category)
    {
        SelectedTemplateCategory = string.IsNullOrWhiteSpace(category) ? "All" : category;
    }

    [RelayCommand]
    public void ClearSearch()
    {
        SearchQuery = "";
    }

    [RelayCommand]
    public void ToggleTemplateGallery()
    {
        IsTemplateGalleryExpanded = !IsTemplateGalleryExpanded;
    }

    [RelayCommand]
    public void CollapseTemplateGallery()
    {
        IsTemplateGalleryExpanded = false;
    }

    [RelayCommand]
    public void ClearTemplateSearch()
    {
        SearchQuery = "";
        SelectedTemplateCategory = "All";
        UpdateFilteredTools();
        UpdateFilteredTemplates();
    }

    // Kept for backward-compatibility with unit tests that assert on ContributedNavigationItems
    public ObservableCollection<NavigationItemViewModel> ContributedNavigationItems { get; } = new();

    public void RefreshDynamicNavigationItems()
    {
        if (_navigationRegistry == null) return;

        DynamicNavigationItems.Clear();
        ContributedNavigationItems.Clear();
        NavGroups.Clear();

        var allItems = _navigationRegistry.GetAllItems();
        var currentSectionId = SelectedNavSection.ToString();

        // Flat list (for active-state tracking)
        foreach (var desc in allItems)
        {
            var item = new NavigationItemViewModel(desc)
            {
                IsActive = string.Equals(desc.Id, currentSectionId, StringComparison.OrdinalIgnoreCase)
            };
            DynamicNavigationItems.Add(item);

            // Contributed = items NOT in the built-in standard set (for legacy EXTENSIONS section)
            if (!StandardNavSectionIds.Contains(desc.Id))
            {
                ContributedNavigationItems.Add(item);
            }
        }

        // Grouped list (for fully plugin-driven sidebar rendering)
        var groups = allItems
            .GroupBy(d => d.Group)
            .OrderBy(g => allItems.First(d => d.Group == g.Key).Order)
            .Select(g =>
            {
                var items = g
                    .OrderBy(d => d.Order)
                    .Select(d => DynamicNavigationItems.First(v => v.Id == d.Id))
                    .ToList();
                return new NavGroupViewModel(g.Key, items);
            })
            .ToList();

        foreach (var group in groups)
        {
            NavGroups.Add(group);
        }

        OnPropertyChanged(nameof(IsPluginDrivenNavigation));

        // If the current section was unmounted and is no longer available in the registry,
        // navigate back to Home to avoid leaving the view in an orphaned state.
        if (SelectedNavSection == HomeNavSection.DiagnosticLogs &&
            !allItems.Any(d => string.Equals(d.Id, "DiagnosticLogs", StringComparison.OrdinalIgnoreCase)))
        {
            SelectNavSection("Home");
        }
    }


    private static readonly HashSet<string> StandardNavSectionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Home", "PdfReader", "NewDocument", "AllTools", "OrganizeAndPage", "OptimizeAndSecurity",
        "ConvertFromPdf", "ConvertToPdf", "EditAndForms", "AiAndAutomation", "Starred",
        "FontPackages", "TesseractData", "Trash", "Help", "Licensing", "Settings", "Plugins"
    };

    [RelayCommand]
    public void SelectNavSection(string sectionName)
    {
        var from = ActiveNavDescriptor?.Id ?? SelectedNavSection.ToString();
        var sw = Stopwatch.StartNew();

        PdfEditorApp.Core.Plugins.Descriptors.NavigationItemDescriptor? activeDesc = null;

        object? targetView = null;
        string viewOrigin = "pre-mounted";

        // Special case for Plugins: HomeView hosts a dedicated full-viewport PluginsManagerPageView
        if (string.Equals(sectionName, "Plugins", StringComparison.OrdinalIgnoreCase))
        {
            if (_navigationRegistry != null)
            {
                activeDesc = _navigationRegistry.GetItem(sectionName);
            }
        }
        else if (_navigationRegistry != null)
        {
            var desc = _navigationRegistry.GetItem(sectionName);
            activeDesc = desc;
            if (desc?.ViewFactory != null)
            {
                if (!_dynamicViewCache.TryGetValue(sectionName, out var cachedView))
                {
                    viewOrigin = "cold-factory";
                    try
                    {
                        var sp = _serviceProvider ?? (IServiceProvider)this;
                        cachedView = desc.ViewFactory(sp);
                        _dynamicViewCache[sectionName] = cachedView;
                    }
                    catch (Exception ex)
                    {
                        AppLogService.Instance.LogError("Navigation", $"Failed to create plugin view for '{sectionName}'", ex);
                        cachedView = null;
                    }
                }
                else
                {
                    viewOrigin = "cache-hit";
                }
                targetView = cachedView;
            }
        }

        ActiveNavDescriptor = activeDesc;
        DynamicPageView = targetView;

        // Update active states across all flat nav items
        foreach (var item in DynamicNavigationItems)
        {
            item.IsActive = string.Equals(item.Id, sectionName, StringComparison.OrdinalIgnoreCase);
        }

        if (IsToolPageActive)
        {
            BackToTools();
        }

        if (Enum.TryParse<HomeNavSection>(sectionName, true, out var section))
        {
            SelectedNavSection = section;

            if (section == HomeNavSection.NewDocument)
            {
                // Deliberately does NOT expand the gallery here. Setting
                // IsTemplateGalleryExpanded = true switched the view from ~15 featured
                // templates to all of them at the exact moment of the transition, so the
                // navigation paid for the largest possible layout. The user can expand it.
                //
                // Thumbnail rasterization is driven by NewDocumentPageView, which owns the
                // attached render host it requires.
            }

            // Automatically set tool category filter according to selected section
            var category = section switch
            {
                HomeNavSection.OrganizeAndPage => "Organize & Page",
                HomeNavSection.OptimizeAndSecurity => "Optimize & Security",
                HomeNavSection.ConvertFromPdf => "Convert from PDF",
                HomeNavSection.ConvertToPdf => "Convert to PDF",
                HomeNavSection.EditAndForms => "Edit & Forms",
                HomeNavSection.AiAndAutomation => "AI & Automation",
                _ => "All"
            };

            if (SelectedToolCategory != category)
            {
                SelectedToolCategory = category;
            }
            else
            {
                UpdateFilteredTools();
            }
        }

        LogNavigationWhenVisible(from, sectionName, sw, viewOrigin);
    }

    /// <summary>Navigations slower than this are logged as a Warning so they surface in the UI.</summary>
    private const int SlowNavigationMs = 250;


    /// <summary>
    /// Logs a navigation once the newly-visible page has actually been laid out and rendered.
    /// </summary>
    /// <remarks>
    /// Reporting <c>sw.ElapsedMilliseconds</c> at the end of the navigation method measured only
    /// the view-model work — property sets and PropertyChanged. Avalonia lays out and renders the
    /// newly-visible page on later dispatcher frames, so a page that took seconds to appear was
    /// logged as "4ms (pre-mounted)". Posting at <see cref="DispatcherPriority.Background"/>
    /// defers this until after that frame's layout and render, so the elapsed time covers the
    /// whole click-to-pixels path. Both phases are reported so the split is visible.
    /// </remarks>
    private static void LogNavigationWhenVisible(string from, string to, Stopwatch sw, string viewOrigin)
    {
        long viewModelMs = sw.ElapsedMilliseconds;

        void Report()
        {
            sw.Stop();
            long totalMs = sw.ElapsedMilliseconds;
            AppLogService.Instance.LogDuration(
                "Navigation",
                $"{from} -> {to} in {totalMs}ms (vm={viewModelMs}ms render={totalMs - viewModelMs}ms, {viewOrigin}).",
                totalMs,
                SlowNavigationMs);
        }

        // No dispatcher loop in headless/unit-test hosts: Post would silently never run.
        if (Application.Current == null)
        {
            Report();
            return;
        }

        Dispatcher.UIThread.Post(Report, DispatcherPriority.Background);
    }


    [RelayCommand]
    public void OpenToolPage(PdfToolId toolId)
    {
        OpenToolPage(toolId, null);
    }

    public void OpenToolPage(PdfToolId toolId, string? initialFilePath = null)
    {
        var from = ActiveToolCard?.Name ?? ActiveNavDescriptor?.Id ?? SelectedNavSection.ToString();
        var sw = Stopwatch.StartNew();

        if (toolId == PdfToolId.BatchMailMerge)
        {
            WeakReferenceMessenger.Default.Send(new OpenBatchGenerationMessage());
            OpenBatchGenerationRequested?.Invoke();
            return;
        }

        var card = AllTools.FirstOrDefault(t => t.Id == toolId);
        if (card != null)
        {
            ActiveToolCard = card;
            IsToolPageActive = true;

            if (_toolViewModelFactory != null)
            {
                if (ActiveToolViewModel != null)
                {
                    ActiveToolViewModel.BackRequested -= BackToTools;
                    ActiveToolViewModel.NavigateToToolRequested -= OnToolNavigateToToolRequested;
                    ActiveToolViewModel.HelpGuideRequested -= OnToolHelpGuideRequested;
                }

                ActiveToolViewModel = _toolViewModelFactory.Create(toolId);
                ActiveToolViewModel.StorageProvider = MainViewModel.StorageProvider;
                ActiveToolViewModel.IsToolStarred = card.IsStarred;
                if (!string.IsNullOrEmpty(initialFilePath))
                {
                    ActiveToolViewModel.SetupInitialFiles(new[] { initialFilePath });
                }
                ActiveToolViewModel.BackRequested += BackToTools;
                ActiveToolViewModel.NavigateToToolRequested += OnToolNavigateToToolRequested;
                ActiveToolViewModel.HelpGuideRequested += OnToolHelpGuideRequested;
                ActiveToolViewModel.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PdfToolViewModelBase.IsToolStarred) && ActiveToolCard != null && ActiveToolViewModel != null)
                    {
                        if (ActiveToolCard.IsStarred != ActiveToolViewModel.IsToolStarred)
                        {
                            ActiveToolCard.ToggleStar();
                            RefreshStarredTools();
                        }
                    }
                };
            }

            // Same blind spot as SelectNavSection: the tool's View is resolved through
            // ViewLocator and laid out after this method returns, so the elapsed time here
            // covers ViewModel construction only.
            LogNavigationWhenVisible(from, card.Name, sw, "new ViewModel instance");
        }
        else
        {
            AppLogService.Instance.Log(AppLogLevel.Warning, "Navigation",
                $"{from} -> tool '{toolId}' failed after {sw.ElapsedMilliseconds}ms: no matching tool card found.");
        }
    }

    private void OnToolNavigateToToolRequested(PdfToolId nextToolId, string targetFile)
    {
        OpenToolPage(nextToolId, targetFile);
    }

    private void OnToolHelpGuideRequested(PdfToolId toolId)
    {
        OpenHelpForTool(toolId);
    }

    [RelayCommand]
    public void BackToTools()
    {
        IsToolPageActive = false;
        ActiveToolCard = null;
        if (ActiveToolViewModel != null)
        {
            ActiveToolViewModel.BackRequested -= BackToTools;
            ActiveToolViewModel.NavigateToToolRequested -= OnToolNavigateToToolRequested;
            ActiveToolViewModel.HelpGuideRequested -= OnToolHelpGuideRequested;
            ActiveToolViewModel = null;
        }
    }

    [RelayCommand]
    public void ToggleStarActiveTool()
    {
        if (ActiveToolCard != null)
        {
            ActiveToolCard.ToggleStar();
            if (ActiveToolViewModel != null)
            {
                ActiveToolViewModel.IsToolStarred = ActiveToolCard.IsStarred;
            }
            if (ToolRunner != null)
            {
                ToolRunner.IsToolStarred = ActiveToolCard.IsStarred;
            }
            RefreshStarredTools();
        }
    }

    public void RefreshStarredTools()
    {
        StarredTools.Clear();
        foreach (var tool in AllTools.Where(t => t.IsStarred))
        {
            StarredTools.Add(tool);
        }
        OnPropertyChanged(nameof(HasStarredTools));
    }

    [RelayCommand]
    public void SelectTool(PdfToolId toolId)
    {
        if (toolId == PdfToolId.WorkflowBuilder)
        {
            WeakReferenceMessenger.Default.Send(new OpenWorkflowStudioMessage());
            OpenWorkflowBuilderRequested?.Invoke();
        }
        else
        {
            OpenToolPage(toolId);
        }
    }

    [RelayCommand]
    public void OpenWorkflowBuilder()
    {
        WeakReferenceMessenger.Default.Send(new OpenWorkflowStudioMessage());
        OpenWorkflowBuilderRequested?.Invoke();
    }

    [RelayCommand]
    public void SelectTemplate(string? templateName)
    {
        WeakReferenceMessenger.Default.Send(new OpenTemplateMessage(templateName));
        OpenTemplateRequested?.Invoke(templateName);
    }

    [RelayCommand]
    public void OpenFile()
    {
        WeakReferenceMessenger.Default.Send(new OpenProjectFileMessage());
        OpenFileRequested?.Invoke();
    }

    [RelayCommand]
    public void OpenRecent(string filePath)
    {
        WeakReferenceMessenger.Default.Send(new OpenInEditorMessage(filePath));
    }

    [RelayCommand]
    public void OpenInViewer(string? filePath)
    {
        var path = filePath ?? string.Empty;
        WeakReferenceMessenger.Default.Send(new OpenInViewerMessage(path));
    }

    [RelayCommand]
    public void OpenInInteractiveViewer(string? filePath)
    {
        var path = filePath ?? string.Empty;
        WeakReferenceMessenger.Default.Send(new OpenInFryPdfViewerMessage(path));
    }

    [RelayCommand]
    public void LaunchInteractiveDeck()
    {
        WeakReferenceMessenger.Default.Send(new LaunchInteractiveDeckMessage());
    }

    [RelayCommand]
    public void PromptRename(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        RenameTargetFilePath = filePath;
        RenameNewName = Path.GetFileNameWithoutExtension(filePath);
        RenameExtension = Path.GetExtension(filePath);
        RenameErrorMessage = "";
        IsRenameDialogOpen = true;
    }

    [RelayCommand]
    public void ConfirmRename()
    {
        if (!FileOperationHelper.RenameFile(RenameTargetFilePath, RenameNewName, out var newPath, out var error))
        {
            RenameErrorMessage = error ?? "Failed to rename document.";
            return;
        }

        var oldPath = RenameTargetFilePath;
        var finalNewPath = newPath!;
        var newTitle = Path.GetFileName(finalNewPath);

        _recentService.Rename(oldPath, finalNewPath, newTitle);
        RefreshRecent();

        IsRenameDialogOpen = false;
        WeakReferenceMessenger.Default.Send(new ProjectRenamedMessage(oldPath, finalNewPath));
        DocumentRenamed?.Invoke(oldPath, finalNewPath);
        TriggerToast($"Renamed to {newTitle}", ToastNotificationType.Success, "PencilOutline");
    }

    [RelayCommand]
    public void CancelRename()
    {
        IsRenameDialogOpen = false;
        RenameErrorMessage = "";
    }

    [RelayCommand]
    public void PromptDelete(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        DeleteTargetFilePath = filePath;
        DeleteTargetFileName = Path.GetFileName(filePath);
        IsDeleteConfirmDialogOpen = true;
    }

    [RelayCommand]
    public void ConfirmDelete()
    {
        var targetPath = DeleteTargetFilePath;
        var targetName = DeleteTargetFileName;
        IsDeleteConfirmDialogOpen = false;

        if (!FileOperationHelper.DeleteFile(targetPath, out var error))
        {
            TriggerToast($"Could not delete file: {error}", ToastNotificationType.Danger, "AlertCircleOutline");
            return;
        }

        _recentService.Remove(targetPath);
        RefreshRecent();
        WeakReferenceMessenger.Default.Send(new ProjectDeletedMessage(targetPath));
        DocumentDeleted?.Invoke(targetPath);
        TriggerToast($"Deleted {targetName}", ToastNotificationType.Success, "TrashCanOutline");
    }

    [RelayCommand]
    public void CancelDelete()
    {
        IsDeleteConfirmDialogOpen = false;
    }

    [RelayCommand]
    public void DuplicateDocument(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        if (!FileOperationHelper.DuplicateFile(filePath, out var newPath, out var error))
        {
            TriggerToast($"Could not duplicate document: {error}", ToastNotificationType.Danger, "AlertCircleOutline");
            return;
        }

        var copyTitle = Path.GetFileName(newPath!);
        _recentService.Add(new RecentDocumentItem
        {
            FilePath = newPath!,
            Title = copyTitle,
            LastOpened = DateTime.UtcNow
        });
        RefreshRecent();
        TriggerToast($"Created duplicate: {copyTitle}", ToastNotificationType.Success, "ContentCopy");
    }

    [RelayCommand]
    public void RevealInFileManager(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        if (!FileOperationHelper.RevealInFileManager(filePath, out var error))
        {
            TriggerToast($"Could not reveal file: {error}", ToastNotificationType.Danger, "AlertCircleOutline");
            return;
        }
        TriggerToast("Opened containing folder", ToastNotificationType.Primary, "FolderOpenOutline");
    }

    [RelayCommand]
    public async Task CopyPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        await CopyToClipboardAsync(filePath);
        TriggerToast("Copied file path to clipboard", ToastNotificationType.Primary, "ClipboardTextOutline");
    }

    [RelayCommand]
    public void RemoveRecent(string filePath)
    {
        _recentService.Remove(filePath);
        RefreshRecent();
        TriggerToast("Removed from recent documents", ToastNotificationType.Primary, "BookmarkRemoveOutline");
    }

    [RelayCommand]
    public void ClearRecent()
    {
        _recentService.Clear();
        RefreshRecent();
        TriggerToast("Cleared recent document history", ToastNotificationType.Primary, "TrashCanOutline");
    }

    // --- Licensing & Third-Party Open Source Initialization ---

    private void InitializeLicenses()
    {
        AllLicenses.Clear();

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "Avalonia UI",
            Version = "12.1.1",
            LicenseType = "MIT License",
            Category = "UI & Graphics Frameworks",
            Purpose = "Core cross-platform modern XAML UI application framework providing high-performance GPU-accelerated graphics, styling, smooth vector transitions, and native window management on macOS, Windows, and Linux.",
            Maintainer = "AvaloniaUI Team & .NET Foundation Community",
            ProjectUrl = "https://avaloniaui.net",
            IconKind = "ApplicationOutline",
            AccentColorHex = "#7029E6",
            LicenseText = """
MIT License

Copyright (c) 2014-2026 AvaloniaUI OÜ

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "QuestPDF",
            Version = "2026.8.0",
            LicenseType = "Community / MIT",
            Category = "PDF & Document Engines",
            Purpose = "High-performance vector PDF rendering and generation engine utilizing fluent layout APIs, smart pagination, table structures, vector shapes, and sub-millimeter typographical accuracy.",
            Maintainer = "Marcin Ziąbek & QuestPDF Contributors",
            ProjectUrl = "https://www.questpdf.com",
            IconKind = "FilePdfBox",
            AccentColorHex = "#FF4500",
            LicenseText = """
QuestPDF Community License / MIT License

Copyright (c) 2020-2026 Marcin Ziąbek

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "PdfPig & Skia Rendering",
            Version = "0.1.16.1",
            LicenseType = "Apache 2.0",
            Category = "PDF & Document Engines",
            Purpose = "Pure C# low-level PDF parsing and extraction engine. Extracts structured text glyphs, font matrices, bounding boxes, vector curves, and rasterizes high-fidelity pages to Skia surfaces for the PDF viewer.",
            Maintainer = "Eli White (UglyToad) & Open Source Contributors",
            ProjectUrl = "https://github.com/UglyToad/PdfPig",
            IconKind = "VectorCircle",
            AccentColorHex = "#0284C7",
            LicenseText = """
Apache License
Version 2.0, January 2004
http://www.apache.org/licenses/

Copyright 2018-2026 UglyToad (Eli White) and contributors

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "PdfSharpCore",
            Version = "1.3.67",
            LicenseType = "MIT License",
            Category = "PDF & Document Engines",
            Purpose = "Low-level PDF manipulation engine handling direct binary page extraction, PDF merging, page splitting, rotation, AES-128/256 document security encryption, metadata injection, and Bates stamping.",
            Maintainer = "Stefan Lange, empira Software & Community",
            ProjectUrl = "https://github.com/stefan-lange/pdfsharpcore",
            IconKind = "ShieldLockOutline",
            AccentColorHex = "#10B981",
            LicenseText = """
MIT License

Copyright (c) 2005-2026 empira Software GmbH, Troisdorf (Germany)
Copyright (c) 2017-2026 Stefan Lange and PdfSharpCore contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "SkiaSharp",
            Version = "3.116.1",
            LicenseType = "MIT License",
            Category = "UI & Graphics Frameworks",
            Purpose = "Cross-platform 2D graphics API based on Google's Skia Graphics Library. Powers fluid continuous canvas zooming, anti-aliased Bézier curve evaluation, sub-pixel text rendering, and image caching.",
            Maintainer = "Mono Project & .NET Foundation",
            ProjectUrl = "https://github.com/mono/SkiaSharp",
            IconKind = "Draw",
            AccentColorHex = "#0EA5E9",
            LicenseText = """
MIT License

Copyright (c) 2015-2026 Xamarin Inc.
Copyright (c) 2017-2026 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "Tabula Table Extractor",
            Version = "1.0.1",
            LicenseType = "MIT License",
            Category = "Office & Data Formats",
            Purpose = "Spatial table extraction engine that detects table boundaries, cell grids, columns, and data coordinates from unstructured PDF documents for CSV, JSON, and Excel tabular conversions.",
            Maintainer = "Manuel Aristarán & Tabula Contributors",
            ProjectUrl = "https://github.com/tabulapdf/tabula-java",
            IconKind = "Table",
            AccentColorHex = "#F59E0B",
            LicenseText = """
MIT License

Copyright (c) 2014-2026 Manuel Aristarán, Mike Tigas, and Jeremy B. Merrill

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "DocumentFormat.OpenXml",
            Version = "3.5.1",
            LicenseType = "MIT License",
            Category = "Office & Data Formats",
            Purpose = "High-speed OpenXML engine for reading, parsing, and exporting structured Microsoft Word (.docx), Excel (.xlsx), and PowerPoint (.pptx) documents from converted PDF files.",
            Maintainer = "Microsoft Corporation & .NET Foundation",
            ProjectUrl = "https://github.com/dotnet/Open-XML-SDK",
            IconKind = "FileWordOutline",
            AccentColorHex = "#2563EB",
            LicenseText = """
MIT License

Copyright (c) Microsoft Corporation. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "Material.Icons.Avalonia",
            Version = "3.0.2",
            LicenseType = "MIT License",
            Category = "UI & Graphics Frameworks",
            Purpose = "Vector iconography library supplying thousands of scalable Material Design icon glyphs across toolbars, ribbon bars, inspectors, tool cards, and dialogs.",
            Maintainer = "SKFox5330 & Pictogrammers Community",
            ProjectUrl = "https://github.com/SKFox5330/Material.Icons.Avalonia",
            IconKind = "StarFourPointsOutline",
            AccentColorHex = "#8B5CF6",
            LicenseText = """
MIT License

Copyright (c) 2020-2026 SKFox5330 & Pictogrammers

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "CommunityToolkit.Mvvm",
            Version = "8.4.2",
            LicenseType = "MIT License",
            Category = "Architecture & Runtime",
            Purpose = "Official Microsoft .NET Community Toolkit MVVM framework providing high-performance source-generated ObservableObject, RelayCommand, and reactive property notifications.",
            Maintainer = "Microsoft Corporation & Community Toolkit Team",
            ProjectUrl = "https://github.com/CommunityToolkit/dotnet",
            IconKind = "LightningBoltOutline",
            AccentColorHex = "#6366F1",
            LicenseText = """
MIT License

Copyright (c) .NET Foundation and Contributors. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "QRCoder",
            Version = "1.8.0",
            LicenseType = "MIT License",
            Category = "Office & Data Formats",
            Purpose = "Pure C# QR code generating library powering dynamic vector URL, Wi-Fi, vCard, SMS, Email, Crypto, and plain text QR code elements in FryPDF document authoring.",
            Maintainer = "Raffael Herrmann (codebude) & Contributors",
            ProjectUrl = "https://github.com/codebude/QRCoder",
            IconKind = "Qrcode",
            AccentColorHex = "#EC4899",
            LicenseText = """
MIT License

Copyright (c) 2013-2026 Raffael Herrmann

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "Microsoft.Extensions.DependencyInjection",
            Version = "10.0.11",
            LicenseType = "MIT License",
            Category = "Architecture & Runtime",
            Purpose = "High-performance Inversion of Control (IoC) dependency injection container powering modular service registration, loose coupling, and decoupled testing across all 32 PDF tools.",
            Maintainer = "Microsoft Corporation & .NET Runtime Team",
            ProjectUrl = "https://github.com/dotnet/runtime",
            IconKind = "ToyBrickOutline",
            AccentColorHex = "#0078D4",
            LicenseText = """
MIT License

Copyright (c) .NET Foundation and Contributors. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = ".NET 10 Runtime & Base Libraries",
            Version = "10.0",
            LicenseType = "MIT License",
            Category = "Architecture & Runtime",
            Purpose = "Modern cross-platform .NET 10 runtime engine providing hardware-accelerated SIMD (AVX/NEON) vector mathematics, AES-256 cryptography primitives, Task-based asynchronous I/O, and garbage collection.",
            Maintainer = "Microsoft Corporation & .NET Foundation",
            ProjectUrl = "https://dotnet.microsoft.com",
            IconKind = "Microsoft",
            AccentColorHex = "#512BD4",
            LicenseText = """
MIT License

Copyright (c) .NET Foundation and Contributors. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "LiveChartsCore & LiveChartsCore.SkiaSharpView",
            Version = "2.0.5",
            LicenseType = "MIT License",
            Category = "UI & Graphics Frameworks",
            Purpose = "Modern, fluid, and hardware-accelerated charting library for .NET. Renders high-performance bar charts, line graphs, and pie/doughnut charts with SkiaSharp integration directly into PDF documents and vector canvases.",
            Maintainer = "Alberto Rodriguez (beto-rodriguez) & LiveCharts Contributors",
            ProjectUrl = "https://github.com/beto-rodriguez/LiveCharts2",
            IconKind = "ChartBoxOutline",
            AccentColorHex = "#3B82F6",
            LicenseText = """
MIT License

Copyright (c) 2020-2026 Alberto Rodriguez

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "Microsoft.Extensions.Logging.Abstractions",
            Version = "10.0.1",
            LicenseType = "MIT License",
            Category = "Architecture & Runtime",
            Purpose = "Structured logging abstractions and diagnostic interfaces for .NET. Powers logging pipelines, diagnostic tracing, and pipeline telemetry across core PDF deconstruction, OCR processing, and document layout engines.",
            Maintainer = "Microsoft Corporation & .NET Foundation",
            ProjectUrl = "https://github.com/dotnet/runtime",
            IconKind = "FormatListBulleted",
            AccentColorHex = "#2563EB",
            LicenseText = """
MIT License

Copyright (c) .NET Foundation and Contributors. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "SIL Open Font License (OFL 1.1) Typeface Collection",
            Version = "1.1",
            LicenseType = "SIL OFL 1.1",
            Category = "Typography & Typefaces",
            Purpose = "Comprehensive open-source multilingual font collection powering FryPDF typography, including Google Noto Sans (Devanagari, Tamil, Telugu, Bengali, Gujarati, Kannada, Malayalam, Arabic, Urdu, Hebrew, Thai, Lao, Khmer, Myanmar, Sinhala, CJK), Inter, Poppins, Montserrat, Raleway, Lato, Fira Code, Caveat, Orbitron, and Oswald.",
            Maintainer = "SIL International, Google Fonts & Type Designers",
            ProjectUrl = "https://scripts.sil.org/OFL",
            IconKind = "FormatFont",
            AccentColorHex = "#D946EF",
            LicenseText = """
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide development of collaborative font projects, to support the font creation efforts of academic and linguistic communities, and to provide a free and open framework in which fonts may be shared and improved in partnership with others.

The OFL allows the licensed fonts to be used, studied, modified and redistributed freely as long as they are not sold by themselves. The fonts, including any derivative works, can be bundled, embedded, redistributed and/or sold with any software provided that any reserved names are not used by derivative works. The fonts and derivatives, however, cannot be released under any other type of license. The requirement for fonts to remain under this license does not apply to any document created using the fonts or their derivatives.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining a copy of the Font Software, to use, study, copy, merge, embed, modify, redistribute, and sell modified and unmodified copies of the Font Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components, in Original or Modified Versions, may be sold by itself.
2) Original or Modified Versions of the Font Software may be bundled, redistributed and/or sold with any software, provided that each copy contains the above copyright notice and this license.
3) No Modified Version of the Font Software may use the Reserved Font Name(s) unless explicit written permission is granted by the corresponding Copyright Holder.
4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font Software shall not be used to promote, endorse or advertise any Modified Version, except to acknowledge the contribution(s) of the Copyright Holder(s) and the Author(s) or with their explicit written permission.
5) The Font Software, modified or unmodified, in part or in whole, must be distributed entirely under this license, and must not be distributed under any other license.

TERMINATION
This license becomes null and void if any of the above conditions are not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM OTHER DEALINGS IN THE FONT SOFTWARE.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "Roboto & Roboto Mono Font Family",
            Version = "2.138",
            LicenseType = "Apache 2.0",
            Category = "Typography & Typefaces",
            Purpose = "Google's flagship sans-serif and monospaced typefaces designed by Christian Robertson. Serves as the primary document reader font, technical tables, code spans, and corporate report templates.",
            Maintainer = "Google LLC & Christian Robertson",
            ProjectUrl = "https://github.com/googlefonts/roboto",
            IconKind = "FormatText",
            AccentColorHex = "#EA4335",
            LicenseText = """
Apache License
Version 2.0, January 2004
http://www.apache.org/licenses/

Copyright 2011 Google Inc. All Rights Reserved.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "Ubuntu Font Family",
            Version = "0.83",
            LicenseType = "Ubuntu Font Licence 1.0",
            Category = "Typography & Typefaces",
            Purpose = "Distinctive contemporary humanist sans-serif typeface designed by Dalton Maag for Canonical Ltd. Included in the Creative & Design typography package for modern headings, branded certificates, and flyers.",
            Maintainer = "Canonical Ltd & Dalton Maag",
            ProjectUrl = "https://design.ubuntu.com/font/",
            IconKind = "Alphabetical",
            AccentColorHex = "#E95420",
            LicenseText = """
Ubuntu Font Licence - Version 1.0
https://ubuntu.com/legal/font-licence

Copyright 2010 Canonical Ltd.
Licensed under the Ubuntu Font Licence version 1.0.

Permission is hereby granted, free of charge, to any person obtaining a copy of the Font Software, to use, study, copy, merge, embed, modify, redistribute, and sell modified and unmodified copies of the Font Software, subject to the following conditions:

1. Neither the Font Software nor any of its individual components, in Original or Modified Versions, may be sold by itself.
2. Original or Modified Versions of the Font Software may be bundled, redistributed and/or sold with any software, provided that each copy contains the above copyright notice and this license.
3. No Modified Version of the Font Software may use the Reserved Font Name(s) unless explicit written permission is granted by the corresponding Copyright Holder.
4. The name(s) of the Copyright Holder(s) or the Author(s) of the Font Software shall not be used to promote, endorse or advertise any Modified Version.
5. The Font Software, modified or unmodified, in part or in whole, must be distributed entirely under this license.

THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "Microsoft.Extensions.AI & OpenAI",
            Version = "10.9.0",
            LicenseType = "MIT License",
            Category = "AI & Document Intelligence",
            Purpose = "Official Microsoft unified AI abstractions and OpenAI connector enabling autonomous AI studio agent workflows, generative PDF document composition from natural language prompts, text summarization, and neural translation.",
            Maintainer = "Microsoft Corporation & .NET AI Team",
            ProjectUrl = "https://github.com/dotnet/extensions",
            IconKind = "RobotOutline",
            AccentColorHex = "#6366F1",
            LicenseText = """
MIT License

Copyright (c) .NET Foundation and Contributors. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "OllamaSharp",
            Version = "5.4.30",
            LicenseType = "MIT License",
            Category = "AI & Document Intelligence",
            Purpose = "High-performance .NET API client for Ollama enabling 100% private, offline, and zero-telemetry local LLM inference (Llama 3.2, Mistral, Gemma, Phi-3) to create PDF documents and analyze text locally on CPU/GPU hardware.",
            Maintainer = "Andreas Müller (awaescher) & Open Source Community",
            ProjectUrl = "https://github.com/awaescher/OllamaSharp",
            IconKind = "LaptopAccount",
            AccentColorHex = "#EC4899",
            LicenseText = """
MIT License

Copyright (c) 2023-2026 Andreas Müller

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"""
        });

        AllLicenses.Add(new ThirdPartyToolLicense
        {
            Name = "Tesseract OCR Engine",
            Version = "5.4.0",
            LicenseType = "Apache 2.0",
            Category = "AI & Document Intelligence",
            Purpose = "Industrial open-source Optical Character Recognition engine with support for multi-language traineddata models, powering local scanned document text extraction, word bounding-box discovery, and searchable PDF compilation.",
            Maintainer = "Ray Smith, Google LLC & Tesseract Community",
            ProjectUrl = "https://github.com/tesseract-ocr/tesseract",
            IconKind = "TextRecognition",
            AccentColorHex = "#0EA5E9",
            LicenseText = """
Apache License
Version 2.0, January 2004
http://www.apache.org/licenses/

Copyright (c) 1985-2026 Ray Smith, Google LLC, and Tesseract contributors

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
"""
        });

        UpdateFilteredLicenses();
    }

    private void UpdateFilteredLicenses()
    {
        FilteredLicenses.Clear();
        foreach (var lib in AllLicenses)
        {
            if (MatchesLicenseCategory(lib) && MatchesLicenseSearch(lib))
            {
                FilteredLicenses.Add(lib);
            }
        }

        OnPropertyChanged(nameof(MatchingLicensesCount));
        OnPropertyChanged(nameof(HasNoMatchingLicenses));
    }

    private bool MatchesLicenseCategory(ThirdPartyToolLicense lib)
    {
        if (SelectedLicenseCategory == "All") return true;

        return string.Equals(SelectedLicenseCategory, lib.Category, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesLicenseSearch(ThirdPartyToolLicense lib)
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return true;
        var q = SearchQuery.Trim();
        return lib.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || lib.Category.Contains(q, StringComparison.OrdinalIgnoreCase)
            || lib.LicenseType.Contains(q, StringComparison.OrdinalIgnoreCase)
            || lib.Purpose.Contains(q, StringComparison.OrdinalIgnoreCase)
            || lib.Maintainer.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    public void SetLicenseCategory(string? category)
    {
        SelectedLicenseCategory = string.IsNullOrWhiteSpace(category) ? "All" : category;
    }

    [RelayCommand]
    public void ClearLicenseSearch()
    {
        SearchQuery = "";
        SelectedLicenseCategory = "All";
        UpdateFilteredLicenses();
    }

    [RelayCommand]
    public void ToggleLicenseExpand(ThirdPartyToolLicense? license)
    {
        if (license != null)
        {
            license.IsExpanded = !license.IsExpanded;
        }
    }

    [RelayCommand]
    public async Task CopyLicenseText(ThirdPartyToolLicense? license)
    {
        if (license == null) return;
        var textToCopy = $"--- {license.Name} ({license.Version}) ---\nLicense: {license.LicenseType}\nMaintainer: {license.Maintainer}\nProject URL: {license.ProjectUrl}\n\n{license.LicenseText}";
        await CopyToClipboardAsync(textToCopy);
    }

    [RelayCommand]
    public async Task CopyFullAttributions()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine("FryPDF — Open Source & Third-Party Software Attribution Notice");
        sb.AppendLine("Copyright (c) 2026 Code Fry Dev. All rights reserved.");
        sb.AppendLine("================================================================================\n");

        foreach (var lib in AllLicenses)
        {
            sb.AppendLine($"--------------------------------------------------------------------------------");
            sb.AppendLine($"Package: {lib.Name} (v{lib.Version})");
            sb.AppendLine($"Category: {lib.Category}");
            sb.AppendLine($"License: {lib.LicenseType}");
            sb.AppendLine($"Maintainer: {lib.Maintainer}");
            sb.AppendLine($"URL: {lib.ProjectUrl}");
            sb.AppendLine($"Purpose: {lib.Purpose}");
            sb.AppendLine($"--------------------------------------------------------------------------------");
            sb.AppendLine(lib.LicenseText.Trim());
            sb.AppendLine();
        }

        await CopyToClipboardAsync(sb.ToString());
    }

    [RelayCommand]
    public async Task OpenLibraryWebsite(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.Launcher != null)
            {
                await desktop.MainWindow.Launcher.LaunchUriAsync(new Uri(url));
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
        }
        catch
        {
            await CopyToClipboardAsync(url);
        }
    }

    private static async Task CopyToClipboardAsync(string text)
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow?.Clipboard != null)
            {
                await desktop.MainWindow.Clipboard.SetTextAsync(text);
            }
        }
        catch { }
    }

    // --- Helpers ---

    public void RefreshRecent()
    {
        RecentDocuments.Clear();
        var items = _recentService.Load();
        foreach (var item in items)
        {
            RecentDocuments.Add(item);
        }
        OnPropertyChanged(nameof(HasRecentDocuments));

        _ = LoadRecentPreviewsAsync(items);
    }

    private async Task LoadRecentPreviewsAsync(List<RecentDocumentItem> items)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath))
                continue;

            try
            {
                if (item.FilePath.EndsWith(".frypdf", StringComparison.OrdinalIgnoreCase) ||
                    item.FilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var doc = await _persistenceService.LoadProjectAsync(item.FilePath);
                    if (doc != null && doc.Pages.Count > 0)
                    {
                        var pageVm = new PageViewModel();
                        pageVm.LoadFromModel(doc.Pages[0]);
                        item.PagePreview = pageVm;
                        item.PageCount = doc.Pages.Count;
                        item.FormatDescription = $"{doc.Pages[0].Format} {doc.Pages[0].Orientation}";
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Resolves services and child ViewModels requested by plugin-driven navigation page factories.
    /// </summary>
    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(HomeViewModel)) return this;
        if (serviceType == typeof(SettingsViewModel)) return Settings;
        if (serviceType == typeof(PluginsManagerViewModel)) return PluginsManager;
        if (serviceType == typeof(FontManagerViewModel)) return FontManager;
        if (serviceType == typeof(TesseractManagerViewModel)) return TesseractManager;
        if (serviceType == typeof(HelpGuideViewModel)) return HelpGuide;
        if (serviceType == typeof(PdfToolRunnerViewModel) && ToolRunner != null) return ToolRunner;
        if (serviceType == typeof(IRecentDocumentsService)) return _recentService;
        if (serviceType == typeof(ITemplateService)) return _templateService;
        if (serviceType == typeof(IProjectPersistenceService)) return _persistenceService;
        if (serviceType == typeof(IPdfToolRegistry)) return _toolRegistry;
        if (serviceType == typeof(IThemeService) && _themeService != null) return _themeService;
        if (serviceType == typeof(IUiSettingsService) && _uiSettingsService != null) return _uiSettingsService;
        if (serviceType == typeof(PdfEditorApp.Core.Plugins.Descriptors.INavigationRegistry) && _navigationRegistry != null) return _navigationRegistry;
        return _serviceProvider?.GetService(serviceType) ?? App.Services?.GetService(serviceType);
    }

    private void OnThemeServiceThemeChanged(AppThemeMode mode) => IsDarkMode = _themeService!.IsDarkMode;

    private void OnHelpGuideToolLaunchRequested(PdfToolId id) => OpenToolPage(id);

    private bool _isDisposed;

    /// <summary>
    /// Detaches from the DI singletons this view model subscribed to.
    /// </summary>
    /// <remarks>
    /// The registries and IThemeService are singletons, so each subscription rooted this view
    /// model permanently. This class already unsubscribes ActiveToolViewModel correctly
    /// elsewhere — these were simply missed.
    /// </remarks>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_themeService != null) _themeService.ThemeChanged -= OnThemeServiceThemeChanged;
        if (ToolRunner != null) ToolRunner.BackRequested -= BackToTools;
        HelpGuide.ToolLaunchRequested -= OnHelpGuideToolLaunchRequested;
        if (_navigationRegistry != null) _navigationRegistry.RegistryChanged -= RefreshDynamicNavigationItems;
        if (_toolRegistry != null) _toolRegistry.RegistryChanged -= InitializeTools;

        GC.SuppressFinalize(this);
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEditorApp.Core.Models;
using PdfEditorApp.Core.Models.Elements;
using PdfEditorApp.ViewModels.ElementViewModels;

namespace PdfEditorApp.ViewModels.FryPdfViewer;

/// <summary>
/// Page ViewModel specifically designed for read-only presentation and interactive document viewing.
/// Separates standard visual elements from rich interactive elements (tables, charts, living forms).
/// </summary>
public partial class FryPdfPageViewerViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private int _pageNumber = 1;

    [ObservableProperty]
    private PageFormat _format = PageFormat.A4;

    [ObservableProperty]
    private PageOrientation _orientation = PageOrientation.Portrait;

    [ObservableProperty]
    private int _rotationAngle = 0;

    [ObservableProperty]
    private double _width = 800;

    [ObservableProperty]
    private double _height = 1131;

    [ObservableProperty]
    private string _backgroundColorHex = "#FFFFFF";

    [ObservableProperty]
    private bool _showHeaderFooter = true;

    [ObservableProperty]
    private string? _headerLeft;

    [ObservableProperty]
    private string? _headerCenter;

    [ObservableProperty]
    private string? _headerRight;

    [ObservableProperty]
    private string? _footerLeft;

    [ObservableProperty]
    private string? _footerCenter;

    [ObservableProperty]
    private string? _footerRight;

    /// <summary>Unified collection of all presentation elements on this page in natural Z-index order.</summary>
    public ObservableCollection<ElementViewModelBase> Elements { get; } = new();

    /// <summary>Interactive living data tables with real-time row search, sorting, and CSV export.</summary>
    public IEnumerable<InteractiveTableViewModel> InteractiveTables => Elements.OfType<InteractiveTableViewModel>();

    /// <summary>Interactive animated charts with hover tooltips and dynamic animations.</summary>
    public IEnumerable<InteractiveChartViewModel> InteractiveCharts => Elements.OfType<InteractiveChartViewModel>();

    /// <summary>Standard visual elements (text, vector shapes, images, math formulas, svgs, barcodes, stamps).</summary>
    public IEnumerable<ElementViewModelBase> StandardElements => Elements.Where(e => e is not InteractiveTableViewModel && e is not InteractiveChartViewModel);

    public FryPdfPageViewerViewModel()
    {
    }

    public static FryPdfPageViewerViewModel FromPageModel(PdfPageModel pageModel)
    {
        var vm = new FryPdfPageViewerViewModel
        {
            Id = pageModel.Id,
            PageNumber = pageModel.PageNumber,
            Format = pageModel.Format,
            Orientation = pageModel.Orientation,
            RotationAngle = pageModel.RotationAngle,
            Width = pageModel.Width,
            Height = pageModel.Height,
            BackgroundColorHex = pageModel.BackgroundColorHex,
            ShowHeaderFooter = pageModel.ShowHeaderFooter,
            HeaderLeft = pageModel.HeaderLeft,
            HeaderCenter = pageModel.HeaderCenter,
            HeaderRight = pageModel.HeaderRight,
            FooterLeft = pageModel.FooterLeft,
            FooterCenter = pageModel.FooterCenter,
            FooterRight = pageModel.FooterRight
        };

        foreach (var el in pageModel.Elements)
        {
            if (el is PdfTableElement tableModel)
            {
                vm.Elements.Add(new InteractiveTableViewModel(tableModel));
            }
            else if (el is PdfChartElement chartModel)
            {
                vm.Elements.Add(new InteractiveChartViewModel(chartModel));
            }
            else
            {
                // Create standard view model using default service
                var standardVm = PageViewModel.DefaultElementService.CreateViewModel(el);
                // Ensure edit mode is false in viewer
                standardVm.IsSelected = false;
                standardVm.IsInEditMode = false;
                vm.Elements.Add(standardVm);
            }
        }

        return vm;
    }

    public static FryPdfPageViewerViewModel FromPageViewModel(PageViewModel pageVm)
    {
        var vm = new FryPdfPageViewerViewModel
        {
            Id = pageVm.Id,
            PageNumber = pageVm.PageNumber,
            Format = pageVm.Format,
            Orientation = pageVm.Orientation,
            RotationAngle = pageVm.RotationAngle,
            Width = pageVm.Width,
            Height = pageVm.Height,
            BackgroundColorHex = pageVm.BackgroundColorHex,
            ShowHeaderFooter = pageVm.ShowHeaderFooter,
            HeaderLeft = pageVm.HeaderLeft,
            HeaderCenter = pageVm.HeaderCenter,
            HeaderRight = pageVm.HeaderRight,
            FooterLeft = pageVm.FooterLeft,
            FooterCenter = pageVm.FooterCenter,
            FooterRight = pageVm.FooterRight
        };

        foreach (var el in pageVm.Elements)
        {
            if (el is TableElementViewModel tableVm)
            {
                vm.Elements.Add(new InteractiveTableViewModel(tableVm));
            }
            else if (el is ChartElementViewModel chartVm)
            {
                vm.Elements.Add(new InteractiveChartViewModel(chartVm));
            }
            else
            {
                // Ensure selection / edit mode is disabled in read-only presentation
                el.IsSelected = false;
                el.IsInEditMode = false;
                vm.Elements.Add(el);
            }
        }

        return vm;
    }

    /// <summary>
    /// Replays entrance animations on all interactive charts on this page.
    /// </summary>
    public void ReplayChartAnimations()
    {
        foreach (var chart in InteractiveCharts)
        {
            chart.ReplayAnimation();
        }
    }

    /// <summary>
    /// Serializes the presentation viewer page back into a standard <see cref="PdfPageModel"/>,
    /// harvesting all user-entered form values and interactive state.
    /// </summary>
    public PdfPageModel ToPageModel()
    {
        var model = new PdfPageModel
        {
            Id = Id,
            PageNumber = PageNumber,
            Format = Format,
            Orientation = Orientation,
            RotationAngle = RotationAngle,
            Width = Width,
            Height = Height,
            BackgroundColorHex = BackgroundColorHex,
            ShowHeaderFooter = ShowHeaderFooter,
            HeaderLeft = HeaderLeft,
            HeaderCenter = HeaderCenter,
            HeaderRight = HeaderRight,
            FooterLeft = FooterLeft,
            FooterCenter = FooterCenter,
            FooterRight = FooterRight
        };

        foreach (var el in Elements)
        {
            model.Elements.Add(el.ToModel());
        }

        return model;
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Core.Models;
using PdfEditorApp.Core.Models.Elements;
using PdfEditorApp.Models;
using PdfEditorApp.Services.Canvas;
using PdfEditorApp.ViewModels.ElementViewModels;

namespace PdfEditorApp.ViewModels;

public partial class PageViewModel : ViewModelBase
{
    private readonly ICanvasElementService _elementService;

    public PageViewModel(ICanvasElementService? elementService = null)
    {
        _elementService = elementService ?? DefaultElementService;
        Elements.CollectionChanged += OnElementsCollectionChanged;
    }

    private void OnElementsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<FormFieldElementViewModel>())
            {
                item.PropertyChanged -= OnFormFieldPropertyChanged;
            }
        }
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems.OfType<FormFieldElementViewModel>())
            {
                item.PropertyChanged += OnFormFieldPropertyChanged;
            }
        }
    }

    private void OnFormFieldPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FormFieldElementViewModel.Value) ||
            e.PropertyName == nameof(FormFieldElementViewModel.IsChecked))
        {
            RecalculateFormFields();
        }
    }

    private static ICanvasElementService? _defaultElementService;
    public static ICanvasElementService DefaultElementService
    {
        get => _defaultElementService ??= new CanvasElementRegistry();
        set => _defaultElementService = value;
    }

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
    private string? _footerLeft = "CONFIDENTIAL & PROPRIETARY";

    [ObservableProperty]
    private string? _footerCenter;

    [ObservableProperty]
    private string? _footerRight = "Page 1 of 1";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private ElementViewModelBase? _selectedElement;

    [ObservableProperty]
    private bool _hasMultiSelection;

    [ObservableProperty]
    private int _selectionCount;

    [ObservableProperty]
    private Rect _selectionBoundingBox;

    public ObservableCollection<ElementViewModelBase> Elements { get; } = new();

    public ObservableCollection<ElementViewModelBase> SelectedElements { get; } = new();

    public event Action<ElementViewModelBase?>? SelectionChanged;
    public event Action? MultiSelectionChanged;

    public void SelectElement(ElementViewModelBase? element)
    {
        SelectedElements.Clear();

        foreach (var el in Elements)
        {
            el.IsSelected = (el == element);
            if (!el.IsSelected) el.IsInEditMode = false;
        }

        if (element != null)
        {
            SelectedElements.Add(element);
        }

        SelectedElement = element;
        HasMultiSelection = false;
        SelectionCount = SelectedElements.Count;
        UpdateSelectionBoundingBox();

        SelectionChanged?.Invoke(element);
        MultiSelectionChanged?.Invoke();
    }

    public void ToggleElementSelection(ElementViewModelBase element)
    {
        if (SelectedElements.Contains(element))
        {
            element.IsSelected = false;
            element.IsInEditMode = false;
            SelectedElements.Remove(element);
        }
        else
        {
            element.IsSelected = true;
            SelectedElements.Add(element);
        }

        SelectedElement = SelectedElements.LastOrDefault();
        HasMultiSelection = SelectedElements.Count > 1;
        SelectionCount = SelectedElements.Count;
        UpdateSelectionBoundingBox();

        SelectionChanged?.Invoke(SelectedElement);
        MultiSelectionChanged?.Invoke();
    }

    /// <summary>
    /// Replaces the current selection with <paramref name="elements"/>.
    /// </summary>
    /// <remarks>
    /// Called from the marquee drag handler on every pointer move (~60 times a second), so it
    /// short-circuits when the selection has not actually changed, and only writes
    /// <c>IsSelected</c> on elements whose state differs — each write raises PropertyChanged,
    /// and it used to fire for every element on the page on every frame of the drag.
    /// </remarks>
    public void SelectElements(IEnumerable<ElementViewModelBase> elements)
    {
        var targetSet = new HashSet<ElementViewModelBase>(elements);

        if (targetSet.Count == SelectedElements.Count && SelectedElements.All(targetSet.Contains))
        {
            return;
        }

        SelectedElements.Clear();

        foreach (var el in Elements)
        {
            bool isSel = targetSet.Contains(el);
            if (el.IsSelected != isSel) el.IsSelected = isSel;
            if (!isSel && el.IsInEditMode) el.IsInEditMode = false;
            if (isSel) SelectedElements.Add(el);
        }

        SelectedElement = SelectedElements.LastOrDefault();
        HasMultiSelection = SelectedElements.Count > 1;
        SelectionCount = SelectedElements.Count;
        UpdateSelectionBoundingBox();

        SelectionChanged?.Invoke(SelectedElement);
        MultiSelectionChanged?.Invoke();
    }

    public void SelectAll()
    {
        SelectElements(Elements);
    }

    public void ClearSelection()
    {
        SelectElement(null);
    }

    public void UpdateSelectionBoundingBox()
    {
        if (SelectedElements.Count == 0)
        {
            SelectionBoundingBox = default;
            return;
        }

        // One pass instead of four separate LINQ enumerations. This runs on every pointer
        // move while dragging or resizing a selection.
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var el in SelectedElements)
        {
            if (el.X < minX) minX = el.X;
            if (el.Y < minY) minY = el.Y;

            double right = el.X + Math.Max(1, el.Width);
            double bottom = el.Y + Math.Max(1, el.Height);
            if (right > maxX) maxX = right;
            if (bottom > maxY) maxY = bottom;
        }

        SelectionBoundingBox = new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    public void AddElement(ElementViewModelBase element)
    {
        int maxZ = Elements.Count > 0 ? Elements.Max(e => e.ZIndex) + 1 : 1;
        element.ZIndex = maxZ;
        Elements.Add(element);
        SelectElement(element);
    }

    public void RemoveElement(ElementViewModelBase element)
    {
        if (SelectedElements.Contains(element))
        {
            SelectedElements.Remove(element);
            element.IsSelected = false;
        }

        if (SelectedElement == element)
        {
            SelectedElement = SelectedElements.LastOrDefault();
        }

        Elements.Remove(element);
        HasMultiSelection = SelectedElements.Count > 1;
        SelectionCount = SelectedElements.Count;
        UpdateSelectionBoundingBox();
        SelectionChanged?.Invoke(SelectedElement);
    }

    public void BringToFront(ElementViewModelBase element)
    {
        if (!Elements.Contains(element) || Elements.Count <= 1) return;
        int oldIndex = Elements.IndexOf(element);
        if (oldIndex != Elements.Count - 1)
        {
            Elements.Move(oldIndex, Elements.Count - 1);
        }
        NormalizeZIndices();
    }

    public void SendToBack(ElementViewModelBase element)
    {
        if (!Elements.Contains(element) || Elements.Count <= 1) return;
        int oldIndex = Elements.IndexOf(element);
        if (oldIndex != 0)
        {
            Elements.Move(oldIndex, 0);
        }
        NormalizeZIndices();
    }

    public void BringForward(ElementViewModelBase element)
    {
        if (!Elements.Contains(element) || Elements.Count <= 1) return;
        int oldIndex = Elements.IndexOf(element);
        if (oldIndex < Elements.Count - 1)
        {
            Elements.Move(oldIndex, oldIndex + 1);
            NormalizeZIndices();
        }
    }

    public void SendBackward(ElementViewModelBase element)
    {
        if (!Elements.Contains(element) || Elements.Count <= 1) return;
        int oldIndex = Elements.IndexOf(element);
        if (oldIndex > 0)
        {
            Elements.Move(oldIndex, oldIndex - 1);
            NormalizeZIndices();
        }
    }

    public void NormalizeZIndices()
    {
        for (int i = 0; i < Elements.Count; i++)
        {
            Elements[i].ZIndex = i + 1;
        }
    }

    public void GroupSelected()
    {
        if (SelectedElements.Count < 2) return;
        string newGroupId = Guid.NewGuid().ToString("N");
        foreach (var el in SelectedElements)
        {
            el.GroupId = newGroupId;
        }
    }

    public void UngroupSelected()
    {
        foreach (var el in SelectedElements)
        {
            el.GroupId = null;
        }
    }

    public void AlignSelectedLeft()
    {
        if (SelectedElements.Count < 2) return;
        double minX = SelectedElements.Min(e => e.X);
        foreach (var el in SelectedElements.Where(e => !e.IsLocked))
        {
            el.X = minX;
        }
        UpdateSelectionBoundingBox();
    }

    public void AlignSelectedRight()
    {
        if (SelectedElements.Count < 2) return;
        double maxRight = SelectedElements.Max(e => e.X + e.Width);
        foreach (var el in SelectedElements.Where(e => !e.IsLocked))
        {
            el.X = Math.Round(maxRight - el.Width, 1);
        }
        UpdateSelectionBoundingBox();
    }

    public void AlignSelectedCenter()
    {
        if (SelectedElements.Count < 2) return;
        double minX = SelectedElements.Min(e => e.X);
        double maxRight = SelectedElements.Max(e => e.X + e.Width);
        double midX = (minX + maxRight) / 2.0;

        foreach (var el in SelectedElements.Where(e => !e.IsLocked))
        {
            el.X = Math.Round(midX - (el.Width / 2.0), 1);
        }
        UpdateSelectionBoundingBox();
    }

    public void AlignSelectedTop()
    {
        if (SelectedElements.Count < 2) return;
        double minY = SelectedElements.Min(e => e.Y);
        foreach (var el in SelectedElements.Where(e => !e.IsLocked))
        {
            el.Y = minY;
        }
        UpdateSelectionBoundingBox();
    }

    public void AlignSelectedBottom()
    {
        if (SelectedElements.Count < 2) return;
        double maxBottom = SelectedElements.Max(e => e.Y + e.Height);
        foreach (var el in SelectedElements.Where(e => !e.IsLocked))
        {
            el.Y = Math.Round(maxBottom - el.Height, 1);
        }
        UpdateSelectionBoundingBox();
    }

    public void AlignSelectedMiddle()
    {
        if (SelectedElements.Count < 2) return;
        double minY = SelectedElements.Min(e => e.Y);
        double maxBottom = SelectedElements.Max(e => e.Y + e.Height);
        double midY = (minY + maxBottom) / 2.0;

        foreach (var el in SelectedElements.Where(e => !e.IsLocked))
        {
            el.Y = Math.Round(midY - (el.Height / 2.0), 1);
        }
        UpdateSelectionBoundingBox();
    }

    public void DistributeSelectedHorizontally()
    {
        if (SelectedElements.Count < 3) return;
        var ordered = SelectedElements.Where(e => !e.IsLocked).OrderBy(e => e.X).ToList();
        if (ordered.Count < 3) return;

        double startX = ordered.First().X;
        double endX = ordered.Last().X + ordered.Last().Width;
        double totalItemW = ordered.Sum(e => e.Width);
        double availableGap = endX - startX - totalItemW;
        double gapPerItem = Math.Max(0, availableGap / (ordered.Count - 1));

        double currentX = startX;
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].X = Math.Round(currentX, 1);
            currentX += ordered[i].Width + gapPerItem;
        }
        UpdateSelectionBoundingBox();
    }

    public void DistributeSelectedVertically()
    {
        if (SelectedElements.Count < 3) return;
        var ordered = SelectedElements.Where(e => !e.IsLocked).OrderBy(e => e.Y).ToList();
        if (ordered.Count < 3) return;

        double startY = ordered.First().Y;
        double endY = ordered.Last().Y + ordered.Last().Height;
        double totalItemH = ordered.Sum(e => e.Height);
        double availableGap = endY - startY - totalItemH;
        double gapPerItem = Math.Max(0, availableGap / (ordered.Count - 1));

        double currentY = startY;
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Y = Math.Round(currentY, 1);
            currentY += ordered[i].Height + gapPerItem;
        }
        UpdateSelectionBoundingBox();
    }

    [RelayCommand]
    public void RotateClockwise()
    {
        RotationAngle = (RotationAngle + 90) % 360;
    }

    public PdfPageModel ToModel()
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

    public void LoadFromModel(PdfPageModel model)
    {
        Id = model.Id;
        PageNumber = model.PageNumber;
        Format = model.Format;
        Orientation = model.Orientation;
        RotationAngle = model.RotationAngle;
        Width = model.Width;
        Height = model.Height;
        BackgroundColorHex = model.BackgroundColorHex;
        ShowHeaderFooter = model.ShowHeaderFooter;
        HeaderLeft = model.HeaderLeft;
        HeaderCenter = model.HeaderCenter;
        HeaderRight = model.HeaderRight;
        FooterLeft = model.FooterLeft;
        FooterCenter = model.FooterCenter;
        FooterRight = model.FooterRight;

        Elements.Clear();
        SelectedElements.Clear();
        SelectedElement = null;
        HasMultiSelection = false;
        SelectionCount = 0;

        foreach (var elModel in model.Elements)
        {
            ElementViewModelBase vm = _elementService.CreateViewModel(elModel);
            Elements.Add(vm);
        }
    }

    private bool _isRecalculating;

    public void RecalculateFormFields()
    {
        if (_isRecalculating) return;
        _isRecalculating = true;
        try
        {
            var formFields = Elements.OfType<FormFieldElementViewModel>().ToList();
            foreach (var field in formFields.Where(f => f.CalculationFormula != CalculationFormula.None))
            {
                if (string.IsNullOrWhiteSpace(field.CalculationSourceFields)) continue;
                var sourceNames = field.CalculationSourceFields
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                var values = formFields
                    .Where(f => sourceNames.Contains(f.FieldName, StringComparer.OrdinalIgnoreCase))
                    .Select(f => double.TryParse(f.Value, out var v) ? (double?)v : (double.TryParse(f.DefaultValue, out var dv) ? (double?)dv : null))
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .ToList();

                if (values.Count == 0) continue;

                double result = field.CalculationFormula switch
                {
                    CalculationFormula.Sum => values.Sum(),
                    CalculationFormula.Average => values.Average(),
                    CalculationFormula.Product => values.Aggregate(1.0, (acc, x) => acc * x),
                    CalculationFormula.Min => values.Min(),
                    CalculationFormula.Max => values.Max(),
                    _ => 0
                };

                field.Value = result.ToString("F2");
            }
        }
        finally
        {
            _isRecalculating = false;
        }
    }
}

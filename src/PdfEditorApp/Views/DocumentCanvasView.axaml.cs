using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PdfEditorApp.Core.Models;
using PdfEditorApp.Services.Typography;
using PdfEditorApp.ViewModels;
using PdfEditorApp.ViewModels.ElementViewModels;

namespace PdfEditorApp.Views;

public partial class DocumentCanvasView : UserControl
{
    private bool _isDraggingElement;
    private bool _isResizingHandle;
    private bool _isMarqueeSelecting;
    private Point _marqueeStartPoint;
    private DateTime _lastMarqueeSelectionUpdate = DateTime.MinValue;

    private DateTime _lastCursorPositionUpdate = DateTime.MinValue;

    /// <summary>One frame at 60 FPS; see the ruler note in OnCanvasPointerMoved.</summary>
    private const double CursorUpdateIntervalMs = 16;
    private string? _activeResizeHandle;
    private Point _lastPointerPosition;
    private ElementViewModelBase? _draggedElement;
    private List<ElementViewModelBase> _draggedElements = new();

    // Potential Drag Tracking (for reliable click vs drag detection)
    private bool _isPotentialDrag;
    private ElementViewModelBase? _potentialDragElement;
    private Point _potentialDragStartPos;
    private bool _wasAlreadySelectedOnPress;

    // In-place text editing baseline tracking
    private readonly Dictionary<string, string> _initialEditContents = new();

    // Movement & Resize Undo Tracking
    private double _dragStartElementX;
    private double _dragStartElementY;
    private List<(ElementViewModelBase Element, double X, double Y)> _dragStartPositions = new();
    private double _resizeStartX;
    private double _resizeStartY;
    private double _resizeStartW;
    private double _resizeStartH;

    // Pan state
    private bool _isSpacePressed;
    private bool _isPanning;
    private Point _panStart;
    private Vector _scrollStart;

    // Pinch Gesture state
    private bool _isPinching;
    private double _pinchStartZoom = 1.0;

    // Zoom commit throttle — wheel/trackpad/pinch events can fire far faster than the
    // LayoutTransformControl-driven canvas can afford to re-measure/re-arrange for. The delta
    // math below still runs on every raw event (so a fast flick still compounds correctly and
    // the cursor-anchor point stays accurate); only the actual write to ZoomLevel/Offset —
    // the part that triggers an expensive layout pass — is coalesced to roughly once per frame.
    private CancellationTokenSource? _zoomThrottleCts;
    private double _pendingZoomLevel;
    private Vector _pendingCanvasOffset;

    private double EffectiveZoomLevel => _zoomThrottleCts != null
        ? _pendingZoomLevel
        : (ViewModel != null && ViewModel.ZoomLevel > 0 ? ViewModel.ZoomLevel : 1.0);

    private Vector EffectiveCanvasOffset => _zoomThrottleCts != null
        ? _pendingCanvasOffset
        : (CanvasScrollViewer?.Offset ?? default);

    private void RequestZoomLevelChange(double newZoom, Vector newOffset)
    {
        if (ViewModel == null) return;

        _pendingZoomLevel = newZoom;
        _pendingCanvasOffset = newOffset;

        if (_zoomThrottleCts != null) return; // a commit is already scheduled; it will pick up these latest values

        _zoomThrottleCts = new CancellationTokenSource();
        var token = _zoomThrottleCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(16, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
            if (token.IsCancellationRequested) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() => CommitPendingZoom(token));
        }, token);
    }

    private void CommitPendingZoom(CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        _zoomThrottleCts?.Dispose();
        _zoomThrottleCts = null;
        var targetOffset = _pendingCanvasOffset;
        if (ViewModel != null) ViewModel.ZoomLevel = _pendingZoomLevel;
        if (CanvasScrollViewer != null)
        {
            CanvasScrollViewer.Offset = targetOffset;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (CanvasScrollViewer != null)
                {
                    CanvasScrollViewer.Offset = targetOffset;
                }
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>Applies the pending zoom immediately instead of waiting out the throttle —
    /// used when a gesture explicitly ends, so the canvas settles crisply.</summary>
    private void FlushPendingZoom()
    {
        if (_zoomThrottleCts == null) return;
        _zoomThrottleCts.Cancel();
        _zoomThrottleCts?.Dispose();
        _zoomThrottleCts = null;
        var targetOffset = _pendingCanvasOffset;
        if (ViewModel != null) ViewModel.ZoomLevel = _pendingZoomLevel;
        if (CanvasScrollViewer != null)
        {
            CanvasScrollViewer.Offset = targetOffset;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (CanvasScrollViewer != null)
                {
                    CanvasScrollViewer.Offset = targetOffset;
                }
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    public DocumentCanvasView()
    {
        InitializeComponent();

        GestureRecognizers.Add(new PinchGestureRecognizer());

        AddHandler(PointerMovedEvent, OnGlobalPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnGlobalPointerReleased, RoutingStrategies.Bubble);
        AddHandler(PointerWheelChangedEvent, OnCanvasPointerWheelChanged, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerTouchPadGestureMagnifyEvent, OnCanvasTouchPadGestureMagnify, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PinchEvent, OnCanvasPinch, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PinchEndedEvent, OnCanvasPinchEnded, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(DoubleTappedEvent, OnCanvasDoubleTapped, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnCanvasKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnCanvasKeyUp, RoutingStrategies.Tunnel);

        _activeTextFormattingHandler = textVm =>
        {
            if (ActiveInPlaceTextBox != null && ActiveInPlaceTextBox.DataContext == textVm)
            {
                ActiveInPlaceTextBox.Text = textVm.Text;
                ActiveInPlaceTextBox.SelectionStart = textVm.ActiveSelectionStart;
                ActiveInPlaceTextBox.SelectionEnd = textVm.ActiveSelectionStart + textVm.ActiveSelectionLength;
                ActiveInPlaceTextBox.Focus();
            }
        };

        InspectorViewModel.OnActiveTextFormattingApplied = _activeTextFormattingHandler;
    }

    /// <summary>
    /// The closure this view installed into the static
    /// <see cref="InspectorViewModel.OnActiveTextFormattingApplied"/> hook, kept so that
    /// detaching can clear it without stomping a newer view's handler.
    /// </summary>
    private readonly Action<TextElementViewModel> _activeTextFormattingHandler;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (CanvasScrollViewer != null)
        {
            // -= before += : this runs again on every re-attach, and ScrollViewer.PropertyChanged
            // fires continuously while scrolling — duplicated handlers multiplied the cost of
            // UpdateViewportOnPlacementService on every scroll tick.
            CanvasScrollViewer.PropertyChanged -= OnCanvasScrollViewerPropertyChanged;
            CanvasScrollViewer.PropertyChanged += OnCanvasScrollViewerPropertyChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (CanvasScrollViewer != null)
        {
            CanvasScrollViewer.PropertyChanged -= OnCanvasScrollViewerPropertyChanged;
        }

        // This view is rooted in a static field (InspectorViewModel.OnActiveTextFormattingApplied),
        // so release it when the editor is navigated away from rather than pinning the whole
        // visual tree — and MainViewModel through it — for the rest of the process.
        if (ReferenceEquals(InspectorViewModel.OnActiveTextFormattingApplied, _activeTextFormattingHandler))
        {
            InspectorViewModel.OnActiveTextFormattingApplied = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnCanvasScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.OffsetProperty || e.Property == ScrollViewer.ViewportProperty)
        {
            UpdateViewportOnPlacementService();
        }
    }

    private void UpdateViewportOnPlacementService()
    {
        if (CanvasScrollViewer == null || PageElementsCanvas == null || ViewModel?.CurrentPage == null) return;

        double zoom = ViewModel.ZoomLevel > 0 ? ViewModel.ZoomLevel : 1.0;
        var scrollCenter = new Point(CanvasScrollViewer.Viewport.Width / 2.0, CanvasScrollViewer.Viewport.Height / 2.0);
        var canvasCenter = CanvasScrollViewer.TranslatePoint(scrollCenter, PageElementsCanvas);

        if (canvasCenter.HasValue)
        {
            double pageCenterX = canvasCenter.Value.X / zoom;
            double pageCenterY = canvasCenter.Value.Y / zoom;
            double visibleWidth = CanvasScrollViewer.Viewport.Width / zoom;
            double visibleHeight = CanvasScrollViewer.Viewport.Height / zoom;

            ViewModel.SmartPlacement.UpdateViewport(pageCenterX, pageCenterY, visibleWidth, visibleHeight);
        }
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnCanvasPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // Support Cmd (Meta), Ctrl, and Option (Alt) modifier keys as well as Zoom tool mode
        bool isZoomModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                              e.KeyModifiers.HasFlag(KeyModifiers.Meta) ||
                              e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool isZoomTool = ViewModel?.ActiveToolMode == ToolMode.Zoom;

        if ((isZoomModifier || isZoomTool) && ViewModel != null && CanvasScrollViewer != null)
        {
            double oldZoom = EffectiveZoomLevel;
            double effectiveDelta = Math.Abs(e.Delta.Y) >= Math.Abs(e.Delta.X) ? e.Delta.Y : e.Delta.X;

            // Proportional smooth zoom supporting both discrete mouse wheel ticks and continuous trackpad pinch gestures
            double zoomDeltaFactor;
            if (Math.Abs(effectiveDelta) >= 1.0)
            {
                // Discrete mouse wheel tick (e.g. standard external wheel)
                zoomDeltaFactor = effectiveDelta > 0 ? 1.15 : (1.0 / 1.15);
            }
            else if (Math.Abs(effectiveDelta) > 0.0001)
            {
                // High-precision smooth trackpad pinch / scroll gesture
                zoomDeltaFactor = Math.Pow(1.002, effectiveDelta * 100);
            }
            else
            {
                zoomDeltaFactor = 1.0;
            }

            double newZoom = Math.Clamp(Math.Round(oldZoom * zoomDeltaFactor, 3), 0.1, 5.0);

            if (Math.Abs(newZoom - oldZoom) > 0.001)
            {
                var mouseInViewer = e.GetPosition(CanvasScrollViewer);
                double ratio = newZoom / oldZoom;
                var baseOffset = EffectiveCanvasOffset;
                double targetOffsetX = (baseOffset.X + mouseInViewer.X) * ratio - mouseInViewer.X;
                double targetOffsetY = (baseOffset.Y + mouseInViewer.Y) * ratio - mouseInViewer.Y;

                RequestZoomLevelChange(newZoom, new Vector(Math.Max(0, targetOffsetX), Math.Max(0, targetOffsetY)));
            }

            UpdateViewportOnPlacementService();
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && CanvasScrollViewer != null)
        {
            // Shift + Wheel -> Horizontal scroll
            double delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
            CanvasScrollViewer.Offset = new Vector(
                Math.Max(0, CanvasScrollViewer.Offset.X - (delta * 40)),
                CanvasScrollViewer.Offset.Y);
            e.Handled = true;
        }
    }

    private void OnCanvasTouchPadGestureMagnify(object? sender, PointerDeltaEventArgs e)
    {
        if (ViewModel != null && CanvasScrollViewer != null)
        {
            double oldZoom = EffectiveZoomLevel;
            double delta = Math.Abs(e.Delta.Y) >= Math.Abs(e.Delta.X) ? e.Delta.Y : e.Delta.X;
            if (delta == 0 && (e.Delta.X != 0 || e.Delta.Y != 0))
            {
                delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
            }

            double zoomFactor = 1.0 + delta;
            double newZoom = Math.Clamp(Math.Round(oldZoom * zoomFactor, 3), 0.1, 5.0);

            if (Math.Abs(newZoom - oldZoom) > 0.001)
            {
                var mouseInViewer = e.GetPosition(CanvasScrollViewer);
                double ratio = newZoom / oldZoom;
                var baseOffset = EffectiveCanvasOffset;
                double targetOffsetX = (baseOffset.X + mouseInViewer.X) * ratio - mouseInViewer.X;
                double targetOffsetY = (baseOffset.Y + mouseInViewer.Y) * ratio - mouseInViewer.Y;

                RequestZoomLevelChange(newZoom, new Vector(Math.Max(0, targetOffsetX), Math.Max(0, targetOffsetY)));
            }

            UpdateViewportOnPlacementService();
            e.Handled = true;
        }
    }

    private void OnCanvasPinch(object? sender, PinchEventArgs e)
    {
        if (ViewModel != null && CanvasScrollViewer != null)
        {
            if (!_isPinching)
            {
                _isPinching = true;
                _pinchStartZoom = ViewModel.ZoomLevel > 0 ? ViewModel.ZoomLevel : 1.0;
            }

            // e.Scale is the total cumulative scale of the pinch gesture since start (starts at 1.0)
            double targetZoom = Math.Clamp(Math.Round(_pinchStartZoom * e.Scale, 3), 0.1, 5.0);
            double oldZ = EffectiveZoomLevel;
            if (Math.Abs(targetZoom - oldZ) > 0.002 && oldZ > 0)
            {
                var origin = e.ScaleOrigin;
                double ratio = targetZoom / oldZ;
                var baseOffset = EffectiveCanvasOffset;
                double newOffsetX = (baseOffset.X + origin.X) * ratio - origin.X;
                double newOffsetY = (baseOffset.Y + origin.Y) * ratio - origin.Y;
                RequestZoomLevelChange(targetZoom, new Vector(Math.Max(0, newOffsetX), Math.Max(0, newOffsetY)));
            }
            e.Handled = true;
        }
    }

    private void OnCanvasPinchEnded(object? sender, PinchEndedEventArgs e)
    {
        _isPinching = false;
        FlushPendingZoom();
        UpdateViewportOnPlacementService();
        e.Handled = true;
    }

    private void OnCanvasDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel == null || CanvasScrollViewer == null) return;

        if (ViewModel.ActiveToolMode == ToolMode.Pan || _isSpacePressed)
        {
            ViewModel.FitToPageDynamic(CanvasScrollViewer.Viewport.Width, CanvasScrollViewer.Viewport.Height);
            e.Handled = true;
            return;
        }
        else if (ViewModel.ActiveToolMode == ToolMode.Zoom)
        {
            ViewModel.ResetZoom();
            e.Handled = true;
            return;
        }

        // Ignore double-taps originating inside overlays (CanvasTextHudView, FindReplaceBarView)
        if (e.Source is Visual sourceVisual && (sourceVisual.FindAncestorOfType<CanvasTextHudView>() != null || sourceVisual.FindAncestorOfType<FindReplaceBarView>() != null))
        {
            return;
        }

        // Direct in-place editing trigger on double-click
        if (ViewModel.CurrentPage == null || PageElementsCanvas == null) return;

        // 1. Check if the double-tap happened directly on an element visual or its ancestor
        ElementViewModelBase? targetElement = null;
        if (e.Source is Visual visual)
        {
            targetElement = (visual as Control)?.DataContext as ElementViewModelBase ??
                            visual.FindAncestorOfType<Control>()?.DataContext as ElementViewModelBase;
        }

        // 2. If not found from visual source, check geometric coordinates on the canvas
        if (targetElement == null)
        {
            var canvasPos = e.GetPosition(PageElementsCanvas);
            double zoom = ViewModel.ZoomLevel > 0 ? ViewModel.ZoomLevel : 1.0;
            double docX = canvasPos.X / zoom;
            double docY = canvasPos.Y / zoom;

            targetElement = ViewModel.CurrentPage.Elements
                .Where(el => !el.IsLocked && docX >= el.X && docX <= el.X + el.Width && docY >= el.Y && docY <= el.Y + el.Height)
                .OrderByDescending(el => el.ZIndex)
                .FirstOrDefault();
        }

        if (targetElement == null)
        {
            targetElement = ViewModel.CurrentPage.SelectedElement;
        }

        if (targetElement != null && !targetElement.IsLocked)
        {
            ViewModel.CurrentPage.SelectElement(targetElement);

            if (targetElement is TextElementViewModel textVm)
            {
                textVm.IsInEditMode = true;
                e.Handled = true;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    FocusInPlaceTextBox(textVm);
                }, Avalonia.Threading.DispatcherPriority.Input);
            }
            else if (targetElement is ShapeElementViewModel shapeVm)
            {
                shapeVm.IsInEditMode = true;
                e.Handled = true;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    FocusInPlaceTextBox(shapeVm);
                }, Avalonia.Threading.DispatcherPriority.Input);
            }
            else if (targetElement is StickyNoteElementViewModel stickyVm)
            {
                stickyVm.IsInEditMode = true;
                e.Handled = true;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    FocusInPlaceTextBox(stickyVm);
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }
            else if (targetElement is MathElementViewModel mathVm)
            {
                mathVm.IsInEditMode = true;
                e.Handled = true;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    FocusInPlaceTextBox(mathVm);
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }
            else if (targetElement is FormFieldElementViewModel formVm)
            {
                formVm.IsInEditMode = true;
                e.Handled = true;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    FocusInPlaceTextBox(formVm);
                }, Avalonia.Threading.DispatcherPriority.Input);
            }
        }
    }

    private void OnCanvasBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel?.CurrentPage == null || PageElementsCanvas == null) return;
        Focus();

        var pointerPoint = e.GetCurrentPoint(this);

        // Middle button click, Spacebar held, or Pan Tool mode => Pan mode
        if (_isSpacePressed || pointerPoint.Properties.IsMiddleButtonPressed || ViewModel.ActiveToolMode == ToolMode.Pan)
        {
            _isPanning = true;
            _panStart = e.GetPosition(this);
            if (CanvasScrollViewer != null)
            {
                _scrollStart = new Vector(CanvasScrollViewer.Offset.X, CanvasScrollViewer.Offset.Y);
            }
            Cursor = new Cursor(StandardCursorType.Hand);
            e.Handled = true;
            return;
        }

        // Zoom Tool mode => Click to Zoom In / Alt-Click to Zoom Out
        if (ViewModel.ActiveToolMode == ToolMode.Zoom)
        {
            if (pointerPoint.Properties.IsLeftButtonPressed)
            {
                bool isAlt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                if (isAlt)
                {
                    ViewModel.ZoomOut();
                }
                else
                {
                    ViewModel.ZoomIn();
                }
                UpdateViewportOnPlacementService();
                e.Handled = true;
                return;
            }
        }

        var pos = e.GetPosition(PageElementsCanvas);
        double zoom = ViewModel.ZoomLevel > 0 ? ViewModel.ZoomLevel : 1.0;
        double canvasX = Math.Max(0, pos.X / zoom);
        double canvasY = Math.Max(0, pos.Y / zoom);

        ViewModel.LastCanvasClickPoint = (canvasX, canvasY);

        // Register right-click point for context-menu placement and AI Studio point targeting
        if (pointerPoint.Properties.IsRightButtonPressed)
        {
            ViewModel.SmartPlacement.SetContextMenuPointer(canvasX, canvasY);
        }
        else if (ViewModel.AiAssistant.IsPointTargetMode)
        {
            ViewModel.AiAssistant.TargetPointOnPage(canvasX, canvasY, ViewModel.CurrentPage);
        }

        if (ViewModel.ActiveToolMode == ToolMode.Draw || ViewModel.ActiveToolMode == ToolMode.Highlight)
        {
            bool isHighlighter = ViewModel.ActiveToolMode == ToolMode.Highlight;
            var ink = new InkElementViewModel
            {
                X = Math.Max(0, canvasX),
                Y = Math.Max(0, canvasY),
                Width = 20,
                Height = isHighlighter ? 18 : 6,
                StrokeColorHex = isHighlighter ? "#FEF08A" : "#0F6CBD",
                StrokeThickness = isHighlighter ? 14.0 : 3.0,
                Opacity = isHighlighter ? 0.45 : 1.0,
                IsHighlighter = isHighlighter
            };

            ViewModel.AddElementWithUndo(ink, isHighlighter ? "Added Highlighter Stroke" : "Added Ink Stroke");
            _isResizingHandle = true;
            _activeResizeHandle = "bottomright";
            _draggedElement = ink;
            _resizeStartX = ink.X;
            _resizeStartY = ink.Y;
            _resizeStartW = ink.Width;
            _resizeStartH = ink.Height;
            _lastPointerPosition = pos;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Marquee Selection Drag on background
        bool isToggle = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!isToggle)
        {
            ViewModel.CurrentPage.ClearSelection();
        }

        if (pointerPoint.Properties.IsLeftButtonPressed && e.Pointer.Type != PointerType.Touch)
        {
            _isMarqueeSelecting = true;
            _marqueeStartPoint = pos;
            if (MarqueeSelectionBox != null)
            {
                Canvas.SetLeft(MarqueeSelectionBox, pos.X);
                Canvas.SetTop(MarqueeSelectionBox, pos.Y);
                MarqueeSelectionBox.Width = 0;
                MarqueeSelectionBox.Height = 0;
                MarqueeSelectionBox.IsVisible = true;
            }
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    private void OnElementPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isSpacePressed || e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _panStart = e.GetPosition(this);
            if (CanvasScrollViewer != null)
            {
                _scrollStart = new Vector(CanvasScrollViewer.Offset.X, CanvasScrollViewer.Offset.Y);
            }
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (sender is Control control && control.DataContext is ElementViewModelBase elementVm)
        {
            // If the clicked element is locked (e.g. background watermark or full-page frame),
            // check if there is an unlocked element under the cursor to select and edit instead.
            if (elementVm.IsLocked && PageElementsCanvas != null && ViewModel?.CurrentPage != null)
            {
                var canvasPos = e.GetPosition(PageElementsCanvas);
                double zoom = ViewModel.ZoomLevel > 0 ? ViewModel.ZoomLevel : 1.0;
                double docX = canvasPos.X / zoom;
                double docY = canvasPos.Y / zoom;

                var topUnlocked = ViewModel.CurrentPage.Elements
                    .Where(el => !el.IsLocked && docX >= el.X && docX <= el.X + el.Width && docY >= el.Y && docY <= el.Y + el.Height)
                    .OrderByDescending(el => el.ZIndex)
                    .FirstOrDefault();

                if (topUnlocked != null)
                {
                    elementVm = topUnlocked;
                }
            }

            bool wasAlreadySelected = elementVm.IsSelected;
            bool isToggle = e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

            if (isToggle)
            {
                ViewModel?.CurrentPage?.ToggleElementSelection(elementVm);
            }
            else if (!elementVm.IsSelected)
            {
                ViewModel?.CurrentPage?.SelectElement(elementVm);
            }

            if (!elementVm.IsInEditMode)
            {
                Focus();
            }

            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed && PageElementsCanvas != null && ViewModel != null)
            {
                var pos = e.GetPosition(PageElementsCanvas);
                double zoom = ViewModel.ZoomLevel > 0 ? ViewModel.ZoomLevel : 1.0;
                ViewModel.SmartPlacement.SetContextMenuPointer(pos.X / zoom, pos.Y / zoom);
            }

            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && ViewModel?.CurrentPage != null && !elementVm.IsLocked)
            {
                if (elementVm.IsInEditMode)
                {
                    return;
                }

                // If user clicked directly on an interactive control inside a form field (CheckBox, TextBox, ComboBox, DatePicker, etc.),
                // allow the native control to receive focus and interaction instead of capturing drag!
                if (elementVm is FormFieldElementViewModel && e.Source is Visual visual)
                {
                    bool isInteractiveChild = visual is TextBox || visual.FindAncestorOfType<TextBox>() != null ||
                                             visual is CheckBox || visual.FindAncestorOfType<CheckBox>() != null ||
                                             visual is RadioButton || visual.FindAncestorOfType<RadioButton>() != null ||
                                             visual is ComboBox || visual.FindAncestorOfType<ComboBox>() != null ||
                                             visual is CalendarDatePicker || visual.FindAncestorOfType<CalendarDatePicker>() != null;

                    if (isInteractiveChild)
                    {
                        elementVm.IsInEditMode = true;
                        return;
                    }
                }

                _isPotentialDrag = true;
                _potentialDragElement = elementVm;
                _potentialDragStartPos = e.GetPosition(PageElementsCanvas);
                _wasAlreadySelectedOnPress = wasAlreadySelected;
                _lastPointerPosition = _potentialDragStartPos;
                e.Handled = true;
            }
        }
    }

    private void OnHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.Tag is string handleName && control.DataContext is ElementViewModelBase elementVm)
        {
            Focus();
            _isResizingHandle = true;
            _activeResizeHandle = handleName;
            _draggedElement = elementVm;
            _resizeStartX = elementVm.X;
            _resizeStartY = elementVm.Y;
            _resizeStartW = elementVm.Width;
            _resizeStartH = elementVm.Height;
            _lastPointerPosition = e.GetPosition(PageElementsCanvas);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    private void FocusInPlaceTextBox(ElementViewModelBase elementVm)
    {
        if (PageElementsCanvas == null) return;

        var textBox = PageElementsCanvas.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(tb => tb.DataContext == elementVm && tb.IsVisible);

        if (textBox != null)
        {
            textBox.Focus();
            textBox.CaretIndex = textBox.Text?.Length ?? 0;
            textBox.SelectAll();
        }
    }

    public static TextBox? ActiveInPlaceTextBox { get; private set; }

    private static void UpdateSelectionFromTextBox(TextBox textBox)
    {
        if (textBox.DataContext is TextElementViewModel textVm)
        {
            int start = Math.Min(textBox.SelectionStart, textBox.SelectionEnd);
            int end = Math.Max(textBox.SelectionStart, textBox.SelectionEnd);
            int len = end - start;

            // If TextBox temporarily lost focus and reports 0 length while ViewModel already has a selection,
            // preserve the selection so toolbar/HUD commands can format it!
            if (!textBox.IsFocused && len == 0 && textVm.HasTextSelection)
            {
                return;
            }

            string text = textBox.Text ?? "";
            string sel = len > 0 && start + len <= text.Length
                ? text.Substring(start, len)
                : string.Empty;

            textVm.UpdateTextSelection(start, len, sel);
        }
    }

    /// <summary>
    /// Wires the in-place editing TextBox created by the canvas DataTemplates.
    /// </summary>
    /// <remarks>
    /// This fires again every time a container is recycled, the page changes, or edit mode is
    /// toggled — and it is wired from three separate DataTemplates. The handlers were
    /// anonymous lambdas, which can never be removed, so every re-attach added five more.
    /// Since TextBox.PropertyChanged fires for *every* property, after K attachments a single
    /// keystroke ran UpdateSelectionFromTextBox K times. Named handlers plus "-= before +="
    /// keep exactly one subscription no matter how often this runs.
    /// </remarks>
    private void OnInPlaceTextBoxAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        SubscribeInPlaceTextBox(textBox);

        if (textBox.IsVisible)
        {
            BeginInPlaceEditing(textBox);
        }
    }

    private void SubscribeInPlaceTextBox(TextBox textBox)
    {
        textBox.GotFocus -= OnInPlaceTextBoxGotFocus;
        textBox.GotFocus += OnInPlaceTextBoxGotFocus;

        textBox.LostFocus -= OnInPlaceTextBoxLostFocus;
        textBox.LostFocus += OnInPlaceTextBoxLostFocus;

        textBox.PropertyChanged -= OnInPlaceTextBoxPropertyChanged;
        textBox.PropertyChanged += OnInPlaceTextBoxPropertyChanged;

        textBox.PointerReleased -= OnInPlaceTextBoxPointerReleased;
        textBox.PointerReleased += OnInPlaceTextBoxPointerReleased;

        textBox.KeyUp -= OnInPlaceTextBoxKeyUp;
        textBox.KeyUp += OnInPlaceTextBoxKeyUp;

        textBox.DetachedFromVisualTree -= OnInPlaceTextBoxDetached;
        textBox.DetachedFromVisualTree += OnInPlaceTextBoxDetached;
    }

    /// <summary>
    /// Releases the handlers wired by <see cref="SubscribeInPlaceTextBox"/> once the TextBox
    /// leaves the visual tree.
    /// </summary>
    private void OnInPlaceTextBoxDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        textBox.GotFocus -= OnInPlaceTextBoxGotFocus;
        textBox.LostFocus -= OnInPlaceTextBoxLostFocus;
        textBox.PropertyChanged -= OnInPlaceTextBoxPropertyChanged;
        textBox.PointerReleased -= OnInPlaceTextBoxPointerReleased;
        textBox.KeyUp -= OnInPlaceTextBoxKeyUp;
        textBox.DetachedFromVisualTree -= OnInPlaceTextBoxDetached;
    }

    /// <summary>Focuses the TextBox and selects its text, recording the pre-edit contents.</summary>
    private void BeginInPlaceEditing(TextBox textBox)
    {
        ActiveInPlaceTextBox = textBox;
        if (textBox.DataContext is ElementViewModelBase el)
        {
            _initialEditContents[el.Id] = textBox.Text ?? "";
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            textBox.Focus();
            textBox.CaretIndex = 0;
            textBox.SelectAll();
            UpdateSelectionFromTextBox(textBox);
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    private void OnInPlaceTextBoxGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        ActiveInPlaceTextBox = textBox;
        UpdateSelectionFromTextBox(textBox);
        if (textBox.DataContext is ElementViewModelBase el)
        {
            _initialEditContents[el.Id] = textBox.Text ?? "";
        }
    }

    private void OnInPlaceTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        if (textBox.DataContext is TextElementViewModel textVm && textVm.IsInEditMode)
        {
            // User is still in edit mode (e.g. interacting with HUD, Ribbon, or Sidebar).
            // Keep ActiveInPlaceTextBox assigned so inline formatting commands continue working!
            return;
        }

        if (ActiveInPlaceTextBox == textBox)
        {
            ActiveInPlaceTextBox = null;
        }
    }

    private void OnInPlaceTextBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        if (e.Property == Visual.IsVisibleProperty && textBox.IsVisible)
        {
            BeginInPlaceEditing(textBox);
        }
        else if (e.Property == TextBox.SelectionStartProperty ||
                 e.Property == TextBox.SelectionEndProperty ||
                 e.Property == TextBox.TextProperty)
        {
            UpdateSelectionFromTextBox(textBox);
        }
    }

    private void OnInPlaceTextBoxPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is TextBox textBox) UpdateSelectionFromTextBox(textBox);
    }

    private void OnInPlaceTextBoxKeyUp(object? sender, KeyEventArgs e)
    {
        if (sender is TextBox textBox) UpdateSelectionFromTextBox(textBox);
    }

    private void OnTextElementDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control control && control.DataContext is TextElementViewModel textVm)
        {
            if (textVm.IsLocked) return;

            textVm.AutoFitHeight();
            if (textVm.Spans != null && textVm.Spans.Count > 0)
            {
                textVm.Text = textVm.GetMarkdownText();
            }
            else
            {
                textVm.Text = RichTextHelper.StripExtendedTags(textVm.Text);
            }

            textVm.IsInEditMode = true;
            e.Handled = true;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                FocusInPlaceTextBox(textVm);
            }, Avalonia.Threading.DispatcherPriority.Input);
        }
    }

    private void OnTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && control.DataContext is TextElementViewModel textVm)
        {
            // If the element is still in edit mode (e.g. interacting with HUD, Ribbon, or Sidebar),
            // DO NOT exit edit mode or wipe spans!
            if (textVm.IsInEditMode)
            {
                return;
            }

            _initialEditContents.TryGetValue(textVm.Id, out var oldTxt);
            string finalPlain = textVm.Text ?? "";
            var finalSpans = textVm.Spans?.Select(s => s.Clone()).ToList();

            if (finalPlain != oldTxt)
            {
                _initialEditContents[textVm.Id] = finalPlain;
                var savedSpans = finalSpans;
                ViewModel?.UndoRedo.RecordAction(
                    "Edit Text",
                    () => {
                        textVm.Text = oldTxt ?? "";
                        textVm.Spans = null;
                    },
                    () => {
                        textVm.Text = finalPlain;
                        textVm.Spans = savedSpans != null ? savedSpans.Select(s => s.Clone()).ToList() : null;
                    }
                );
            }
        }
    }

    private void OnShapeElementDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control control && control.DataContext is ShapeElementViewModel shapeVm)
        {
            shapeVm.IsInEditMode = true;
            e.Handled = true;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                FocusInPlaceTextBox(shapeVm);
            }, Avalonia.Threading.DispatcherPriority.Input);
        }
    }

    private void OnShapeTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && control.DataContext is ShapeElementViewModel shapeVm)
        {
            string currentLabel = shapeVm.Label ?? "";
            if (_initialEditContents.TryGetValue(shapeVm.Id, out var oldTxt) && currentLabel != oldTxt)
            {
                _initialEditContents[shapeVm.Id] = currentLabel;
                ViewModel?.UndoRedo.RecordAction(
                    "Edit Shape Label",
                    () => shapeVm.Label = oldTxt,
                    () => shapeVm.Label = currentLabel
                );
            }
        }
    }

    private void OnTableCellLostFocus(object? sender, RoutedEventArgs e)
    {
        // Table cells have two-way binding directly to TableHeaderItem/TableCellItem
    }

    private void OnStickyNoteTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        // Sticky note text has two-way binding directly to NoteText
    }

    private void OnMathElementDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control control && control.DataContext is MathElementViewModel mathVm)
        {
            ViewModel?.OpenMathStudioCommand.Execute(mathVm);
            e.Handled = true;
        }
    }

    private void OnMathTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && control.DataContext is MathElementViewModel mathVm)
        {
            if (_initialEditContents.TryGetValue(mathVm.Id, out var oldFormula) && mathVm.Formula != oldFormula)
            {
                string newFormula = mathVm.Formula;
                _initialEditContents[mathVm.Id] = newFormula;
                ViewModel?.UndoRedo.RecordAction(
                    "Edit Formula",
                    () => { mathVm.Formula = oldFormula; mathVm.RenderSvg(); },
                    () => { mathVm.Formula = newFormula; mathVm.RenderSvg(); }
                );
            }
            mathVm.IsInEditMode = false;
            mathVm.RenderSvg();
        }
    }

    private void OnGlobalPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isPanning && CanvasScrollViewer != null)
        {
            var curPos = e.GetPosition(this);
            var delta = curPos - _panStart;
            CanvasScrollViewer.Offset = new Vector(
                Math.Max(0, _scrollStart.X - delta.X),
                Math.Max(0, _scrollStart.Y - delta.Y));
            e.Handled = true;
            return;
        }

        if (PageElementsCanvas != null && ViewModel != null)
        {
            // CursorCanvasX/Y drive CanvasRulerControl.CursorPosition, which is in that
            // control's AffectsRender set — so each write re-renders *both* rulers, and each
            // ruler render walks a tick loop. This fired on every raw pointer move, even when
            // nothing was being dragged, and a trackpad reports far faster than the 60 FPS the
            // frame budget in section 6 of the performance mandate allows for. Coalesced to one
            // update per frame, matching the marquee throttle below.
            var now = DateTime.UtcNow;
            if ((now - _lastCursorPositionUpdate).TotalMilliseconds >= CursorUpdateIntervalMs)
            {
                _lastCursorPositionUpdate = now;

                var pPos = e.GetPosition(PageElementsCanvas);
                double z = ViewModel.ZoomLevel > 0 ? ViewModel.ZoomLevel : 1.0;

                // Rounded to a tenth of a point: the rulers cannot show more precision than
                // that, and an unchanged value skips the property write entirely, so hovering
                // almost-still costs no renders at all.
                double cursorX = Math.Round(Math.Max(0, pPos.X / z), 1);
                double cursorY = Math.Round(Math.Max(0, pPos.Y / z), 1);

                if (ViewModel.CursorCanvasX != cursorX) ViewModel.CursorCanvasX = cursorX;
                if (ViewModel.CursorCanvasY != cursorY) ViewModel.CursorCanvasY = cursorY;
            }
        }

        if (_isMarqueeSelecting && PageElementsCanvas != null && ViewModel?.CurrentPage != null)
        {
            var curPos = e.GetPosition(PageElementsCanvas);
            double minX = Math.Min(_marqueeStartPoint.X, curPos.X);
            double minY = Math.Min(_marqueeStartPoint.Y, curPos.Y);
            double w = Math.Abs(curPos.X - _marqueeStartPoint.X);
            double h = Math.Abs(curPos.Y - _marqueeStartPoint.Y);

            // The box itself tracks the cursor on every move (cheap, needs to feel precise).
            // Recomputing which elements intersect it is an O(n) scan over the whole page
            // followed by another O(n) pass inside SelectElements, so that part is throttled —
            // OnGlobalPointerReleased always does one final, unthrottled pass so the committed
            // selection is never stale.
            if (MarqueeSelectionBox != null)
            {
                Canvas.SetLeft(MarqueeSelectionBox, minX);
                Canvas.SetTop(MarqueeSelectionBox, minY);
                MarqueeSelectionBox.Width = w;
                MarqueeSelectionBox.Height = h;
            }

            var now = DateTime.UtcNow;
            if ((now - _lastMarqueeSelectionUpdate).TotalMilliseconds >= 16)
            {
                _lastMarqueeSelectionUpdate = now;
                UpdateMarqueeSelection(minX, minY, w, h);
            }

            e.Handled = true;
            return;
        }

        if (_isPotentialDrag && !_isDraggingElement && PageElementsCanvas != null && ViewModel?.CurrentPage != null)
        {
            var curPos = e.GetPosition(PageElementsCanvas);
            double distSq = Math.Pow(curPos.X - _potentialDragStartPos.X, 2) + Math.Pow(curPos.Y - _potentialDragStartPos.Y, 2);
            if (distSq > 9.0) // 3px drag threshold
            {
                _isDraggingElement = true;
                _draggedElement = _potentialDragElement;
                _draggedElements = ViewModel.CurrentPage.SelectedElements.Where(el => !el.IsLocked).ToList();
                if (_draggedElement != null && !_draggedElements.Contains(_draggedElement)) _draggedElements.Add(_draggedElement);

                if (_draggedElement != null && !string.IsNullOrEmpty(_draggedElement.GroupId))
                {
                    var groupMembers = ViewModel.CurrentPage.Elements.Where(el => el.GroupId == _draggedElement.GroupId && !el.IsLocked);
                    foreach (var gm in groupMembers)
                    {
                        if (!_draggedElements.Contains(gm)) _draggedElements.Add(gm);
                    }
                }

                _dragStartPositions = _draggedElements.Select(el => (Element: el, el.X, el.Y)).ToList();
                if (_draggedElement != null)
                {
                    _dragStartElementX = _draggedElement.X;
                    _dragStartElementY = _draggedElement.Y;
                }
                _lastPointerPosition = curPos;
                e.Pointer.Capture(this);
            }
        }

        if (PageElementsCanvas == null || ViewModel?.CurrentPage == null) return;
        if (!_isResizingHandle && !_isDraggingElement) return;

        var currentPos = e.GetPosition(PageElementsCanvas);
        double deltaX = currentPos.X - _lastPointerPosition.X;
        double deltaY = currentPos.Y - _lastPointerPosition.Y;

        double zoom = ViewModel.ZoomLevel > 0 ? ViewModel.ZoomLevel : 1.0;
        deltaX /= zoom;
        deltaY /= zoom;

        if (_isResizingHandle && _draggedElement != null && !string.IsNullOrEmpty(_activeResizeHandle))
        {
            _draggedElement.Resize(_activeResizeHandle, deltaX, deltaY);
            _lastPointerPosition = currentPos;
            ViewModel.CurrentPage.UpdateSelectionBoundingBox();
        }
        else if (_isDraggingElement && _draggedElements.Count > 0)
        {
            foreach (var el in _draggedElements)
            {
                el.MoveBy(deltaX, deltaY, ViewModel.CurrentPage.Width, ViewModel.CurrentPage.Height);
            }
            _lastPointerPosition = currentPos;
            ViewModel.CurrentPage.UpdateSelectionBoundingBox();
        }
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        // Handled by tunnel/bubble or direct event
    }

    private void UpdateMarqueeSelection(double boxLeft, double boxTop, double boxWidth, double boxHeight)
    {
        if (ViewModel?.CurrentPage == null) return;

        double z = ViewModel.ZoomLevel > 0 ? ViewModel.ZoomLevel : 1.0;
        var marqueeRect = new Rect(boxLeft / z, boxTop / z, Math.Max(1, boxWidth / z), Math.Max(1, boxHeight / z));

        var intersecting = ViewModel.CurrentPage.Elements
            .Where(el => marqueeRect.Intersects(new Rect(el.X, el.Y, Math.Max(1, el.Width), Math.Max(1, el.Height))))
            .ToList();

        ViewModel.CurrentPage.SelectElements(intersecting);
    }

    private void OnGlobalPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPotentialDrag && !_isDraggingElement && !_isResizingHandle && !_isMarqueeSelecting)
        {
            // Click without drag occurred!
            if (_wasAlreadySelectedOnPress && _potentialDragElement != null && !_potentialDragElement.IsInEditMode)
            {
                // Second click on already selected element enters in-place edit mode!
                if (_potentialDragElement is TextElementViewModel textVm)
                {
                    textVm.IsInEditMode = true;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => FocusInPlaceTextBox(textVm), Avalonia.Threading.DispatcherPriority.Input);
                }
                else if (_potentialDragElement is ShapeElementViewModel shapeVm)
                {
                    shapeVm.IsInEditMode = true;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => FocusInPlaceTextBox(shapeVm), Avalonia.Threading.DispatcherPriority.Input);
                }
                else if (_potentialDragElement is FormFieldElementViewModel formVm)
                {
                    formVm.IsInEditMode = true;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => FocusInPlaceTextBox(formVm), Avalonia.Threading.DispatcherPriority.Input);
                }
            }
        }
        _isPotentialDrag = false;
        _potentialDragElement = null;

        if (_isMarqueeSelecting)
        {
            // Final, unthrottled pass — guarantees the committed selection matches exactly
            // where the box ended up, regardless of the move handler's throttle timing. Only
            // for an actual drag: a plain click (no move at all, box still at its 0x0 press
            // size) must leave selection untouched, matching the previous behavior — otherwise
            // a shift/ctrl-click with no drag would wipe out the existing selection.
            if (MarqueeSelectionBox != null)
            {
                if (MarqueeSelectionBox.Width > 2 || MarqueeSelectionBox.Height > 2)
                {
                    UpdateMarqueeSelection(Canvas.GetLeft(MarqueeSelectionBox), Canvas.GetTop(MarqueeSelectionBox), MarqueeSelectionBox.Width, MarqueeSelectionBox.Height);
                }
                MarqueeSelectionBox.IsVisible = false;
            }
            _isMarqueeSelecting = false;
        }
        else if (_isResizingHandle && _draggedElement != null)
        {
            var el = _draggedElement;
            if (ViewModel != null && ViewModel.SnapToGrid)
            {
                double snap = (double)(int)ViewModel.GridSnapSize;
                if (snap > 1)
                {
                    el.X = Math.Round(el.X / snap) * snap;
                    el.Y = Math.Round(el.Y / snap) * snap;
                    el.Width = Math.Max(snap, Math.Round(el.Width / snap) * snap);
                    el.Height = Math.Max(snap, Math.Round(el.Height / snap) * snap);
                }
            }

            double fromX = _resizeStartX, fromY = _resizeStartY, fromW = _resizeStartW, fromH = _resizeStartH;
            double toX = el.X, toY = el.Y, toW = el.Width, toH = el.Height;
            if (Math.Abs(toW - fromW) > 0.5 || Math.Abs(toH - fromH) > 0.5 || Math.Abs(toX - fromX) > 0.5 || Math.Abs(toY - fromY) > 0.5)
            {
                ViewModel?.UndoRedo.RecordAction(
                    $"Resize {el.DisplayName}",
                    () => { el.X = fromX; el.Y = fromY; el.Width = fromW; el.Height = fromH; ViewModel?.CurrentPage?.UpdateSelectionBoundingBox(); },
                    () => { el.X = toX; el.Y = toY; el.Width = toW; el.Height = toH; ViewModel?.CurrentPage?.UpdateSelectionBoundingBox(); }
                );
            }
        }
        else if (_isDraggingElement && _draggedElements.Count > 0)
        {
            if (ViewModel != null && ViewModel.SnapToGrid)
            {
                double snap = (double)(int)ViewModel.GridSnapSize;
                if (snap > 1)
                {
                    foreach (var el in _draggedElements)
                    {
                        el.X = Math.Round(el.X / snap) * snap;
                        el.Y = Math.Round(el.Y / snap) * snap;
                    }
                }
            }

            var initialPositions = _dragStartPositions.ToList();
            var finalPositions = _draggedElements.Select(el => (Element: el, el.X, el.Y)).ToList();

            bool anyMoved = false;
            for (int i = 0; i < initialPositions.Count; i++)
            {
                if (Math.Abs(initialPositions[i].X - finalPositions[i].X) > 0.5 || Math.Abs(initialPositions[i].Y - finalPositions[i].Y) > 0.5)
                {
                    anyMoved = true;
                    break;
                }
            }

            if (anyMoved)
            {
                string desc = _draggedElements.Count == 1 ? $"Move {_draggedElements[0].DisplayName}" : $"Move {_draggedElements.Count} Elements";
                ViewModel?.UndoRedo.RecordAction(
                    desc,
                    () => {
                        foreach (var p in initialPositions) { p.Element.X = p.X; p.Element.Y = p.Y; }
                        ViewModel?.CurrentPage?.UpdateSelectionBoundingBox();
                    },
                    () => {
                        foreach (var p in finalPositions) { p.Element.X = p.X; p.Element.Y = p.Y; }
                        ViewModel?.CurrentPage?.UpdateSelectionBoundingBox();
                    }
                );
            }
        }

        if (e.Pointer.Captured == this)
        {
            e.Pointer.Capture(null);
        }
        _isDraggingElement = false;
        _isResizingHandle = false;
        _activeResizeHandle = null;
        _draggedElement = null;
        _draggedElements.Clear();
        _dragStartPositions.Clear();
        _isPanning = false;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (MarqueeSelectionBox != null) MarqueeSelectionBox.IsVisible = false;
        _isMarqueeSelecting = false;
        _isDraggingElement = false;
        _isResizingHandle = false;
        _activeResizeHandle = null;
        _draggedElement = null;
        _draggedElements.Clear();
        _dragStartPositions.Clear();
        _isPanning = false;
    }

    /// <summary>
    /// Pan cursor for space-drag, created once.
    /// </summary>
    /// <remarks>
    /// <see cref="Cursor"/> is an IDisposable wrapper over a native handle. This was allocated
    /// fresh on every space-bar press and never disposed, leaking a handle per press — the same
    /// bug already fixed in <c>Views/Controls/PdfTextOverlayControl</c>.
    /// </remarks>
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private void OnCanvasKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _isSpacePressed = false;
            Cursor = Cursor.Default;
        }
    }

    private void OnCanvasKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel?.CurrentPage == null) return;

        var selected = ViewModel.CurrentPage.SelectedElement;

        // Check if user is actively typing inside any TextBox / TextPresenter
        var topLevel = TopLevel.GetTopLevel(this);
        var focused = topLevel?.FocusManager?.GetFocusedElement();
        bool isFocusedInTextBox = focused is TextBox ||
                                  focused is Avalonia.Controls.Presenters.TextPresenter ||
                                  (focused is Control ctrl && ctrl.FindAncestorOfType<TextBox>() != null);

        bool isSourceTextBox = e.Source is TextBox ||
                               e.Source is Avalonia.Controls.Presenters.TextPresenter ||
                               (e.Source is Control sCtrl && sCtrl.FindAncestorOfType<TextBox>() != null);

        bool isEditingText = isFocusedInTextBox || isSourceTextBox || (selected != null && selected.IsInEditMode);

        if (isEditingText)
        {
            if (e.Key == Key.Escape)
            {
                if (selected != null)
                {
                    selected.IsInEditMode = false;
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            {
                if (selected != null)
                {
                    selected.IsInEditMode = false;
                }
                e.Handled = true;
            }
            // Allow all typing, backspace, delete, arrows, and shortcuts to flow to the TextBox
            return;
        }

        if (e.Key == Key.Space && !_isSpacePressed)
        {
            _isSpacePressed = true;
            Cursor = HandCursor;
            return;
        }

        bool isCtrlOrCmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10.0 : 1.0;

        if (isCtrlOrCmd)
        {
            switch (e.Key)
            {
                case Key.D0:
                case Key.NumPad0:
                    ViewModel.ResetZoom();
                    e.Handled = true;
                    break;
                case Key.D1:
                case Key.NumPad1:
                    ViewModel.FitToWidthDynamic(CanvasScrollViewer?.Viewport.Width ?? 800);
                    e.Handled = true;
                    break;
                case Key.D9:
                case Key.NumPad9:
                    ViewModel.FitToPageDynamic(CanvasScrollViewer?.Viewport.Width ?? 800, CanvasScrollViewer?.Viewport.Height ?? 800);
                    e.Handled = true;
                    break;
                case Key.D2:
                case Key.NumPad2:
                    ViewModel.FitToWidthDynamic(CanvasScrollViewer?.Viewport.Width ?? 800);
                    e.Handled = true;
                    break;
                case Key.OemPlus:
                case Key.Add:
                    ViewModel.ZoomIn();
                    e.Handled = true;
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    ViewModel.ZoomOut();
                    e.Handled = true;
                    break;
                case Key.A:
                    ViewModel.CurrentPage?.SelectAll();
                    e.Handled = true;
                    break;
                case Key.C:
                    ViewModel.CopyCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.X:
                    ViewModel.CutCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.V:
                    ViewModel.PasteCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.D:
                    ViewModel.DuplicateCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Z:
                    ViewModel.UndoCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Y:
                    ViewModel.RedoCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.S:
                    ViewModel.SaveProjectCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.OemCloseBrackets:
                    ViewModel.Inspector.BringToFrontCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.OemOpenBrackets:
                    ViewModel.Inspector.SendToBackCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
        else if (selected != null || (ViewModel.CurrentPage?.SelectedElements.Count ?? 0) > 0)
        {
            // If user starts typing any alphanumeric or punctuation character while a text element is selected,
            // immediately enter in-place edit mode and route focus to the TextBox!
            if (selected is TextElementViewModel textEl && !textEl.IsInEditMode && !isCtrlOrCmd && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                bool isPrintable = (e.Key >= Key.A && e.Key <= Key.Z) ||
                                   (e.Key >= Key.D0 && e.Key <= Key.D9) ||
                                   (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9) ||
                                   e.Key == Key.Space || e.Key == Key.Enter || e.Key == Key.F2 ||
                                   e.Key == Key.OemPeriod || e.Key == Key.OemComma || e.Key == Key.OemMinus || e.Key == Key.OemPlus;

                if (isPrintable)
                {
                    textEl.IsInEditMode = true;

                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        FocusInPlaceTextBox(textEl);
                    }, Avalonia.Threading.DispatcherPriority.Input);

                    if (e.Key == Key.Enter || e.Key == Key.F2)
                    {
                        e.Handled = true;
                        return;
                    }
                    return;
                }
            }
            else if (selected is ShapeElementViewModel shapeEl && !shapeEl.IsInEditMode && !isCtrlOrCmd && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                if (e.Key == Key.Enter || e.Key == Key.F2)
                {
                    shapeEl.IsInEditMode = true;
                    e.Handled = true;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => FocusInPlaceTextBox(shapeEl), Avalonia.Threading.DispatcherPriority.Input);
                    return;
                }
            }
            else if (selected is FormFieldElementViewModel formEl && !formEl.IsInEditMode && !isCtrlOrCmd && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                bool isPrintable = (e.Key >= Key.A && e.Key <= Key.Z) ||
                                   (e.Key >= Key.D0 && e.Key <= Key.D9) ||
                                   (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9) ||
                                   e.Key == Key.Space || e.Key == Key.Enter || e.Key == Key.F2 ||
                                   e.Key == Key.OemPeriod || e.Key == Key.OemComma || e.Key == Key.OemMinus || e.Key == Key.OemPlus;

                if (isPrintable)
                {
                    formEl.IsInEditMode = true;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => FocusInPlaceTextBox(formEl), Avalonia.Threading.DispatcherPriority.Input);

                    if (e.Key == Key.Enter || e.Key == Key.F2)
                    {
                        e.Handled = true;
                        return;
                    }
                    return;
                }
            }

            if (e.Key == Key.Tab && ViewModel?.CurrentPage != null)
            {
                var formFields = ViewModel.CurrentPage.Elements
                    .OfType<FormFieldElementViewModel>()
                    .OrderBy(f => f.TabIndex > 0 ? f.TabIndex : int.MaxValue)
                    .ThenBy(f => f.Y)
                    .ThenBy(f => f.X)
                    .ToList();

                if (formFields.Count > 0)
                {
                    bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    int curIdx = selected is FormFieldElementViewModel currentForm ? formFields.IndexOf(currentForm) : -1;
                    int nextIdx;
                    if (isShift)
                    {
                        nextIdx = curIdx <= 0 ? formFields.Count - 1 : curIdx - 1;
                    }
                    else
                    {
                        nextIdx = (curIdx + 1) % formFields.Count;
                    }

                    var nextField = formFields[nextIdx];
                    ViewModel.CurrentPage.SelectElement(nextField);
                    nextField.IsInEditMode = true;
                    e.Handled = true;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => FocusInPlaceTextBox(nextField), Avalonia.Threading.DispatcherPriority.Input);
                    return;
                }
            }

            switch (e.Key)
            {
                case Key.Enter:
                case Key.F2:
                    break;
                case Key.Delete:
                case Key.Back:
                    ViewModel?.Inspector.DeleteSelectedElementCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    ViewModel?.CurrentPage?.ClearSelection();
                    e.Handled = true;
                    break;
                case Key.Left:
                case Key.Right:
                case Key.Up:
                case Key.Down:
                    if (selected != null && ViewModel?.CurrentPage is { } page)
                    {
                        double oldX = selected.X;
                        double oldY = selected.Y;

                        if (e.Key == Key.Left) selected.X = Math.Max(0, selected.X - step);
                        else if (e.Key == Key.Right) selected.X = Math.Min(page.Width - selected.Width, selected.X + step);
                        else if (e.Key == Key.Up) selected.Y = Math.Max(0, selected.Y - step);
                        else if (e.Key == Key.Down) selected.Y = Math.Min(page.Height - selected.Height, selected.Y + step);

                        double newX = selected.X;
                        double newY = selected.Y;
                        var el = selected;
                        ViewModel?.UndoRedo.RecordAction(
                            $"Nudge {el.DisplayName}",
                            () => { el.X = oldX; el.Y = oldY; },
                            () => { el.X = newX; el.Y = newY; }
                        );
                        e.Handled = true;
                    }
                    break;
                case Key.OemCloseBrackets:
                    ViewModel?.Inspector.BringToFrontCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.OemOpenBrackets:
                    ViewModel?.Inspector.SendToBackCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
        else
        {
            // Global quick single-key tool switches (when no element is selected)
            switch (e.Key)
            {
                case Key.V:
                    ViewModel.SetToolModeCommand.Execute("Select");
                    e.Handled = true;
                    break;
                case Key.H:
                    ViewModel.SetToolModeCommand.Execute("Pan");
                    e.Handled = true;
                    break;
                case Key.Z:
                    ViewModel.SetToolModeCommand.Execute("Zoom");
                    e.Handled = true;
                    break;
                case Key.T:
                    ViewModel.AddTextElementCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.R:
                    ViewModel.AddShapeElementCommand.Execute("Rectangle");
                    e.Handled = true;
                    break;
                case Key.N:
                    ViewModel.AddStickyNoteElementCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.D:
                    ViewModel.AddInkElementCommand.Execute(false);
                    e.Handled = true;
                    break;
            }
        }
    }
}

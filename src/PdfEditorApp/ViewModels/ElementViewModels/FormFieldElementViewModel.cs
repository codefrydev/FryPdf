using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Core.Models;
using PdfEditorApp.Core.Models.Elements;
using PdfEditorApp.Models;

namespace PdfEditorApp.ViewModels.ElementViewModels;

public partial class FormFieldElementViewModel : ElementViewModelBase
{
    [ObservableProperty]
    private FormFieldType _fieldType = FormFieldType.Text;

    [ObservableProperty]
    private string _fieldName = "form_field_1";

    [ObservableProperty]
    private string _label = "Full Legal Name:";

    [ObservableProperty]
    private string _placeholder = "Type text here...";

    [ObservableProperty]
    private string _value = "";

    [ObservableProperty]
    private string _defaultValue = "";

    [ObservableProperty]
    private string _tooltip = "";

    [ObservableProperty]
    private bool _isRequired = true;

    [ObservableProperty]
    private bool _isReadOnly = false;

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private FormValidationType _validationType = FormValidationType.None;

    [ObservableProperty]
    private string _customValidationRegex = "";

    [ObservableProperty]
    private string _borderColorHex = "#0F6CBD";

    [ObservableProperty]
    private string _backgroundColorHex = "#F8FAFC";

    [ObservableProperty]
    private double _fontSize = 12;

    [ObservableProperty]
    private string _fontFamily = "Arial";

    [ObservableProperty]
    private string _textColorHex = "#1E293B";

    [ObservableProperty]
    private TextAlignmentMode _textAlignment = TextAlignmentMode.Left;

    [ObservableProperty]
    private bool _showBorder = true;

    [ObservableProperty]
    private bool _showBackground = true;

    [ObservableProperty]
    private bool _showLabel = true;

    [ObservableProperty]
    private int _maxLength = 0;

    [ObservableProperty]
    private bool _isPassword = false;

    [ObservableProperty]
    private int _tabIndex = 0;

    // Acrobat Form Calculations & Action Buttons
    [ObservableProperty]
    private CalculationFormula _calculationFormula = CalculationFormula.None;

    [ObservableProperty]
    private string _calculationSourceFields = "";

    [ObservableProperty]
    private FormButtonAction _buttonAction = FormButtonAction.SubmitForm;

    [ObservableProperty]
    private string _actionTarget = "";

    public ObservableCollection<string> Options { get; } = new();

    public override ElementKind Kind => ElementKind.FormField;
    public override string DisplayName => $"Form: {Label} ({FieldType})";

    public FormFieldElementViewModel()
    {
        Options.Add("Approved");
        Options.Add("Under Review");
        Options.Add("Declined");
    }

    [RelayCommand]
    public void ToggleChecked()
    {
        IsChecked = !IsChecked;
    }

    [RelayCommand]
    public void AddOption(string? optionText)
    {
        string text = string.IsNullOrWhiteSpace(optionText) ? $"Option {Options.Count + 1}" : optionText.Trim();
        Options.Add(text);
    }

    [RelayCommand]
    public void RemoveOption(string? optionText)
    {
        if (optionText != null && Options.Contains(optionText))
        {
            Options.Remove(optionText);
        }
    }

    public override PdfElementBase ToModel()
    {
        var model = new PdfFormFieldElement
        {
            Id = Id,
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            ZIndex = ZIndex,
            Rotation = Rotation,
            Opacity = Opacity,
            IsLocked = IsLocked,
            FieldType = FieldType,
            FieldName = FieldName,
            Label = Label,
            Placeholder = Placeholder,
            Value = Value,
            DefaultValue = DefaultValue,
            Tooltip = Tooltip,
            IsRequired = IsRequired,
            IsReadOnly = IsReadOnly,
            IsChecked = IsChecked,
            ValidationType = ValidationType,
            CustomValidationRegex = CustomValidationRegex,
            BorderColorHex = BorderColorHex,
            BackgroundColorHex = BackgroundColorHex,
            FontSize = FontSize,
            FontFamily = FontFamily,
            TextColorHex = TextColorHex,
            TextAlignment = TextAlignment,
            ShowBorder = ShowBorder,
            ShowBackground = ShowBackground,
            ShowLabel = ShowLabel,
            MaxLength = MaxLength,
            IsPassword = IsPassword,
            TabIndex = TabIndex,
            CalculationFormula = CalculationFormula,
            CalculationSourceFields = CalculationSourceFields,
            ButtonAction = ButtonAction,
            ActionTarget = ActionTarget,
            Options = new System.Collections.Generic.List<string>(Options)
        };
        return model;
    }

    public override void LoadFromModel(PdfElementBase model)
    {
        if (model is PdfFormFieldElement form)
        {
            Id = form.Id;
            X = form.X;
            Y = form.Y;
            Width = form.Width;
            Height = form.Height;
            ZIndex = form.ZIndex;
            Rotation = form.Rotation;
            Opacity = form.Opacity;
            IsLocked = form.IsLocked;

            FieldType = form.FieldType;
            FieldName = form.FieldName;
            Label = form.Label;
            Placeholder = form.Placeholder;
            Value = form.Value;
            DefaultValue = form.DefaultValue;
            Tooltip = form.Tooltip;
            IsRequired = form.IsRequired;
            IsReadOnly = form.IsReadOnly;
            IsChecked = form.IsChecked;
            ValidationType = form.ValidationType;
            CustomValidationRegex = form.CustomValidationRegex;
            BorderColorHex = form.BorderColorHex;
            BackgroundColorHex = form.BackgroundColorHex;
            FontSize = form.FontSize;
            FontFamily = form.FontFamily;
            TextColorHex = form.TextColorHex;
            TextAlignment = form.TextAlignment;
            ShowBorder = form.ShowBorder;
            ShowBackground = form.ShowBackground;
            ShowLabel = form.ShowLabel;
            MaxLength = form.MaxLength;
            IsPassword = form.IsPassword;
            TabIndex = form.TabIndex;
            CalculationFormula = form.CalculationFormula;
            CalculationSourceFields = form.CalculationSourceFields;
            ButtonAction = form.ButtonAction;
            ActionTarget = form.ActionTarget;

            Options.Clear();
            foreach (var opt in form.Options) Options.Add(opt);
        }
    }
}

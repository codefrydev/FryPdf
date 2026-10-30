using System.Collections.Generic;

namespace PdfEditorApp.Core.Models.Elements;

public class PdfFormFieldElement : PdfElementBase
{
    public override ElementKind Kind => ElementKind.FormField;

    public FormFieldType FieldType { get; set; } = FormFieldType.Text;
    public string FieldName { get; set; } = "form_field_1";
    public string Label { get; set; } = "Full Name:";
    public string Placeholder { get; set; } = "Enter your full legal name...";
    public string Value { get; set; } = "";
    public string DefaultValue { get; set; } = "";
    public string Tooltip { get; set; } = "";
    public bool IsRequired { get; set; } = true;
    public bool IsReadOnly { get; set; } = false;
    public bool IsChecked { get; set; } = false;
    public FormValidationType ValidationType { get; set; } = FormValidationType.None;
    public string CustomValidationRegex { get; set; } = "";
    public string BorderColorHex { get; set; } = "#0F6CBD";
    public string BackgroundColorHex { get; set; } = "#F8FAFC";
    public double FontSize { get; set; } = 12;
    public string FontFamily { get; set; } = "Arial";
    public string TextColorHex { get; set; } = "#1E293B";
    public TextAlignmentMode TextAlignment { get; set; } = TextAlignmentMode.Left;
    public bool ShowBorder { get; set; } = true;
    public bool ShowBackground { get; set; } = true;
    public bool ShowLabel { get; set; } = true;
    public int MaxLength { get; set; } = 0;
    public bool IsPassword { get; set; } = false;
    public int TabIndex { get; set; } = 0;
    public List<string> Options { get; set; } = new() { "Option 1", "Option 2", "Option 3" };

    // Acrobat Form Calculations & Action Buttons
    public CalculationFormula CalculationFormula { get; set; } = CalculationFormula.None;
    public string CalculationSourceFields { get; set; } = "";
    public FormButtonAction ButtonAction { get; set; } = FormButtonAction.SubmitForm;
    public string ActionTarget { get; set; } = "";

    public override PdfElementBase Clone()
    {
        var clone = (PdfFormFieldElement)base.Clone();
        clone.Options = new List<string>(Options);
        return clone;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using PdfEditorApp.Core.Models;
using PdfEditorApp.Core.Models.Elements;
using UglyToad.PdfPig;
using UglyToad.PdfPig.AcroForms;
using UglyToad.PdfPig.AcroForms.Fields;

namespace PdfEditorApp.Core.Deconstruction.Extractors;

/// <summary>
/// Strongly-typed, reflection-free AcroForms interactive form field extractor.
/// Maps native PdfPig AcroField hierarchy into editable <see cref="PdfFormFieldElement"/> models.
/// </summary>
public static class PdfFormExtractor
{
    /// <summary>
    /// Extracts all AcroForms on the given document/page using strongly-typed patterns without reflection.
    /// </summary>
    public static List<PdfFormFieldElement> ExtractFormFields(
        PdfDocument? doc,
        int pageNumber,
        double pageHeight,
        ref int formZIndex,
        PdfDeconstructionOptions options,
        ILogger? logger = null)
    {
        var formElements = new List<PdfFormFieldElement>();
        if (doc == null) return formElements;

        try
        {
            if (doc.TryGetForm(out var form) && form != null && form.Fields != null)
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var field in form.Fields)
                {
                    // Filter by page: if field specifies a positive page number, ensure it matches current page
                    if (field.PageNumber > 0 && field.PageNumber != pageNumber)
                    {
                        continue;
                    }

                    if (!field.Bounds.HasValue || field.Bounds.Value.Width <= 0 || field.Bounds.Value.Height <= 0)
                        continue;

                    var b = field.Bounds.Value;
                    double fX = Math.Max(0, b.Left);
                    double fY = Math.Max(0, pageHeight - b.Top);
                    double fW = Math.Max(20, b.Width);
                    double fH = Math.Max(14, b.Height);

                    string fieldName = field.Information?.PartialName
                        ?? field.Information?.AlternateName
                        ?? field.Information?.MappingName
                        ?? $"FormField_{pageNumber}_{formElements.Count + 1}";

                    // Name disambiguation
                    if (usedNames.Contains(fieldName))
                    {
                        fieldName = $"{fieldName}_{pageNumber}_{formElements.Count + 1}";
                    }
                    usedNames.Add(fieldName);

                    string rawLabel = field.Information?.AlternateName ?? field.Information?.PartialName ?? field.Information?.MappingName ?? fieldName;
                    string label = HumanizeLabel(rawLabel);

                    string fieldValue = ExtractFieldValue(field);
                    FormFieldType fieldType = MapFieldType(field);

                    // PDF spec 32000-1: bit 1 of FieldFlags is ReadOnly (0x1)
                    bool isReadOnly = (field.FieldFlags & 1) != 0;

                    // On imported PDFs, single-line input boxes (height <= 28) overlay existing printed labels.
                    // Keep ShowLabel = false so internal labels don't crowd the input box or obscure page content.
                    bool showLabel = fieldType == FormFieldType.Checkbox || fieldType == FormFieldType.Radio || fH >= 36;

                    var formElement = new PdfFormFieldElement
                    {
                        X = Math.Round(fX, 1),
                        Y = Math.Round(fY, 1),
                        Width = Math.Round(fW, 1),
                        Height = Math.Round(fH, 1),
                        FieldName = fieldName,
                        Label = label,
                        ShowLabel = showLabel,
                        Placeholder = fieldType == FormFieldType.Signature ? "Click to Sign / Verify" : (string.IsNullOrEmpty(fieldValue) ? $"Enter {label.TrimEnd(':')}..." : fieldValue),
                        Value = fieldValue,
                        DefaultValue = fieldValue,
                        FieldType = fieldType,
                        IsReadOnly = isReadOnly,
                        IsChecked = fieldType == FormFieldType.Checkbox && (fieldValue.Equals("true", StringComparison.OrdinalIgnoreCase) || fieldValue.Equals("yes", StringComparison.OrdinalIgnoreCase) || fieldValue.Equals("1")),
                        BackgroundColorHex = "Transparent",
                        ShowBackground = false,
                        BorderColorHex = "#2563EB",
                        ShowBorder = true,
                        FontSize = Math.Clamp(Math.Round(fH * 0.6, 1), 8.0, 14.0),
                        ZIndex = formZIndex++
                    };

                    // Extract dropdown and listbox options
                    if (field is AcroComboBoxField combo && combo.Options != null && combo.Options.Count > 0)
                    {
                        formElement.Options = combo.Options.Select(o => o.ToString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToList();
                    }
                    else if (field is AcroListBoxField list && list.Options != null && list.Options.Count > 0)
                    {
                        formElement.Options = list.Options.Select(o => o.ToString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToList();
                    }

                    formElements.Add(formElement);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to extract AcroForms on page {PageNumber}", pageNumber);
        }

        return formElements;
    }

    private static string HumanizeLabel(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Field:";
        string clean = raw.Trim();
        if (clean.StartsWith("txt_", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("chk_", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("rdo_", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("cbo_", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("btn_", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring(4);
        }
        clean = clean.Replace('_', ' ').Replace('-', ' ');
        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
        }
        string result = string.Join(" ", words);
        if (!result.EndsWith(":") && !result.EndsWith("?"))
            result += ":";
        return result;
    }

    private static string ExtractFieldValue(AcroFieldBase field)
    {
        return field switch
        {
            AcroTextField tf => tf.Value ?? string.Empty,
            AcroCheckboxField cb => cb.IsChecked ? "true" : "false",
            AcroRadioButtonField rb => rb.IsSelected ? "true" : "false",
            AcroComboBoxField combo => combo.SelectedOptions.FirstOrDefault() ?? combo.Options.FirstOrDefault()?.ToString() ?? string.Empty,
            AcroListBoxField list => list.SelectedOptions.FirstOrDefault() ?? list.Options.FirstOrDefault()?.ToString() ?? string.Empty,
            _ => string.Empty
        };
    }

    private static FormFieldType MapFieldType(AcroFieldBase field)
    {
        return field.FieldType switch
        {
            AcroFieldType.Checkbox => FormFieldType.Checkbox,
            AcroFieldType.RadioButton => FormFieldType.Radio,
            AcroFieldType.ComboBox or AcroFieldType.ListBox => FormFieldType.Dropdown,
            AcroFieldType.Signature => FormFieldType.Signature,
            _ => FormFieldType.Text
        };
    }
}

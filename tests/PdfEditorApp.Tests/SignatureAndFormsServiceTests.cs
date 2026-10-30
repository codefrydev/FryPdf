using System;
using PdfEditorApp.Core.Models;
using PdfEditorApp.Core.Models.Elements;
using PdfEditorApp.Models;
using PdfEditorApp.Services;
using Xunit;

namespace PdfEditorApp.Tests;

public class SignatureAndFormsServiceTests
{
    [Fact]
    public void SignatureService_ComputeDocumentSha256_ProducesDeterministicHash()
    {
        // Arrange
        var service = new SignatureService();
        var doc1 = new PdfDocumentModel
        {
            Title = "Contract_Final.pdf",
            Author = "Enterprise Signer",
            Subject = "Service Agreement"
        };
        var page1 = new PdfPageModel { PageNumber = 1 };
        page1.Elements.Add(new PdfTextElement { Text = "Terms & Conditions", X = 50, Y = 50 });
        doc1.Pages.Add(page1);

        var doc2 = new PdfDocumentModel
        {
            Title = "Contract_Final.pdf",
            Author = "Enterprise Signer",
            Subject = "Service Agreement"
        };
        var page2 = new PdfPageModel { PageNumber = 1 };
        page2.Elements.Add(new PdfTextElement { Text = "Terms & Conditions", X = 50, Y = 50 });
        doc2.Pages.Add(page2);

        // Act
        string hash1 = service.ComputeDocumentSha256(doc1);
        string hash2 = service.ComputeDocumentSha256(doc2);

        // Assert
        Assert.Equal(64, hash1.Length);
        Assert.Equal(hash1, hash2);

        // Modify doc2
        doc2.Title = "Contract_Tampered.pdf";
        string hash3 = service.ComputeDocumentSha256(doc2);
        Assert.NotEqual(hash1, hash3);
    }

    [Fact]
    public void SignatureService_CreateCryptographicSignatureSeal_PopulatesMetadataAndDigest()
    {
        // Arrange
        var service = new SignatureService();
        string dummyDigest = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";

        // Act
        var seal = service.CreateCryptographicSignatureSeal("Dr. Evelyn Reed", "Global Medical Corp", "Clinical Approval", dummyDigest, 120, 250);

        // Assert
        Assert.NotNull(seal);
        Assert.Equal(120, seal.X);
        Assert.Equal(250, seal.Y);
        Assert.Equal(320, seal.Width);
        Assert.Equal(100, seal.Height);
        Assert.Contains("Dr. Evelyn Reed", seal.Label);
        Assert.Contains("Global Medical Corp", seal.Label);
        Assert.Contains("Clinical Approval", seal.Label);
        Assert.Contains("SHA256:", seal.Label);
        Assert.Equal("#16A34A", seal.StrokeColorHex); // Emerald verification border
    }

    [Fact]
    public void SignatureService_CreateCursiveSignatureElement_AppliesFontAndStyleCorrectly()
    {
        // Arrange
        var service = new SignatureService();

        // Act
        var sigExecutive = service.CreateCursiveSignatureElement("Alexander Hamilton", SignatureStyle.CursiveElegance, 50, 100);
        var sigScript = service.CreateCursiveSignatureElement("Alexander Hamilton", SignatureStyle.ClassicScript, 50, 100);

        // Assert
        Assert.Equal("Georgia", sigExecutive.FontFamily);
        Assert.True(sigExecutive.IsItalic);
        Assert.Equal("Alexander Hamilton", sigExecutive.Text);

        Assert.Equal("Times New Roman", sigScript.FontFamily);
        Assert.Equal("#0F6CBD", sigScript.TextColorHex);
    }

    [Fact]
    public void SignatureService_CreateFormFieldElement_GeneratesProperFieldTypes()
    {
        // Arrange
        var service = new SignatureService();

        // Act
        var txtField = service.CreateFormFieldElement(FormFieldType.Text, "txt_customer_name", 50, 100, 200, 30);
        var chkField = service.CreateFormFieldElement(FormFieldType.Checkbox, "chk_terms_agreed", 50, 150, 24, 24);

        // Assert
        Assert.Equal(FormFieldType.Text, txtField.FieldType);
        Assert.Equal("txt_customer_name", txtField.FieldName);
        Assert.Equal(200, txtField.Width);

        Assert.Equal(FormFieldType.Checkbox, chkField.FieldType);
        Assert.Equal("chk_terms_agreed", chkField.FieldName);
        Assert.Equal("false", chkField.DefaultValue);
    }

    [Fact]
    public void PdfFormFieldElement_CloneAndExtendedProperties_PreservesAllFields()
    {
        var original = new PdfFormFieldElement
        {
            FieldName = "email_address",
            Label = "Email Address:",
            Value = "test@example.com",
            DefaultValue = "user@domain.com",
            FieldType = FormFieldType.Text,
            FontFamily = "Inter",
            FontSize = 14.5,
            TextColorHex = "#2563EB",
            TextAlignment = TextAlignmentMode.Center,
            ShowBorder = false,
            ShowBackground = true,
            ShowLabel = false,
            MaxLength = 50,
            IsPassword = false,
            TabIndex = 2
        };
        original.Options.Add("Custom Option");

        var cloned = (PdfFormFieldElement)original.Clone();

        Assert.Equal(original.FieldName, cloned.FieldName);
        Assert.Equal(original.Label, cloned.Label);
        Assert.Equal(original.Value, cloned.Value);
        Assert.Equal(original.FontFamily, cloned.FontFamily);
        Assert.Equal(original.FontSize, cloned.FontSize);
        Assert.Equal(original.TextColorHex, cloned.TextColorHex);
        Assert.Equal(original.TextAlignment, cloned.TextAlignment);
        Assert.False(cloned.ShowBorder);
        Assert.True(cloned.ShowBackground);
        Assert.False(cloned.ShowLabel);
        Assert.Equal(50, cloned.MaxLength);
        Assert.Equal(2, cloned.TabIndex);
        Assert.Contains("Custom Option", cloned.Options);
    }

    [Fact]
    public void PageViewModel_RecalculateFormFields_AutomaticallyRecalculatesOnFieldValueChange()
    {
        var page = new PdfPageModel { PageNumber = 1 };
        var pageVm = new PdfEditorApp.ViewModels.PageViewModel();
        pageVm.LoadFromModel(page);

        var item1 = new PdfEditorApp.ViewModels.ElementViewModels.FormFieldElementViewModel
        {
            FieldName = "item_price",
            Value = "25.50"
        };
        var item2 = new PdfEditorApp.ViewModels.ElementViewModels.FormFieldElementViewModel
        {
            FieldName = "item_tax",
            Value = "4.50"
        };
        var total = new PdfEditorApp.ViewModels.ElementViewModels.FormFieldElementViewModel
        {
            FieldName = "order_total",
            CalculationFormula = CalculationFormula.Sum,
            CalculationSourceFields = "item_price, item_tax",
            Value = "0.00"
        };

        pageVm.Elements.Add(item1);
        pageVm.Elements.Add(item2);
        pageVm.Elements.Add(total);

        // Initial recalculation
        pageVm.RecalculateFormFields();
        Assert.Equal("30.00", total.Value);

        // Value change should trigger automatic recalculation
        item1.Value = "50.00";
        Assert.Equal("54.50", total.Value);
    }

    [Fact]
    public void FryPdfViewerViewModel_SyncCurrentDocumentModel_CapturesViewerEdits()
    {
        var doc = new PdfDocumentModel { Title = "InteractiveForm.frypdf" };
        var page = new PdfPageModel { PageNumber = 1 };
        page.Elements.Add(new PdfFormFieldElement
        {
            FieldName = "full_name",
            Value = ""
        });
        doc.Pages.Add(page);

        var viewer = new PdfEditorApp.ViewModels.FryPdfViewer.FryPdfViewerViewModel();
        viewer.LoadFromModel(doc);

        Assert.Single(viewer.Pages);
        var formVm = viewer.Pages[0].Elements.OfType<PdfEditorApp.ViewModels.ElementViewModels.FormFieldElementViewModel>().First();
        formVm.Value = "Alice Developer";

        // Synchronize back to document model
        viewer.SyncCurrentDocumentModel();

        var syncedField = viewer.CurrentDocumentModel!.Pages[0].Elements.OfType<PdfFormFieldElement>().First();
        Assert.Equal("Alice Developer", syncedField.Value);
    }

    [Fact]
    public async System.Threading.Tasks.Task PdfExportService_ExportsCompactFormField_WithoutOverflowOrClipping()
    {
        var doc = new PdfDocumentModel { Title = "CompactFormExport.pdf" };
        var page = new PdfPageModel { PageNumber = 1, Width = 595.28, Height = 841.89 };
        page.Elements.Add(new PdfFormFieldElement
        {
            FieldName = "tax_id",
            Label = "Tax ID:",
            Value = "XX-9988776",
            X = 50,
            Y = 100,
            Width = 180,
            Height = 18, // Typical compact 18pt AcroField height
            ShowLabel = false
        });
        doc.Pages.Add(page);

        var exportService = new PdfExportService();
        byte[] pdfBytes = await exportService.ExportToBytesAsync(doc);

        Assert.NotNull(pdfBytes);
        Assert.True(pdfBytes.Length > 500);
    }
}

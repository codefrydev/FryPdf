using PdfEditorApp.ViewModels.Tools.Core;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PdfEditorApp.Models;
using PdfEditorApp.Services;
using PdfEditorApp.Services.Tools.Intelligence;

namespace PdfEditorApp.ViewModels.Tools.Intelligence;

public partial class FormFieldEntryItem : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _value = "";
}

public partial class PdfFormsToolViewModel : PdfToolViewModelBase
{
    [ObservableProperty]
    private bool _flattenFields;

    [ObservableProperty]
    private bool _exportFieldValuesJson;

    public ObservableCollection<FormFieldEntryItem> DiscoveredFields { get; } = new();

    public override bool UsesWorkspaceShell => true;

    public PdfFormsToolViewModel(IPdfDocumentOperationsService operationsService, PdfToolDefinition tool)
        : base(operationsService, tool)
    {
    }

    public async Task RefreshFormFieldsAsync()
    {
        DiscoveredFields.Clear();
        if (string.IsNullOrWhiteSpace(PrimaryInputFile) || !System.IO.File.Exists(PrimaryInputFile))
            return;

        try
        {
            var formService = new PdfFormService();
            var dict = await formService.GetFormFieldsAsync(PrimaryInputFile);
            foreach (var kvp in dict)
            {
                DiscoveredFields.Add(new FormFieldEntryItem { Name = kvp.Key, Value = kvp.Value });
            }
        }
        catch { }
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(IProgress<double> progress, CancellationToken ct)
    {
        var options = new FormToolOptions
        {
            InputFilePath = PrimaryInputFile ?? string.Empty,
            FlattenFields = FlattenFields,
            ExportFieldValuesJson = ExportFieldValuesJson,
            FieldValues = DiscoveredFields.ToDictionary(f => f.Name, f => f.Value)
        };

        return await OperationsService.ExecuteToolAsync(PdfToolId.PdfForms, options, progress, ct);
    }
}

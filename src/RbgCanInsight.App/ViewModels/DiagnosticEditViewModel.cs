using System.IO;
using RbgCanInsight.App.Common;
using RbgCanInsight.DiagData;

namespace RbgCanInsight.App.ViewModels;

/// <summary>Edit the display name and description of one CDD/ODX definition and save a validated
/// COPY. The source file is never modified; unknown vendor content is preserved.</summary>
public sealed class DiagnosticEditViewModel : ObservableObject
{
    private readonly DidParameter _parameter;
    private readonly string _sourcePath;
    private string _longName;
    private string _description;
    private string _status;
    private bool _busy;
    private AsyncCommand? _save;

    public DiagnosticEditViewModel(DidParameter parameter)
    {
        _parameter = parameter;
        _sourcePath = parameter.SourceFile;
        _longName = parameter.LongName;
        _description = parameter.Description;
        CanEditFile = DiagnosticCatalogEditor.CanEdit(_sourcePath, out var reason);
        _status = CanEditFile ? "Change the text, then save an edited copy. The source file stays unchanged." : reason;
    }

    public string Header => $"DID 0x{_parameter.Did:X4} · {_parameter.Name} · {Path.GetFileName(_sourcePath)}";
    public bool CanEditFile { get; }

    public string LongName
    {
        get => _longName;
        set { if (SetProperty(ref _longName, value)) _save?.RaiseCanExecuteChanged(); }
    }

    public string Description
    {
        get => _description;
        set { if (SetProperty(ref _description, value)) _save?.RaiseCanExecuteChanged(); }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasChanges =>
        !string.Equals(LongName.Trim(), _parameter.LongName.Trim(), StringComparison.Ordinal) ||
        !string.Equals(Description.Trim(), _parameter.Description.Trim(), StringComparison.Ordinal);

    public Func<string, string?> PickSaveFile { get; set; } = suggested =>
    {
        var extension = Path.GetExtension(suggested);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save edited diagnostic description copy",
            Filter = $"{extension.TrimStart('.').ToUpperInvariant()} description (*{extension})|*{extension}",
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = false,
            FileName = suggested
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };

    public AsyncCommand SaveCommand => _save ??= new AsyncCommand(SaveAsync,
        () => CanEditFile && !_busy && HasChanges);

    private async Task SaveAsync()
    {
        var suggested = Path.GetFileNameWithoutExtension(_sourcePath) + "-edited" + Path.GetExtension(_sourcePath);
        if (PickSaveFile(suggested) is not { } destination) return;
        _busy = true;
        _save?.RaiseCanExecuteChanged();
        try
        {
            var edit = new DiagnosticTextEdit(_parameter.Did, _parameter.LongName, _parameter.Description,
                LongName.Trim(), Description.Trim());
            var source = _sourcePath;
            await Task.Run(() => DiagnosticCatalogEditor.SaveCopy(source, destination, [edit]));
            Status = $"Edited copy saved as {Path.GetFileName(destination)}: only DID 0x{_parameter.Did:X4}'s name and " +
                     "description changed (validated by reloading and diffing); the source file is unchanged.";
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException
            or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Status = "Edited copy not saved: " + exception.Message;
        }
        finally
        {
            _busy = false;
            _save?.RaiseCanExecuteChanged();
        }
    }
}

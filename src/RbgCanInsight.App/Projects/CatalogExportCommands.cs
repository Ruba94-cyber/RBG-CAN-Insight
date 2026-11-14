using System.IO;
using System.Windows;
using Microsoft.Win32;
using RbgCanInsight.DiagData;

namespace RbgCanInsight.App.Projects;

public static class CatalogExportCommands
{
    public const string Caveat = "The file passes the app's structural checks and was validated against a public community ODX 2.2 schema in our tests, but not against the official ASAM/ISO 22901-1 schema (which is licensed). Check it in your ODX tool before relying on it.";

    /// <summary>Exports one loaded catalog as an ODX-D file, then runs the structural check and reports the result.</summary>
    public static void ExportOdx(Window owner, DidCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var dialog = new SaveFileDialog
        {
            Title = "Export catalog as ODX-D",
            Filter = "ODX diagnostic layer (*.odx-d)|*.odx-d",
            DefaultExt = ".odx-d",
            AddExtension = true,
            FileName = SafeFileName(string.IsNullOrWhiteSpace(catalog.Name) ? "catalog" : catalog.Name),
        };
        if (dialog.ShowDialog(owner) != true) return;

        try
        {
            OdxCatalogWriter.Write(catalog, dialog.FileName);
            var problems = OdxCatalogWriter.Validate(dialog.FileName);
            var text = problems.Count == 0
                ? $"Exported to {dialog.FileName}.\n\nThe structural check found no problems."
                : $"Exported to {dialog.FileName}.\n\nThe structural check found {problems.Count} problem(s):\n- " +
                  string.Join("\n- ", problems.Take(20)) + (problems.Count > 20 ? $"\n... and {problems.Count - 20} more" : "");
            text += "\n\n" + Caveat + " The source catalog file is never modified.";
            MessageBox.Show(owner, text, "ODX export", MessageBoxButton.OK,
                problems.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(owner, "The ODX export failed: " + ex.Message, "ODX export failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Convenience: exports the first loaded catalog (hook: <c>vm.Mapping.Catalogs</c>), or explains that none is loaded.</summary>
    public static void ExportOdx(Window owner, ViewModels.MainViewModel vm)
    {
        var catalogs = vm.Mapping.Catalogs;
        if (catalogs.Count == 0)
        {
            MessageBox.Show(owner, "Load a diagnostic catalog (CDD, ODX or PDX) first.", "ODX export",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ExportOdx(owner, catalogs[0]);
    }

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

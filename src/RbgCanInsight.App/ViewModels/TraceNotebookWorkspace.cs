using System.Collections.ObjectModel;
using System.IO;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App.ViewModels;

/// <summary>Tab metadata for a saved Trace-grid analysis, not a second raw
/// trace or a separate decoder. UI Automation can distinguish active tabs.</summary>
public sealed record TraceNotebookTabRow(TraceNotebookPage Page, bool IsActive)
{
    public string Id => Page.Id;
    public string Title => Page.Title;
    public string Description
    {
        get
        {
            var view = Page.ViewModeIndex switch
            {
                1 => "Latest per ID",
                2 => "Changes only",
                _ => "All frames"
            };
            var clock = Page.TimeDisplayModeIndex switch
            {
                1 => "Relative to trace start",
                2 => "Delta from previous frame",
                3 => "Delta from previous same ID",
                _ => "Absolute timestamp"
            };
            return (Page.SourcePath is null ? "Source: all recordings" : "Source: " + Page.SourcePath) +
                   Environment.NewLine +
                   "Filter: " + (string.IsNullOrWhiteSpace(Page.Query) ? "all frames" : Page.Query) +
                   Environment.NewLine + "Rows: " + view + " · Clock: " + clock +
                   (string.IsNullOrWhiteSpace(Page.Notes) ? "" :
                       Environment.NewLine + "Note: " + Page.Notes[..Math.Min(160, Page.Notes.Length)]) +
                   (Page.DiagnosticRecipe is null
                       ? Environment.NewLine + "Trace view only; UDS and DBC investigations retain their own filters."
                       : Environment.NewLine + "Saved DTC and DID↔DBC investigation filters" +
                         (Page.DiagnosticRecipe.OriginalSourcePath is { } diagnostic
                             ? " · logger: " + diagnostic : " · all recordings"));
        }
    }
}

/// <summary>One saved cell linked to the exact notebook tab that owns its
/// dataset/query recipe; result rows are recomputed, never serialized.</summary>
public sealed record TraceNotebookSavedCellRow(
    string PageId, string PageTitle, TraceNotebookCell Cell)
{
    // Row identity, not value equality: after a session restore an identical
    // recipe is a new row. Value equality would let WPF UI Automation reuse the
    // stale item peer of the replaced row, which exposes no child controls
    // (the "Run again"/"Remove" buttons vanish from the accessibility tree).
    public bool Equals(TraceNotebookSavedCellRow? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public string Title => Cell.Title;
    public string SourceTitle => PageTitle;
    public string KindText => Cell.Kind switch
    {
        TraceAggregateKind.BySource => "Group by physical source",
        TraceAggregateKind.BySecond => "Traffic bursts · 1 s per original recording and bus",
        _ => "Group by CAN ID/channel/source"
    };
}

/// <summary>
/// Lightweight notebook-style tab manager. Switching pages restores one
/// reproducible Trace query/view/source recipe without reimport or cloning
/// CAN payloads. The native .rbgsession stores all pages as optional JSON.
/// </summary>
public sealed class TraceNotebookWorkspace : ObservableObject
{
    private readonly MainViewModel _host;
    private readonly List<TraceNotebookPage> _pages = [];
    private string? _activeId;
    private string _titleDraft = string.Empty;
    private string _notesDraft = string.Empty;
    private RelayCommand? _newCommand;
    private RelayCommand? _duplicateCommand;
    private RelayCommand? _updateCommand;
    private RelayCommand? _renameCommand;
    private RelayCommand? _closeCommand;
    private RelayCommand<TraceNotebookTabRow>? _openCommand;

    public TraceNotebookWorkspace(MainViewModel host) => _host = host;

    public ObservableCollection<TraceNotebookTabRow> Tabs { get; } = [];
    public ObservableCollection<TraceNotebookSavedCellRow> SavedCells { get; } = [];
    public bool HasActive => _activeId is not null;
    public string? ActiveId => _activeId;
    public TraceNotebookDiagnosticRecipe? ActiveDiagnosticRecipe =>
        _pages.FirstOrDefault(page => page.Id == _activeId)?.DiagnosticRecipe;

    /// <summary>Save only a bounded diagnostic filter/source recipe into the
    /// active analysis; no ECU identity guesses, CAN frames or decoded results
    /// are cached or serialized.</summary>
    public bool SaveDiagnosticRecipe(TraceNotebookDiagnosticRecipe recipe)
    {
        var index = ActiveIndex();
        if (index < 0) return false;
        _pages[index] = TraceNotebookPages.Normalize(_pages[index] with
        {
            DiagnosticRecipe = recipe
        });
        RefreshTabs();
        return true;
    }
    public string Summary => _pages.Count == 0
        ? "Analysis tabs · create a reusable Trace view"
        : $"Analysis tabs · {_pages.Count} saved view(s)" +
          (_activeId is null ? "" : " · active");
    public string TitleDraft
    {
        get => _titleDraft;
        set => SetProperty(ref _titleDraft, value);
    }
    public string NotesDraft
    {
        get => _notesDraft;
        set => SetProperty(ref _notesDraft, value);
    }

    public RelayCommand NewCommand => _newCommand ??= new RelayCommand(() =>
    {
        if (_pages.Count >= TraceNotebookPages.MaxPages)
        {
            _host.StatusText = $"Maximum {TraceNotebookPages.MaxPages} analysis tabs per session.";
            return;
        }
        SaveActive();
        var page = _host.CaptureCurrentTraceNotebook(
            $"Analysis {_pages.Count + 1}", "", null);
        if (page is null) return;
        _pages.Add(page);
        _activeId = page.Id;
        TitleDraft = page.Title;
        NotesDraft = page.Notes;
        RefreshTabs();
        _host.StatusText = $"Analysis tab created: {page.Title}. Save the session to keep it.";
    });

    public RelayCommand DuplicateCommand => _duplicateCommand ??= new RelayCommand(() =>
    {
        if (_activeId is null || _pages.Count >= TraceNotebookPages.MaxPages) return;
        SaveActive();
        var source = _pages.FirstOrDefault(page => page.Id == _activeId);
        if (source is null) return;
        var copy = TraceNotebookPages.New(source.Title + " copy", source.Query,
            source.ViewModeIndex, source.TimeDisplayModeIndex, source.SourcePath, source.Notes) with
        {
            Cells = source.Cells?.Select(cell =>
                TraceNotebookPages.NewCell(cell.Kind, cell.Title)).ToArray(),
            DiagnosticRecipe = source.DiagnosticRecipe
        };
        _pages.Add(copy);
        _activeId = copy.Id;
        TitleDraft = copy.Title;
        NotesDraft = copy.Notes;
        RefreshTabs();
        _host.StatusText = $"Analysis tab duplicated: {copy.Title}.";
    });

    public RelayCommand UpdateCommand => _updateCommand ??= new RelayCommand(() =>
    {
        if (_activeId is null) return;
        if (!SaveActive(includeCurrentView: true)) return;
        RefreshTabs();
        _host.StatusText = "Current Trace query, source focus, modes and notes saved in this tab.";
    });

    public RelayCommand RenameCommand => _renameCommand ??= new RelayCommand(() =>
    {
        var index = ActiveIndex();
        if (index < 0) return;
        var name = TraceNotebookPages.Normalize(_pages[index] with { Title = TitleDraft }).Title;
        _pages[index] = _pages[index] with { Title = name };
        TitleDraft = name;
        RefreshTabs();
        _host.StatusText = $"Analysis tab renamed to {name}.";
    });

    public RelayCommand CloseCommand => _closeCommand ??= new RelayCommand(() =>
    {
        var index = ActiveIndex();
        if (index < 0) return;
        _pages.RemoveAt(index);
        _activeId = null;
        TitleDraft = string.Empty;
        NotesDraft = string.Empty;
        // Like a familiar tabbed editor, prefer the neighbouring tab.
        // If its physical source vanished, leave the current Trace untouched
        // and do not pretend the neighbouring recipe was applied.
        if (_pages.Count > 0)
        {
            var next = _pages[Math.Min(index, _pages.Count - 1)];
            if (_host.TryActivateTraceNotebook(next))
            {
                _activeId = next.Id;
                TitleDraft = next.Title;
                NotesDraft = next.Notes;
            }
        }
        RefreshTabs();
        _host.StatusText = _activeId is null
            ? "Analysis tab closed. Trace filters remain unchanged."
            : "Analysis tab closed; neighbouring analysis selected.";
    });

    public RelayCommand<TraceNotebookTabRow> OpenCommand =>
        _openCommand ??= new RelayCommand<TraceNotebookTabRow>(row => TryOpen(row));

    /// <summary>True only when the exact saved source and Trace recipe were
    /// restored. This is not inferred from the previous active-tab ID.</summary>
    public bool TryOpen(TraceNotebookTabRow? row)
    {
        if (row is null) return false;
        SaveActive(); // preserve note edits even when clicking the selected tab
        var requested = _pages.FirstOrDefault(page => page.Id == row.Id);
        if (requested is null) return false;
        if (!_host.TryActivateTraceNotebook(requested)) return false;
        _activeId = requested.Id;
        TitleDraft = requested.Title;
        NotesDraft = requested.Notes;
        RefreshTabs();
        return true;
    }

    /// <summary>The active tab captures note edits on session save. The
    /// query/source recipe changes only when Update view is explicitly chosen;
    /// switching tabs must not silently overwrite an investigator's evidence.</summary>
    public IReadOnlyList<TraceNotebookPage> SnapshotForSession()
    {
        SaveActive();
        return TraceNotebookPages.NormalizeMany(_pages);
    }

    /// <summary>Called after session sources have been resolved and imported.
    /// Returns false when an explicitly requested active page cannot be opened.</summary>
    public bool RestoreFromSession(
        IReadOnlyList<TraceNotebookPage>? pages, string? activeId,
        IReadOnlyDictionary<string, string> resolvedSourcePaths)
    {
        _pages.Clear();
        _pages.AddRange(TraceNotebookPages.RebindSources(pages, resolvedSourcePaths));
        _activeId = null;
        TitleDraft = string.Empty;
        NotesDraft = string.Empty;
        var requested = _pages.FirstOrDefault(page => page.Id == activeId);
        var restored = requested is null || _host.TryActivateTraceNotebook(requested);
        if (requested is not null && restored)
        {
            _activeId = requested.Id;
            TitleDraft = requested.Title;
            NotesDraft = requested.Notes;
        }
        RefreshTabs();
        return restored;
    }

    /// <summary>Discard tab recipes when a completely unrelated recording
    /// replaces the dataset; do not silently apply previous filters to new CAN.</summary>
    public void ClearForNewDataset()
    {
        if (_pages.Count == 0) return;
        _pages.Clear();
        _activeId = null;
        TitleDraft = string.Empty;
        NotesDraft = string.Empty;
        RefreshTabs();
    }

    private int ActiveIndex() => _activeId is null ? -1 :
        _pages.FindIndex(page => page.Id == _activeId);

    private bool SaveActive(bool includeCurrentView = false)
    {
        var index = ActiveIndex();
        if (index < 0) return false;
        // Notes are editable independently. A different source focus or query
        // does NOT overwrite the saved tab when switching or saving a session.
        _pages[index] = TraceNotebookPages.Normalize(
            _pages[index] with { Notes = NotesDraft });
        if (!includeCurrentView) return true;
        // Even an explicit Update may not silently reassign a missing file.
        if (_pages[index].SourcePath is { } pinned &&
            !_host.IsNotebookSourceAvailable(pinned))
        {
            _host.StatusText = "The tab's original source is unavailable. View recipe not overwritten.";
            return false;
        }
        var replacement = _host.CaptureCurrentTraceNotebook(
            _pages[index].Title, NotesDraft, _pages[index].Id);
        if (replacement is null) return false;
        // Updating the Trace query must not erase the separately saved
        // diagnostic investigation or its pinned original logger.
        _pages[index] = replacement with
        {
            Cells = _pages[index].Cells,
            DiagnosticRecipe = _pages[index].DiagnosticRecipe
        };
        return true;
    }

    /// <summary>Attach the computation recipe to the active saved analysis,
    /// or create one from the current Trace view on first use.</summary>
    public bool SaveAggregateCell(TraceAggregateKind kind)
    {
        SaveActive();
        if (ActiveIndex() < 0)
            NewCommand.Execute(null);
        var index = ActiveIndex();
        if (index < 0) return false;
        var owner = _pages[index];
        var current = _host.CaptureCurrentTraceNotebook(owner.Title, owner.Notes, owner.Id);
        if (current is null) return false;
        if (!string.Equals(owner.Query, current.Query, StringComparison.Ordinal) ||
            !string.Equals(owner.SourcePath, current.SourcePath, StringComparison.OrdinalIgnoreCase) ||
            owner.ViewModeIndex != current.ViewModeIndex ||
            owner.TimeDisplayModeIndex != current.TimeDisplayModeIndex)
        {
            _host.StatusText = "Update saved view first: the current source, filter or row mode differs from this analysis tab.";
            return false;
        }
        var cells = (_pages[index].Cells ?? []).ToList();
        if (cells.Count >= TraceNotebookPages.MaxCellsPerPage)
        {
            _host.StatusText = $"This analysis has reached its limit of {TraceNotebookPages.MaxCellsPerPage} cells.";
            return false;
        }
        cells.Add(TraceNotebookPages.NewCell(kind));
        _pages[index] = TraceNotebookPages.Normalize(_pages[index] with { Cells = cells });
        RefreshTabs();
        return true;
    }

    public bool RemoveAggregateCell(TraceNotebookSavedCellRow? row)
    {
        if (row is null) return false;
        SaveActive();
        var index = _pages.FindIndex(page => page.Id == row.PageId);
        if (index < 0) return false;
        var cells = (_pages[index].Cells ?? [])
            .Where(cell => !string.Equals(cell.Id, row.Cell.Id, StringComparison.Ordinal))
            .ToArray();
        if (cells.Length == (_pages[index].Cells?.Count ?? 0)) return false;
        _pages[index] = _pages[index] with { Cells = cells };
        RefreshTabs();
        return true;
    }

    public bool TryOpenCellOwner(TraceNotebookSavedCellRow row)
    {
        var page = _pages.FirstOrDefault(item => item.Id == row.PageId);
        return page is not null && TryOpen(new TraceNotebookTabRow(page, false));
    }

    private void RefreshTabs()
    {
        Tabs.Clear();
        SavedCells.Clear();
        foreach (var page in _pages)
        {
            Tabs.Add(new TraceNotebookTabRow(page, page.Id == _activeId));
            foreach (var cell in page.Cells ?? [])
                SavedCells.Add(new TraceNotebookSavedCellRow(page.Id, page.Title, cell));
        }
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(Summary));
    }
}

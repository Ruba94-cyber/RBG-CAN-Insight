using System.Collections.ObjectModel;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App.ViewModels;

/// <summary>One universal search hit: a DBC message, a decoded signal, a trace identifier or a
/// DID, each with its own way to jump straight to it without knowing in advance which workspace
/// it lives in.</summary>
public sealed record GlobalSearchResult(string Category, string Title, string Subtitle, Action Activate);

/// <summary>Backs the Ctrl+K universal search: live-filters across every searchable catalog the
/// app already builds (DBC messages, decoded signals, trace identifiers, DIDs) instead of making
/// the user guess which of the eight workspaces holds what they are looking for.</summary>
public sealed class GlobalSearchViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _query = string.Empty;
    private int _selectedIndex = -1;

    public GlobalSearchViewModel(MainViewModel main)
    {
        _main = main;
        Refresh();
    }

    public ObservableCollection<GlobalSearchResult> Results { get; } = [];

    public string EmptyStateText => string.IsNullOrWhiteSpace(Query)
        ? "Type a signal name, CAN ID, ECU, DBC message or DID to search."
        : "No results in the loaded data. Check the spelling or load a Trace/DBC first.";

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                Refresh();
            }
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SetProperty(ref _selectedIndex, value);
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            SelectedIndex = -1;
            return;
        }

        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Results.Count - 1);
    }

    /// <summary>Runs the selected result's action and reports whether one was available to run.</summary>
    public bool ActivateSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count)
        {
            return false;
        }

        Results[SelectedIndex].Activate();
        return true;
    }

    private void Refresh()
    {
        Results.Clear();
        foreach (var result in _main.SearchGlobal(Query))
        {
            Results.Add(result);
        }

        SelectedIndex = Results.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(EmptyStateText));
    }
}

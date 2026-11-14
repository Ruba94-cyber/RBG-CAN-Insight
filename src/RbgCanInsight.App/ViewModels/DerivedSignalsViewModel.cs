using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using RbgCanInsight.Analysis.Derived;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App.ViewModels;

/// <summary>A recorded signal that can be bound to an expression variable.</summary>
public sealed record DerivedSignalChoice(string Display, PlotSeriesViewModel Series);

/// <summary>One variable used by the expression and the recorded signal it is bound to.</summary>
public sealed class DerivedVariableRow : ObservableObject
{
    private readonly ListCollectionView _view;
    private DerivedSignalChoice? _selected;
    private string _searchText = string.Empty;

    public DerivedVariableRow(string name, IReadOnlyList<DerivedSignalChoice> choices)
    {
        Name = name;
        Choices = choices;
        _view = new ListCollectionView(choices.ToList()) { Filter = FilterChoice };
    }

    public string Name { get; }
    public string AutomationId => "derived.var." + Name;
    public string AutomationName => $"Recorded signal for variable {Name}";
    public IReadOnlyList<DerivedSignalChoice> Choices { get; }
    public ICollectionView FilteredChoices => _view;

    public DerivedSignalChoice? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            if (value is not null && _searchText != value.Display) SetSearchTextCore(value.Display);
        }
    }

    /// <summary>Text typed into the searchable box. Substring match, case-insensitive; a single remaining
    /// match (or an exact one) is selected automatically.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            value ??= string.Empty;
            if (value == _searchText) return;
            SetSearchTextCore(value);
            if (_selected is not null && _selected.Display != value) Selected = null;
            _view.Refresh();
            if (_selected is null && value.Length > 0)
            {
                var exact = Choices.FirstOrDefault(c => string.Equals(c.Display, value, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) { Selected = exact; return; }
                var matches = Choices.Where(Matches).Take(2).ToList();
                if (matches.Count == 1) Selected = matches[0];
            }
        }
    }

    private void SetSearchTextCore(string value)
    {
        _searchText = value;
        OnPropertyChanged(nameof(SearchText));
    }

    private bool Matches(DerivedSignalChoice c) =>
        _searchText.Length == 0 || (_selected is not null && _selected.Display == _searchText) ||
        c.Display.Contains(_searchText, StringComparison.OrdinalIgnoreCase);

    private bool FilterChoice(object item) => item is DerivedSignalChoice c && Matches(c);
}

/// <summary>State of the "Derived signals" window. Expressions are only ever evaluated by
/// <see cref="ExpressionEngine"/>; no code is compiled or executed.</summary>
public sealed class DerivedSignalsViewModel : ObservableObject
{
    public static readonly string[] ExampleExpressions =
        ["a - b", "rate(speed)", "avg(temp, 2)", "if(speed > 50, 1, 0)"];

    public const string CheatSheet =
        "Operators + - * / % < <= > >= == != && || !   Functions abs min max sqrt pow clamp if   " +
        "Over time: prev(x) delta(x) rate(x) avg(x, seconds)";

    public const string OfflineNote =
        "Derived values are computed offline from recorded samples only. They are not recorded on the bus " +
        "and are never sent to a vehicle.";

    private readonly List<DerivedSignalChoice> _choices;
    private string _expression = string.Empty;
    private string _errorText = string.Empty;
    private string _statusText = "Type an expression above, then choose a recorded signal for each variable.";
    private string _statsText = "No preview yet.";
    private SampleSeries? _result;
    private ExpressionEngine.CompiledExpression? _compiled;

    public DerivedSignalsViewModel(IEnumerable<PlotSeriesViewModel> recorded)
    {
        _choices = recorded
            .Select(s => new DerivedSignalChoice(
                $"{s.MessageName}.{s.SignalName} ({s.ChannelText}, {s.DatabaseName})", s))
            .OrderBy(c => c.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();
        PreviewCommand = new AsyncCommand(PreviewAsync, () => _compiled is not null);
        ExampleCommand = new RelayCommand<string>(e => Expression = e ?? string.Empty);
        ParseExpression();
    }

    public IReadOnlyList<DerivedSignalChoice> Choices => _choices;
    public ObservableCollection<DerivedVariableRow> Variables { get; } = [];
    public IReadOnlyList<string> Examples => ExampleExpressions;
    public string CheatSheetText => CheatSheet;
    public string OfflineNoteText => OfflineNote;
    public AsyncCommand PreviewCommand { get; }
    public RelayCommand<string> ExampleCommand { get; }

    public SampleSeries? Result
    {
        get => _result;
        private set { if (SetProperty(ref _result, value)) { OnPropertyChanged(nameof(HasResult)); } }
    }

    public bool HasResult => _result is { Count: > 0 };

    public string Expression
    {
        get => _expression;
        set
        {
            value ??= string.Empty;
            if (!SetProperty(ref _expression, value)) return;
            ClearResult();
            ParseExpression();
        }
    }

    public string ErrorText { get => _errorText; private set { if (SetProperty(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _errorText.Length > 0;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string StatsText { get => _statsText; private set => SetProperty(ref _statsText, value); }
    public string ExpressionText => _expression.Trim();

    /// <summary>Live parse: updates the error line and the variable table (kept per name).</summary>
    public void ParseExpression()
    {
        _compiled = null;
        if (string.IsNullOrWhiteSpace(_expression))
        {
            ErrorText = string.Empty;
            SyncVariables([]);
        }
        else if (ExpressionEngine.TryCompile(_expression, out var compiled, out var error))
        {
            _compiled = compiled;
            ErrorText = string.Empty;
            SyncVariables(compiled!.RequiredVariables);
            StatusText = compiled.RequiredVariables.Count == 0
                ? "This expression uses no variables, so there is no time grid. Use at least one signal."
                : "Choose a recorded signal for each variable, then press Preview.";
        }
        else
        {
            // The engine position is 0-based; show it 1-based to the user.
            ErrorText = $"Error at character {error!.Position + 1}: {error.Message}";
            StatusText = "Fix the expression to continue.";
        }

        PreviewCommand?.RaiseCanExecuteChanged();
    }

    private void ClearResult()
    {
        if (_result is null) return;
        Result = null;
        StatsText = "No preview yet.";
    }

    private void SyncVariables(IReadOnlyList<string> names)
    {
        for (var i = Variables.Count - 1; i >= 0; i--)
        {
            if (!names.Contains(Variables[i].Name, StringComparer.Ordinal)) Variables.RemoveAt(i);
        }

        foreach (var name in names)
        {
            if (Variables.Any(v => v.Name == name)) continue;
            var row = new DerivedVariableRow(name, _choices);
            var guess = GuessBinding(name);
            if (guess is not null) row.Selected = guess;
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DerivedVariableRow.Selected)) ClearResult(); };
            Variables.Add(row);
        }
    }

    /// <summary>Pre-binds a variable when its name identifies exactly one recorded signal.</summary>
    private DerivedSignalChoice? GuessBinding(string name)
    {
        var matches = _choices.Where(c =>
            string.Equals(c.Series.SignalName, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals($"{c.Series.MessageName}.{c.Series.SignalName}", name, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private async Task PreviewAsync()
    {
        var compiled = _compiled;
        if (compiled is null) return;
        var unbound = Variables.Where(v => v.Selected is null).Select(v => v.Name).ToList();
        if (unbound.Count > 0)
        {
            StatusText = "Choose a recorded signal for: " + string.Join(", ", unbound);
            return;
        }

        var bound = Variables.ToDictionary(v => v.Name, v => v.Selected!.Series.Timeline);
        StatusText = "Computing...";
        try
        {
            var series = await Task.Run(() =>
            {
                var inputs = bound.ToDictionary(p => p.Key, p => DerivedSeriesTools.ToSampleSeries(p.Value));
                return compiled.Evaluate(inputs);
            });
            Result = series;
            StatsText = DerivedSeriesStats.Compute(series).ToText();
            StatusText = series.Count == 0
                ? "The selected signals have no samples."
                : "Preview ready. Values are computed offline and are not recorded.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Result = null;
            StatsText = "No preview yet.";
            StatusText = "Could not compute: " + ex.Message;
        }
    }

    public string BuildCsv() => Result is null ? string.Empty : DerivedSeriesTools.ToCsv(ExpressionText, Result);

    /// <summary>A plottable series for the derived result (not recorded), or null with <paramref name="reason"/>.</summary>
    public RbgCanInsight.Analysis.SignalTimeline? CreateTimeline(out string reason)
    {
        reason = string.Empty;
        if (Result is null) { reason = "Press Preview first."; return null; }
        var timeline = DerivedSeriesTools.ToTimeline(ExpressionText, Result);
        if (timeline.Samples.Count == 0) { reason = "The derived signal has no finite samples to plot."; return null; }
        return timeline;
    }

    public PlotSeriesViewModel? CreatePlotSeries(out string reason)
    {
        reason = string.Empty;
        if (Result is null) { reason = "Press Preview first."; return null; }
        var timeline = DerivedSeriesTools.ToTimeline(ExpressionText, Result);
        if (timeline.Samples.Count == 0) { reason = "The derived signal has no finite samples to plot."; return null; }
        return new PlotSeriesViewModel(timeline);
    }
}

using System.Collections.ObjectModel;
using RbgCanInsight.Analysis;
using RbgCanInsight.App.Common;

namespace RbgCanInsight.App.ViewModels;

public sealed class QueryFieldChoice(QueryField field)
{
    public QueryField Field { get; } = field;
    public string Label { get; } = TraceQueryBuilder.FieldLabel(field);
}

/// <summary>One editable condition row in the guided query builder.</summary>
public sealed class QueryConditionRowViewModel : ObservableObject
{
    private QueryFieldChoice _field;
    private string _value = string.Empty;
    private bool _exclude;
    private bool _orWithPrevious;

    public QueryConditionRowViewModel(QueryFieldChoice field, string value = "", bool exclude = false,
        bool orWithPrevious = false)
    {
        _field = field;
        _value = value;
        _exclude = exclude;
        _orWithPrevious = orWithPrevious;
    }

    public QueryFieldChoice Field
    {
        get => _field;
        set
        {
            if (SetProperty(ref _field, value))
            {
                if (value.Field == QueryField.FrameKind && !FrameKinds.Contains(Value)) Value = FrameKinds[0];
                if (value.Field == QueryField.UdsAny) Value = string.Empty;
                OnPropertyChanged(nameof(NeedsValue));
                OnPropertyChanged(nameof(IsFrameKind));
                OnPropertyChanged(nameof(IsFreeText));
            }
        }
    }

    public string Value { get => _value; set => SetProperty(ref _value, value); }
    public bool Exclude { get => _exclude; set => SetProperty(ref _exclude, value); }
    /// <summary>Starts a new alternative: "A B OR C" means (A and B) or C. Ignored on the first row.</summary>
    public bool OrWithPrevious { get => _orWithPrevious; set => SetProperty(ref _orWithPrevious, value); }
    public bool NeedsValue => Field.Field != QueryField.UdsAny;
    public bool IsFrameKind => Field.Field == QueryField.FrameKind;
    public bool IsFreeText => NeedsValue && !IsFrameKind;
    public IReadOnlyList<string> FrameKinds { get; } = TraceQueryBuilder.FrameKindNames.ToArray();
    public QueryCondition ToCondition() => new(Field.Field, Value, Exclude, OrWithPrevious);
}

/// <summary>Guided Trace filter builder: ANDed conditions with an include/exclude switch, a live
/// preview of the exact filter text and a plain-English reading, plus named reusable queries.</summary>
public sealed class QueryBuilderViewModel : ObservableObject
{
    private readonly SavedQueryStore _store;
    private readonly Action<string> _apply;
    private readonly Action<IReadOnlyList<SavedQuery>>? _savedChanged;
    private string _saveColourName = RowColourRules.NoColourName;
    private string _queryText = string.Empty;
    private string _description = "Every frame (no conditions)";
    private string _messages = string.Empty;
    private string _saveName = string.Empty;
    private SavedQuery? _selectedSaved;
    private bool _isValid = true;
    private RelayCommand? _addCommand, _clearCommand, _applyCommand, _saveCommand, _deleteCommand;
    private RelayCommand<QueryConditionRowViewModel>? _removeCommand;

    public QueryBuilderViewModel(SavedQueryStore store, Action<string> apply, string currentQuery = "",
        Action<IReadOnlyList<SavedQuery>>? savedChanged = null)
    {
        _store = store;
        _apply = apply;
        _savedChanged = savedChanged;
        FieldChoices = Enum.GetValues<QueryField>().Select(field => new QueryFieldChoice(field)).ToArray();
        LoadText(currentQuery);
        foreach (var saved in store.Load()) SavedQueries.Add(saved);
        Conditions.CollectionChanged += (_, _) => Recompute();
        Recompute();
    }

    public IReadOnlyList<QueryFieldChoice> FieldChoices { get; }
    public ObservableCollection<QueryConditionRowViewModel> Conditions { get; } = [];
    public ObservableCollection<SavedQuery> SavedQueries { get; } = [];

    public string QueryText { get => _queryText; private set => SetProperty(ref _queryText, value); }
    public string Description { get => _description; private set => SetProperty(ref _description, value); }
    public string Messages { get => _messages; private set => SetProperty(ref _messages, value); }
    public bool IsValid { get => _isValid; private set => SetProperty(ref _isValid, value); }
    public string SaveName { get => _saveName; set => SetProperty(ref _saveName, value); }

    /// <summary>Row colour paints Trace rows matching the saved query ("None" = a plain saved query).</summary>
    public IReadOnlyList<string> ColourChoices { get; } =
        [RowColourRules.NoColourName, .. RowColourRules.Palette.Select(item => item.Name)];

    public string SaveColourName { get => _saveColourName; set => SetProperty(ref _saveColourName, value); }

    public SavedQuery? SelectedSaved
    {
        get => _selectedSaved;
        set
        {
            if (SetProperty(ref _selectedSaved, value) && value is not null)
            {
                SaveName = value.Name;
                SaveColourName = RowColourRules.NameForHex(value.Colour);
                LoadText(value.Query);
            }
        }
    }

    public RelayCommand AddConditionCommand => _addCommand ??= new RelayCommand(() => AddRow(new QueryConditionRowViewModel(
        FieldChoices.First(choice => choice.Field == QueryField.CanId))));

    public RelayCommand<QueryConditionRowViewModel> RemoveConditionCommand =>
        _removeCommand ??= new RelayCommand<QueryConditionRowViewModel>(row =>
        {
            if (row is not null) { row.PropertyChanged -= OnRowChanged; Conditions.Remove(row); }
        });

    public RelayCommand ClearCommand => _clearCommand ??= new RelayCommand(() =>
    {
        foreach (var row in Conditions) row.PropertyChanged -= OnRowChanged;
        Conditions.Clear();
    });

    public RelayCommand ApplyCommand => _applyCommand ??= new RelayCommand(() =>
    {
        if (IsValid) _apply(QueryText);
    }, () => IsValid);

    public RelayCommand SaveCommand => _saveCommand ??= new RelayCommand(() =>
    {
        if (!IsValid || SaveName.Trim().Length == 0) return;
        var items = _store.Upsert(SaveName, QueryText, RowColourRules.HexForName(SaveColourName));
        ReplaceSaved(items);
        _savedChanged?.Invoke(items);
        _selectedSaved = items.FirstOrDefault(item => item.Name.Equals(SaveName.Trim(), StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(SelectedSaved));
    });

    public RelayCommand DeleteSavedCommand => _deleteCommand ??= new RelayCommand(() =>
    {
        if (SelectedSaved is null) return;
        var remaining = _store.Remove(SelectedSaved.Name);
        ReplaceSaved(remaining);
        _savedChanged?.Invoke(remaining);
        _selectedSaved = null;
        OnPropertyChanged(nameof(SelectedSaved));
    });

    /// <summary>Loads filter text into the rows. Terms the builder cannot express are reported, never dropped silently.</summary>
    public void LoadText(string query)
    {
        foreach (var row in Conditions) row.PropertyChanged -= OnRowChanged;
        Conditions.Clear();
        var conditions = TraceQueryBuilder.Parse(query, out var unrecognized);
        foreach (var condition in conditions)
        {
            AddRow(new QueryConditionRowViewModel(
                FieldChoices.First(choice => choice.Field == condition.Field), condition.Value, condition.Exclude,
                condition.OrWithPrevious));
        }

        if (unrecognized.Count > 0)
        {
            Messages = "These terms are not supported by the builder and were not loaded: " +
                       string.Join(' ', unrecognized);
        }
    }

    private void AddRow(QueryConditionRowViewModel row)
    {
        row.PropertyChanged += OnRowChanged;
        Conditions.Add(row);
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Recompute();

    private void Recompute()
    {
        var conditions = Conditions.Select(row => row.ToCondition()).ToArray();
        var result = TraceQueryBuilder.Compose(conditions);
        QueryText = result.Query;
        Description = TraceQueryBuilder.Describe(conditions);
        IsValid = result.IsValid;
        var lines = result.Errors.Concat(result.Warnings).ToArray();
        Messages = lines.Length == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
        ApplyCommand.RaiseCanExecuteChanged();
    }

    private void ReplaceSaved(IEnumerable<SavedQuery> items)
    {
        SavedQueries.Clear();
        foreach (var item in items) SavedQueries.Add(item);
    }
}

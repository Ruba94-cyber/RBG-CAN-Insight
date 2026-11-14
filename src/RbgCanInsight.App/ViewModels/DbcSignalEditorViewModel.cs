using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using RbgCanInsight.App.Common;
using RbgCanInsight.Dbc;

namespace RbgCanInsight.App.ViewModels;

public sealed record DbcEditReviewRow(string Identifier, string Message, string Signal, string Differences);

/// <summary>One editable VAL_ raw integer code and the meaning declared by DBC.</summary>
public sealed class DbcNamedValueEditorRow : ObservableObject
{
    private string _rawCode;
    private string _meaning;

    public DbcNamedValueEditorRow(string rawCode, string meaning)
    {
        _rawCode = rawCode;
        _meaning = meaning;
    }

    public string RawCode { get => _rawCode; set => SetProperty(ref _rawCode, value); }
    public string Meaning { get => _meaning; set => SetProperty(ref _meaning, value); }
}

/// <summary>
/// DBC engineering draft: scalar SG_ editing never changes the active source
/// decoder or the original input file. Stage -> review diff -> validated Save As.
/// A future full editor will manage renames, multiplexing, enums and OEM attributes.
/// </summary>
public sealed class DbcSignalEditorViewModel : ObservableObject
{
    private sealed record WorkingSession(DbcDatabase Database, string Path, DbcWorkingCopy Copy);
    private readonly Dictionary<string, WorkingSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private WorkingSession? _session;
    private DbcMessageIdentity? _identity;
    private string _signalName = string.Empty;

    private string _startBitText = string.Empty;
    private string _bitLengthText = string.Empty;
    private string _factorText = string.Empty;
    private string _offsetText = string.Empty;
    private string _minimumText = string.Empty;
    private string _maximumText = string.Empty;
    private string _unitText = string.Empty;
    private int _byteOrderIndex;
    private bool _isSigned;
    private string _statusText = "Select a DBC signal to prepare a safe working-copy edit.";
    private string _previewText = string.Empty;
    private string _selectedSummary = "No signal selected.";
    private IReadOnlyList<DbcEditReviewRow> _changeRows = [];
    private DbcNamedValueEditorRow? _selectedNamedValue;

    public ObservableCollection<DbcNamedValueEditorRow> NamedValueRows { get; } = [];
    public DbcNamedValueEditorRow? SelectedNamedValue
    {
        get => _selectedNamedValue;
        set => SetProperty(ref _selectedNamedValue, value);
    }
    public string NamedValueSummary => _identity is null
        ? "Select a DBC signal to inspect its defined raw codes."
        : $"{NamedValueRows.Count} raw value meaning(s). " +
          "Edit the grid and select Stage VAL_ values. Raw codes accept decimal or 0x hexadecimal.";

    public string StartBitText { get => _startBitText; set => SetProperty(ref _startBitText, value); }
    public string BitLengthText { get => _bitLengthText; set => SetProperty(ref _bitLengthText, value); }
    public string FactorText { get => _factorText; set => SetProperty(ref _factorText, value); }
    public string OffsetText { get => _offsetText; set => SetProperty(ref _offsetText, value); }
    public string MinimumText { get => _minimumText; set => SetProperty(ref _minimumText, value); }
    public string MaximumText { get => _maximumText; set => SetProperty(ref _maximumText, value); }
    public string UnitText { get => _unitText; set => SetProperty(ref _unitText, value); }
    public int ByteOrderIndex { get => _byteOrderIndex; set => SetProperty(ref _byteOrderIndex, value); }
    public bool IsSigned { get => _isSigned; set => SetProperty(ref _isSigned, value); }
    public IReadOnlyList<string> ByteOrderChoices { get; } = ["Intel / little-endian", "Motorola / big-endian"];
    public string SelectedSummary { get => _selectedSummary; private set => SetProperty(ref _selectedSummary, value); }
    public string PreviewText { get => _previewText; private set => SetProperty(ref _previewText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public IReadOnlyList<DbcEditReviewRow> ChangeRows
    {
        get => _changeRows;
        private set => SetProperty(ref _changeRows, value);
    }

    public bool CanEdit => _session is not null && _identity is not null &&
        string.Equals(Path.GetExtension(_session.Path), ".dbc", StringComparison.OrdinalIgnoreCase);
    public bool CanSave => CanEdit && _session!.Copy.IsModified;
    public bool CanUndo => CanEdit && _session!.Copy.CanUndo;
    public bool CanRedo => CanEdit && _session!.Copy.CanRedo;
    public bool HasStagedChanges => _sessions.Values.Any(session => session.Copy.IsModified);
    public string ChangesSummary => _session is null ? "No database selected." :
        _session.Copy.Changes.Count == 0
            ? "No staged edits. Original DBC and active decoder are unchanged."
            : $"{_session.Copy.Changes.Count} staged signal(s). Active decoder and original DBC remain unchanged until the saved copy is loaded.";

    public RelayCommand AddNamedValueCommand { get; }
    public RelayCommand RemoveNamedValueCommand { get; }
    public RelayCommand StageNamedValuesCommand { get; }
    public RelayCommand StageCommand { get; }
    public RelayCommand RevertSignalCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand DiscardAllCommand { get; }
    public RelayCommand SaveAsCommand { get; }

    public DbcSignalEditorViewModel()
    {
        AddNamedValueCommand = new RelayCommand(AddNamedValue);
        RemoveNamedValueCommand = new RelayCommand(RemoveNamedValue);
        StageNamedValuesCommand = new RelayCommand(StageNamedValues);
        StageCommand = new RelayCommand(Stage);
        RevertSignalCommand = new RelayCommand(RevertSelected);
        UndoCommand = new RelayCommand(UndoStaged);
        RedoCommand = new RelayCommand(RedoStaged);
        DiscardAllCommand = new RelayCommand(DiscardAll);
        SaveAsCommand = new RelayCommand(SaveAs);
    }

    public bool HasStagedChangesFor(string databaseName) =>
        _sessions.TryGetValue(databaseName, out var state) && state.Copy.IsModified;

    public void ForgetDatabase(string databaseName)
    {
        if (_session?.Database.Name == databaseName)
            Select(null, null, null, null);
        _sessions.Remove(databaseName);
        NotifyChanges();
    }

    public void ForgetAll()
    {
        _sessions.Clear();
        Select(null, null, null, null);
    }

    /// <summary>Bind the selected source definition. Draft changes are retained
    /// per loaded DB while navigating, but discarded if that DB instance is reloaded.</summary>
    public void Select(DbcDatabase? database, DbcMessage? message, DbcSignal? signal, string? path)
    {
        _identity = null;
        _signalName = string.Empty;
        _session = null;
        if (database is null || message is null || signal is null || string.IsNullOrWhiteSpace(path))
        {
            SelectedSummary = "No signal selected.";
            PreviewText = string.Empty;
            NamedValueRows.Clear();
            SelectedNamedValue = null;
            OnPropertyChanged(nameof(NamedValueSummary));
            StatusText = "Select a DBC signal in the signal table, then choose this editor tab.";
            NotifyChanges();
            return;
        }

        if (!_sessions.TryGetValue(database.Name, out var state) ||
            !ReferenceEquals(state.Database, database) ||
            !string.Equals(state.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            state = new WorkingSession(database, path, new DbcWorkingCopy(database));
            _sessions[database.Name] = state;
        }
        _session = state;
        _identity = new DbcMessageIdentity(message.Identifier, message.IsExtended);
        _signalName = signal.Name;
        SelectedSummary = $"{database.Name} / {message.Name}.{signal.Name} / " +
            $"0x{message.Identifier:X} ({(message.IsExtended ? "29" : "11")}-bit)";
        LoadSelectedFields();
        StatusText = CanEdit
            ? "Edit the fields and select Stage. Review the changes below; Save As never overwrites the original."
            : "ARXML editing is not supported by the safe DBC writer. This source remains read-only.";
        NotifyChanges();
    }

    private void LoadSelectedFields()
    {
        if (_session is null || _identity is not { } identity) return;
        if (!_session.Copy.TryGetMessage(identity, out var message))
            return;
        var signal = message.Signals.SingleOrDefault(item => item.Name == _signalName);
        if (signal is null) return;

        StartBitText = signal.StartBit.ToString(CultureInfo.InvariantCulture);
        BitLengthText = signal.BitLength.ToString(CultureInfo.InvariantCulture);
        ByteOrderIndex = signal.ByteOrder == DbcByteOrder.Intel ? 0 : 1;
        IsSigned = signal.IsSigned;
        FactorText = Format(signal.Factor);
        OffsetText = Format(signal.Offset);
        MinimumText = Format(signal.Minimum);
        MaximumText = Format(signal.Maximum);
        UnitText = signal.Unit;
        NamedValueRows.Clear();
        foreach (var entry in (signal.ValueDescriptions ?? new Dictionary<long, string>())
                     .OrderBy(entry => entry.Key))
            NamedValueRows.Add(new DbcNamedValueEditorRow(
                entry.Key.ToString(CultureInfo.InvariantCulture), entry.Value));
        SelectedNamedValue = NamedValueRows.FirstOrDefault();
        OnPropertyChanged(nameof(NamedValueSummary));
        var layout = DbcSignalInspector.Inspect(message, signal);
        var validation = DbcEngineeringValidator.Validate(message)
            .Where(issue => issue.Signal == signal.Name || issue.Signal == "(message)")
            .Take(8)
            .ToArray();
        PreviewText = "Staged bit layout: " + layout.SelectedBoundsText +
            (layout.Warnings.Count > 0 ? " · Layout: " + string.Join("; ", layout.Warnings) : "") +
            (validation.Length > 0
                ? " · Engineering checks: " + string.Join("; ",
                    validation.Select(issue => $"{issue.Severity}: {issue.Code} — {issue.Detail}"))
                : " · No engineering checks triggered for this signal.");
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static double ParseDouble(string input, string label)
    {
        if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) &&
            !double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out result))
            throw new FormatException($"{label} must be a valid number.");
        if (!double.IsFinite(result))
            throw new FormatException($"{label} must be finite.");
        return result;
    }

    private static int ParseInt(string input, string label)
    {
        if (!int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            throw new FormatException($"{label} must be a whole number.");
        return result;
    }

    private void Stage()
    {
        if (!CanEdit || _identity is not { } identity || _session is null)
        {
            StatusText = "Select a writable .dbc source signal first.";
            return;
        }
        try
        {
            if (!_session.Copy.TryGetMessage(identity, out var message))
                throw new InvalidOperationException("Selected CAN identity is unavailable.");
            var current = message.Signals.Single(signal => signal.Name == _signalName);
            var proposed = current with
            {
                StartBit = ParseInt(StartBitText, "Start bit"),
                BitLength = ParseInt(BitLengthText, "Signal length"),
                ByteOrder = ByteOrderIndex switch
                {
                    0 => DbcByteOrder.Intel,
                    1 => DbcByteOrder.Motorola,
                    _ => throw new ArgumentException("Select a valid byte order.")
                },
                IsSigned = IsSigned,
                Factor = ParseDouble(FactorText, "Factor"),
                Offset = ParseDouble(OffsetText, "Offset"),
                Minimum = ParseDouble(MinimumText, "Minimum"),
                Maximum = ParseDouble(MaximumText, "Maximum"),
                Unit = UnitText ?? string.Empty
            };
            _session.Copy.StageSignal(identity, _signalName, proposed);
            LoadSelectedFields();
            NotifyChanges();
            StatusText = _session.Copy.Changes.Count == 0
                ? "Selected signal matches the source; there is no pending change."
                : $"Staged {message.Name}.{_signalName}; verify the diff and Save As when ready.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException)
        {
            StatusText = "Cannot stage signal: " + error.Message;
        }
    }


    private static long ParseRawCode(string value)
    {
        var input = value.Trim();
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var digits = input[2..];
            if (digits.Length is < 1 or > 16 ||
                !ulong.TryParse(digits, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var raw) || raw > long.MaxValue)
                throw new FormatException("Hexadecimal raw codes must be within signed Int64.");
            return (long)raw;
        }
        if (!long.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            throw new FormatException("Raw code must be a decimal integer or 0x hexadecimal.");
        return result;
    }

    private void AddNamedValue()
    {
        if (!CanEdit) return;
        var taken = NamedValueRows.Select(row =>
        {
            try { return (long?)ParseRawCode(row.RawCode); }
            catch (FormatException) { return null; }
        }).Where(value => value.HasValue).Select(value => value!.Value).ToHashSet();
        long candidate = 0;
        while (taken.Contains(candidate) && candidate < 4_096) candidate++;
        var created = new DbcNamedValueEditorRow(
            candidate.ToString(CultureInfo.InvariantCulture), "");
        NamedValueRows.Add(created);
        SelectedNamedValue = created;
        OnPropertyChanged(nameof(NamedValueSummary));
        StatusText = "Enter the meaning for the new raw code, then Stage VAL_ values.";
    }

    private void RemoveNamedValue()
    {
        if (!CanEdit || SelectedNamedValue is not { } row) return;
        NamedValueRows.Remove(row);
        SelectedNamedValue = NamedValueRows.FirstOrDefault();
        OnPropertyChanged(nameof(NamedValueSummary));
        StatusText = "Value removed from draft grid. Use Stage VAL_ values to apply.";
    }

    private void StageNamedValues()
    {
        if (!CanEdit || _session is null || _identity is not { } identity)
        {
            StatusText = "Select a writable DBC signal first.";
            return;
        }
        try
        {
            var values = new Dictionary<long, string>();
            foreach (var row in NamedValueRows)
            {
                var raw = ParseRawCode(row.RawCode);
                if (!values.TryAdd(raw, row.Meaning))
                    throw new ArgumentException($"Duplicate raw code: {raw}.");
            }
            _session.Copy.StageNamedValues(identity, _signalName, values);
            LoadSelectedFields();
            NotifyChanges();
            StatusText = "Staged VAL_ meanings for " + _signalName +
                ". Review the changes and use Save DBC copy to persist safely.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or FormatException)
        {
            StatusText = "VAL_ values not staged: " + error.Message;
        }
    }

    private void RevertSelected()
    {
        if (_session is null || _identity is not { } identity) return;
        try
        {
            _session.Copy.RevertSignal(identity, _signalName);
            LoadSelectedFields();
            NotifyChanges();
            StatusText = "Reverted selected signal to its original source definition.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            StatusText = "Could not revert selected signal: " + error.Message;
        }
    }

    private void UndoStaged()
    {
        if (!CanUndo || _session is null || !_session.Copy.Undo()) return;
        LoadSelectedFields();
        NotifyChanges();
        StatusText = "Undid the latest staged DBC edit. Source and active decoder were not modified.";
    }

    private void RedoStaged()
    {
        if (!CanRedo || _session is null || !_session.Copy.Redo()) return;
        LoadSelectedFields();
        NotifyChanges();
        StatusText = "Restored the latest staged DBC edit. Review the draft before Save As.";
    }

    private void DiscardAll()
    {
        if (_session is null || !_session.Copy.IsModified) return;
        if (MessageBox.Show("Discard all staged edits AND undo/redo history in this DBC? Original file is not affected.",
                "Discard DBC working copy", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        _session.Copy.RevertAll();
        LoadSelectedFields();
        NotifyChanges();
        StatusText = "Discarded all staged edits for this DBC.";
    }

    private void SaveAs()
    {
        if (!CanSave || _session is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Vector CAN database (*.dbc)|*.dbc",
            DefaultExt = ".dbc",
            AddExtension = true,
            OverwritePrompt = false,
            CheckFileExists = false,
            FileName = Path.GetFileNameWithoutExtension(_session.Path) + "-edited.dbc",
            Title = "Save reviewed DBC modifications as a new file (source protected)"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            SaveToPath(dialog.FileName);
        }
        catch (Exception error)
        {
            StatusText = "DBC Save As refused: " + error.Message;
            MessageBox.Show(error.Message, "DBC not saved", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Windows GUI verification uses this path directly without an interactive
    /// SaveFileDialog. All source-preserving checks and no-overwrite rules still apply.</summary>
    public void SaveToPath(string destination)
    {
        if (!CanSave || _session is null)
            throw new InvalidOperationException("Stage at least one DBC signal before Save As.");
        DbcSurgicalSaveAs.WriteNewFile(_session.Path, destination, _session.Copy);
        StatusText = "New DBC saved: " + destination +
            ". Load this copy to analyze with the edited definitions. The source remains unchanged.";
    }

    private void NotifyChanges()
    {
        ChangeRows = _session?.Copy.Changes.Select(change =>
            new DbcEditReviewRow(
                $"0x{change.MessageIdentity.Identifier:X} " +
                    (change.MessageIdentity.IsExtended ? "29-bit" : "11-bit"),
                change.MessageName, change.SignalName, Difference(change.Before, change.After)))
            .ToArray() ?? [];
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(HasStagedChanges));
        OnPropertyChanged(nameof(ChangesSummary));
    }

    private static string Difference(DbcSignal old, DbcSignal next)
    {
        var differences = new List<string>();
        if (old.StartBit != next.StartBit) differences.Add($"start {old.StartBit} → {next.StartBit}");
        if (old.BitLength != next.BitLength) differences.Add($"bits {old.BitLength} → {next.BitLength}");
        if (old.ByteOrder != next.ByteOrder) differences.Add($"order {old.ByteOrder} → {next.ByteOrder}");
        if (old.IsSigned != next.IsSigned) differences.Add($"signed {old.IsSigned} → {next.IsSigned}");
        if (old.Factor != next.Factor) differences.Add($"factor {Format(old.Factor)} → {Format(next.Factor)}");
        if (old.Offset != next.Offset) differences.Add($"offset {Format(old.Offset)} → {Format(next.Offset)}");
        if (old.Minimum != next.Minimum) differences.Add($"min {Format(old.Minimum)} → {Format(next.Minimum)}");
        if (old.Maximum != next.Maximum) differences.Add($"max {Format(old.Maximum)} → {Format(next.Maximum)}");
        if (old.Unit != next.Unit) differences.Add($"unit {old.Unit} → {next.Unit}");
        var oldValues = old.ValueDescriptions ?? new Dictionary<long, string>();
        var nextValues = next.ValueDescriptions ?? new Dictionary<long, string>();
        if (oldValues.Count != nextValues.Count ||
            oldValues.Any(item => !nextValues.TryGetValue(item.Key, out var meaning) ||
                !string.Equals(item.Value, meaning, StringComparison.Ordinal)))
            differences.Add($"VAL_ meanings {oldValues.Count} → {nextValues.Count} (labels/codes changed)");
        return string.Join("; ", differences);
    }
}

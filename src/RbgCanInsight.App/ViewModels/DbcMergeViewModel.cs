using System.Collections.ObjectModel;
using System.IO;
using RbgCanInsight.App.Common;
using RbgCanInsight.Dbc;

namespace RbgCanInsight.App.ViewModels;

/// <summary>One reviewable merge decision. Additions default to "take incoming"; changes that
/// would replace a base definition default to "keep base" and need an explicit choice.</summary>
public sealed class DbcMergeItemRowViewModel : ObservableObject
{
    private bool _takeIncoming;

    public DbcMergeItemRowViewModel(DbcMergeItem item)
    {
        Item = item;
        _takeIncoming = item.DefaultDecision == DbcMergeDecision.TakeIncoming;
    }

    public DbcMergeItem Item { get; }
    public string Id => Item.Id;
    public string KindText => Item.Kind switch
    {
        DbcMergeKind.AddMessage => "Add message",
        DbcMergeKind.AddSignal => "Add signal",
        DbcMergeKind.ChangeMessage => "Change message",
        _ => "Change signal"
    };
    public string Frame => $"{Item.FrameFormat} {Item.IdentifierText}";
    public string MessageName => Item.MessageName;
    public string Signal => Item.Signal;
    public string Summary => Item.Summary;
    public bool IsChange => Item.Kind is DbcMergeKind.ChangeMessage or DbcMergeKind.ChangeSignal;
    public bool TakeIncoming { get => _takeIncoming; set => SetProperty(ref _takeIncoming, value); }
}

/// <summary>Non-destructive DBC merge workflow: choose base and incoming version, review every
/// difference, save a NEW merged DBC. Both source files stay untouched.</summary>
public sealed class DbcMergeViewModel : ObservableObject
{
    private string _basePath = string.Empty;
    private string _incomingPath = string.Empty;
    private string _status = "Choose the base DBC and the incoming DBC version to merge.";
    private bool _isBusy;
    private DbcMergePlan? _plan;
    private RelayCommand? _chooseBase;
    private RelayCommand? _chooseIncoming;
    private RelayCommand? _acceptAdditions;
    private RelayCommand? _keepBaseForAll;
    private RelayCommand? _takeIncomingForAll;
    private AsyncCommand? _save;

    public ObservableCollection<DbcMergeItemRowViewModel> Rows { get; } = [];

    public string BasePath
    {
        get => _basePath;
        private set { if (SetProperty(ref _basePath, value)) OnPropertyChanged(nameof(BaseFileName)); }
    }

    public string IncomingPath
    {
        get => _incomingPath;
        private set { if (SetProperty(ref _incomingPath, value)) OnPropertyChanged(nameof(IncomingFileName)); }
    }

    public string BaseFileName => BasePath.Length == 0 ? "(not chosen)" : Path.GetFileName(BasePath);
    public string IncomingFileName => IncomingPath.Length == 0 ? "(not chosen)" : Path.GetFileName(IncomingPath);
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasPlan => _plan is not null;
    public bool CanSave => _plan is not null && Rows.Count > 0 && !_isBusy;

    public string PlanSummary => _plan is null ? string.Empty :
        $"{Rows.Count(row => row.Item.Kind is DbcMergeKind.AddMessage or DbcMergeKind.AddSignal)} additions, " +
        $"{Rows.Count(row => row.IsChange)} conflicting changes, " +
        $"{_plan.KeptBaseOnly} base-only definitions kept (a merge never deletes), " +
        $"{_plan.NotMergeable.Count} ambiguous (not merged)";

    public IReadOnlyList<string> NotMergeable => _plan?.NotMergeable ?? [];

    /// <summary>Raised when a native file picker is needed; overridable for tests.</summary>
    public Func<string, string?> PickOpenFile { get; set; } = title =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "Vector CAN database (*.dbc)|*.dbc",
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };

    public Func<string, string?> PickSaveFile { get; set; } = suggested =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save merged DBC",
            Filter = "Vector CAN database (*.dbc)|*.dbc",
            DefaultExt = ".dbc",
            AddExtension = true,
            OverwritePrompt = false,
            FileName = suggested
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };

    public RelayCommand ChooseBaseCommand => _chooseBase ??= new RelayCommand(() =>
    {
        if (PickOpenFile("Choose the base DBC (kept as the starting point)") is { } path) _ = SetBaseAsync(path);
    });

    public RelayCommand ChooseIncomingCommand => _chooseIncoming ??= new RelayCommand(() =>
    {
        if (PickOpenFile("Choose the incoming DBC version to merge in") is { } path) _ = SetIncomingAsync(path);
    });

    public RelayCommand AcceptAdditionsCommand => _acceptAdditions ??= new RelayCommand(() =>
    {
        foreach (var row in Rows) row.TakeIncoming = !row.IsChange;
        Status = "Additions selected; conflicting changes keep the base definition.";
    }, () => Rows.Count > 0);

    public RelayCommand KeepBaseForAllCommand => _keepBaseForAll ??= new RelayCommand(() =>
    {
        foreach (var row in Rows) row.TakeIncoming = false;
        Status = "Nothing selected: the merged file would equal the base DBC.";
    }, () => Rows.Count > 0);

    public RelayCommand TakeIncomingForAllCommand => _takeIncomingForAll ??= new RelayCommand(() =>
    {
        foreach (var row in Rows) row.TakeIncoming = true;
        Status = "Every difference selected, including changes that replace base definitions.";
    }, () => Rows.Count > 0);

    public AsyncCommand SaveCommand => _save ??= new AsyncCommand(SaveAsync, () => CanSave);

    public Task SetBaseAsync(string path) { BasePath = path; return PlanAsync(); }
    public Task SetIncomingAsync(string path) { IncomingPath = path; return PlanAsync(); }

    private async Task PlanAsync()
    {
        _plan = null;
        Rows.Clear();
        NotifyPlanChanged();
        if (BasePath.Length == 0 || IncomingPath.Length == 0) return;
        if (string.Equals(Path.GetFullPath(BasePath), Path.GetFullPath(IncomingPath), StringComparison.OrdinalIgnoreCase))
        {
            Status = "Base and incoming must be two different files.";
            return;
        }
        _isBusy = true;
        Status = "Comparing definitions…";
        NotifyPlanChanged();
        try
        {
            var basePath = BasePath;
            var incomingPath = IncomingPath;
            var plan = await Task.Run(() => DbcMerge.PlanFiles(basePath, incomingPath));
            _plan = plan;
            foreach (var item in plan.Items) Rows.Add(new DbcMergeItemRowViewModel(item));
            Status = plan.Items.Count == 0
                ? "No mergeable differences: the incoming DBC adds or changes nothing relative to the base."
                : "Review each difference, then save the merged DBC as a new file.";
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException
            or InvalidDataException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            Status = "Cannot merge: " + exception.Message;
        }
        finally
        {
            _isBusy = false;
            NotifyPlanChanged();
        }
    }

    private async Task SaveAsync()
    {
        if (_plan is null) return;
        var suggested = Path.GetFileNameWithoutExtension(BasePath) + "-merged.dbc";
        if (PickSaveFile(suggested) is not { } destination) return;
        var decisions = Rows.ToDictionary(row => row.Id,
            row => row.TakeIncoming ? DbcMergeDecision.TakeIncoming : DbcMergeDecision.KeepBase);
        _isBusy = true;
        NotifyPlanChanged();
        try
        {
            var basePath = BasePath;
            var incomingPath = IncomingPath;
            var result = await Task.Run(() => DbcMerge.WriteMergedFile(basePath, incomingPath, destination, decisions));
            Status = $"Merged DBC saved as {Path.GetFileName(destination)}: {result.Applied.Count} selected " +
                     $"difference(s) applied, validated by re-parsing. Both source files are unchanged.";
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException
            or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Status = "Merged DBC not saved: " + exception.Message;
        }
        finally
        {
            _isBusy = false;
            NotifyPlanChanged();
        }
    }

    private void NotifyPlanChanged()
    {
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(NotMergeable));
        _save?.RaiseCanExecuteChanged();
        _acceptAdditions?.RaiseCanExecuteChanged();
        _keepBaseForAll?.RaiseCanExecuteChanged();
        _takeIncomingForAll?.RaiseCanExecuteChanged();
    }
}

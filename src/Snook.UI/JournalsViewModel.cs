using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.UI;

public sealed partial class MainWindowViewModel
{
    private IReadOnlyList<Journal> _journals = [];
    private Guid _selectedJournalId;
    private bool _showDeletedJournalItems;
    private bool _reconcilingJournals;
    private int _journalLoadVersion;
    private string? _journalNextPage;
    private string _journalSearch = string.Empty;
    private string _journalTag = string.Empty;
    private string _appliedJournalSearch = string.Empty;
    private string _appliedJournalTag = string.Empty;
    private readonly JournalOperation _journalLifecycleOperation = new();
    public bool IsJournalsVisible => CurrentSection == "Journals";
    public ObservableCollection<JournalOption> JournalOptions { get; } = [];
    public ObservableCollection<JournalEntryRowViewModel> JournalEntries { get; } = [];
    public Guid SelectedJournalId
    {
        get => _selectedJournalId;
        set { if (SetField(ref _selectedJournalId, value) && !_reconcilingJournals) { NotifyJournalState(); _ = LoadJournalEntriesAsync(); } }
    }
    public bool ShowDeletedJournalItems { get => _showDeletedJournalItems; set { if (SetField(ref _showDeletedJournalItems, value)) _ = LoadJournalsAsync(); } }
    public string JournalSearch { get => _journalSearch; set => SetField(ref _journalSearch, value); }
    public string JournalTag { get => _journalTag; set => SetField(ref _journalTag, value); }
    private Journal? SelectedJournal => _journals.FirstOrDefault(journal => journal.Id == SelectedJournalId);
    public bool CanManageJournal => SelectedJournal is not null;
    public bool CanEditJournal => SelectedJournal is { DeletedAtUtc: null };
    public bool CanWriteJournalEntry => SelectedJournalId == Guid.Empty ? _journals.Any(journal => journal.DeletedAtUtc is null) : CanEditJournal;
    public bool HasNoJournals => _journals.Count == 0;
    public bool HasNoJournalEntries => JournalEntries.Count == 0 && !HasNoJournals;
    public bool HasMoreJournalEntries => _journalNextPage is not null;
    public string JournalDeleteLabel => SelectedJournal?.DeletedAtUtc is null ? "Delete journal" : "Restore journal";
    public string JournalContext => SelectedJournal is { } journal
        ? journal.DeletedAtUtc is not null ? "Deleted journal · restore it to write again." : journal.Description
        : "Your words, collected across journals.";
    public ICommand NewJournalCommand { get; private set; } = null!;
    public ICommand EditJournalCommand { get; private set; } = null!;
    public ICommand DeleteJournalCommand { get; private set; } = null!;
    public ICommand NewJournalEntryCommand { get; private set; } = null!;
    public ICommand FilterJournalEntriesCommand { get; private set; } = null!;
    public ICommand MoreJournalEntriesCommand { get; private set; } = null!;

    private void InitializeJournalCommands()
    {
        NewJournalCommand = new AsyncCommand(_ => { OpenJournalEditor(null); return Task.CompletedTask; });
        EditJournalCommand = new AsyncCommand(_ => { if (CanEditJournal) OpenJournalEditor(SelectedJournal); return Task.CompletedTask; });
        DeleteJournalCommand = new AsyncCommand(_ => ChangeJournalLifecycleAsync());
        NewJournalEntryCommand = new AsyncCommand(_ => { if (CanWriteJournalEntry) OpenJournalEntryEditor(null); return Task.CompletedTask; });
        FilterJournalEntriesCommand = new AsyncCommand(async _ =>
        {
            _appliedJournalSearch = JournalSearch;
            _appliedJournalTag = JournalTag;
            await LoadJournalEntriesAsync();
        });
        MoreJournalEntriesCommand = new AsyncCommand(_ => LoadJournalEntriesAsync(append: true));
    }

    private async Task LoadJournalsAsync()
    {
        var version = ++_journalLoadVersion;
        try
        {
            var journals = await _backend.GetJournalsAsync(ShowDeletedJournalItems);
            if (version != _journalLoadVersion) return;
            _journals = journals;
            var selected = journals.Any(journal => journal.Id == SelectedJournalId) ? SelectedJournalId : Guid.Empty;
            _reconcilingJournals = true;
            try
            {
                ReconcileOptions(JournalOptions, new[] { new JournalOption(Guid.Empty, "All journals") }.Concat(journals.Select(journal =>
                    new JournalOption(journal.Id, journal.Name + (journal.DeletedAtUtc is null ? string.Empty : " · deleted")))), option => option.Id);
                SelectedJournalId = selected;
            }
            finally { _reconcilingJournals = false; }
            RaisePropertyChanged(nameof(SelectedJournalId));
            NotifyJournalState();
            await LoadJournalEntriesAsync();
        }
        catch (Exception exception) { StatusMessage = exception.Message; }
    }

    private async Task LoadJournalEntriesAsync(bool append = false)
    {
        if (append && _journalNextPage is null) return;
        var version = ++_journalLoadVersion;
        try
        {
            var page = await _backend.GetJournalEntriesAsync(new JournalEntryQuery(SelectedJournalId == Guid.Empty ? null : SelectedJournalId,
                _appliedJournalSearch, _appliedJournalTag, ShowDeletedJournalItems, 30, append ? _journalNextPage : null));
            if (version != _journalLoadVersion) return;
            if (!append)
            {
                var ids = page.Items.Select(entry => entry.Id).ToHashSet();
                foreach (var row in JournalEntries.Where(row => !ids.Contains(row.Entry.Id)).ToArray()) JournalEntries.Remove(row);
            }
            for (var index = 0; index < page.Items.Count; index++)
            {
                var entry = page.Items[index];
                var journal = _journals.FirstOrDefault(item => item.Id == entry.JournalId);
                var row = JournalEntries.FirstOrDefault(item => item.Entry.Id == entry.Id);
                if (row is null)
                {
                    row = new JournalEntryRowViewModel(entry, journal, OpenJournalEntryEditor, ChangeJournalEntryLifecycleAsync);
                    if (append) JournalEntries.Add(row); else JournalEntries.Insert(index, row);
                }
                else
                {
                    row.Refresh(entry, journal);
                    if (!append && JournalEntries.IndexOf(row) != index) JournalEntries.Move(JournalEntries.IndexOf(row), index);
                }
            }
            _journalNextPage = page.ContinuationToken;
            NotifyJournalState();
        }
        catch (Exception exception) { StatusMessage = exception.Message; }
    }

    private void NotifyJournalState()
    {
        foreach (var property in new[] { nameof(CanManageJournal), nameof(CanEditJournal), nameof(CanWriteJournalEntry), nameof(HasNoJournals),
                     nameof(HasNoJournalEntries), nameof(HasMoreJournalEntries), nameof(JournalDeleteLabel), nameof(JournalContext) }) RaisePropertyChanged(property);
    }

    private void OpenJournalEditor(Journal? journal)
    {
        var draft = new JournalEditorViewModel(journal, SaveJournalAsync, ReviewJournalAsync);
        UtilityDraft = draft;
        UtilityTitle = journal is null ? "A new journal" : "Edit journal";
        UtilitySaveCommand = draft.SaveCommand;
        _utilityKind = "journal";
        StatusMessage = "Changes stay in this editor until you save.";
        NotifyUtilityEditor();
    }

    private void OpenJournalEntryEditor(JournalEntryRowViewModel? row)
    {
        var draft = new JournalEntryEditorViewModel(row?.Entry, _journals, SelectedJournalId,
            row is not null && !row.CanChange, SaveJournalEntryAsync, ReviewJournalEntryAsync);
        UtilityDraft = draft;
        UtilityTitle = row is null ? "A moment to reflect" : draft.IsReadOnly ? "Journal entry" : "Your journal entry";
        UtilitySaveCommand = draft.SaveCommand;
        _utilityKind = "journal-entry";
        StatusMessage = draft.IsReadOnly ? "Restore the journal and entry to edit. Cancel closes this view." : "Changes stay in this editor until you save.";
        NotifyUtilityEditor();
    }

    private async Task SaveJournalAsync(JournalEditorViewModel draft)
    {
        try
        {
            var definition = new JournalDefinition(draft.Name, draft.Description);
            var saved = draft.Journal is { } journal
                ? await _backend.UpdateJournalAsync(journal.Id, definition, draft.Operation.For(definition, journal.Revision))
                : await _backend.CreateJournalAsync(definition, draft.Operation.For(definition, null));
            FinishUtilityEdit(draft);
            _selectedJournalId = saved.Id;
            await LoadJournalsAsync();
            RaisePropertyChanged(nameof(SelectedJournalId));
            StatusMessage = "Journal saved.";
        }
        catch (Exception exception) { StatusMessage = exception.Message; }
    }

    private async Task ReviewJournalAsync(JournalEditorViewModel draft)
    {
        try
        {
            var latest = (await _backend.GetJournalsAsync(true)).SingleOrDefault(journal => journal.Id == draft.Journal?.Id)
                ?? throw new SnookException(SnookErrorCode.NotFound, "The journal could not be found.");
            if (latest.DeletedAtUtc is not null) throw new SnookException(SnookErrorCode.InvalidTransition, "Restore the deleted journal first. Your draft is still here.");
            draft.Review(latest);
            StatusMessage = "Latest saved journal shown below. Review it before saving your draft.";
        }
        catch (Exception exception) { StatusMessage = exception.Message; }
    }

    private async Task SaveJournalEntryAsync(JournalEntryEditorViewModel draft)
    {
        try
        {
            var definition = draft.Definition();
            if (draft.Entry is { } entry)
                await _backend.UpdateJournalEntryAsync(entry.Id, definition, draft.Operation.For(definition, entry.Revision));
            else await _backend.CreateJournalEntryAsync(definition, draft.Operation.For(definition, null));
            FinishUtilityEdit(draft);
            await LoadJournalsAsync();
            StatusMessage = "Entry saved.";
        }
        catch (Exception exception) { StatusMessage = exception.Message; }
    }

    private async Task ReviewJournalEntryAsync(JournalEntryEditorViewModel draft)
    {
        try
        {
            var latest = await _backend.GetJournalEntryAsync(draft.Entry!.Id);
            var journal = (await _backend.GetJournalsAsync(true)).Single(item => item.Id == latest.JournalId);
            if (latest.DeletedAtUtc is not null || journal.DeletedAtUtc is not null)
                throw new SnookException(SnookErrorCode.InvalidTransition, "Restore the journal and entry first. Your draft is still here.");
            draft.Review(latest, journal.Name);
            StatusMessage = "Latest saved entry shown below. Review it before saving your draft.";
        }
        catch (Exception exception) { StatusMessage = exception.Message; }
    }

    private async Task ChangeJournalLifecycleAsync()
    {
        if (SelectedJournal is not { } journal) return;
        try
        {
            var deleted = journal.DeletedAtUtc is null;
            await _backend.SetJournalDeletedAsync(journal.Id, deleted, _journalLifecycleOperation.For(new { journal.Id, deleted }, journal.Revision));
            await LoadJournalsAsync();
            StatusMessage = deleted ? "Journal deleted. Its entries are preserved; use Show deleted to restore it." : "Journal restored.";
        }
        catch (Exception exception) { await LoadJournalsAsync(); StatusMessage = exception.Message; }
    }

    private async Task ChangeJournalEntryLifecycleAsync(JournalEntryRowViewModel row)
    {
        try
        {
            var deleted = row.Entry.DeletedAtUtc is null;
            await _backend.SetJournalEntryDeletedAsync(row.Entry.Id, deleted, row.Operation.For(new { row.Entry.Id, deleted }, row.Entry.Revision));
            await LoadJournalsAsync();
            StatusMessage = deleted ? "Entry deleted. Use Show deleted to restore it." : "Entry restored.";
        }
        catch (Exception exception) { await LoadJournalsAsync(); StatusMessage = exception.Message; }
    }
}

public sealed record JournalOption(Guid Id, string Name);
public sealed record JournalMoodOption(int Value, string Name);

public sealed class JournalEntryRowViewModel : INotifyPropertyChanged
{
    private Journal? _journal;
    public JournalEntryRowViewModel(JournalEntry entry, Journal? journal, Action<JournalEntryRowViewModel> open, Func<JournalEntryRowViewModel, Task> lifecycle)
    {
        Entry = entry;
        _journal = journal;
        OpenCommand = new AsyncCommand(_ => { open(this); return Task.CompletedTask; }, disableWhileRunning: false);
        DeleteCommand = new AsyncCommand(_ => lifecycle(this));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public JournalEntry Entry { get; private set; }
    internal JournalOperation Operation { get; } = new();
    public string Title => string.IsNullOrWhiteSpace(Entry.Title) ? "Untitled reflection" : Entry.Title;
    public string ContentPreview => Entry.Content.Length > 600 ? Entry.Content[..600] + "…" : Entry.Content;
    public string DateLabel => Entry.OccurredAtUtc.ToLocalTime().ToString("dddd, MMMM d · HH:mm", CultureInfo.CurrentCulture);
    public string JournalName => _journal?.Name ?? "Journal";
    public string MoodLabel => Entry.Mood is { } mood ? $"Mood {mood} / 7" : "No mood recorded";
    public string TagLabel => string.Join("   ", Entry.Tags.Select(tag => "#" + tag));
    public bool HasTags => Entry.Tags.Count > 0;
    public bool IsDeleted => Entry.DeletedAtUtc is not null || _journal?.DeletedAtUtc is not null;
    public string DeletedLabel => _journal?.DeletedAtUtc is not null ? "Journal deleted · restore the journal first" : "Entry deleted";
    public bool CanChange => !IsDeleted;
    public bool CanRestoreOrDelete => _journal is { DeletedAtUtc: null };
    public string OpenLabel => CanChange ? "Read & edit" : "Read entry";
    public string OpenName => "Open journal entry " + Title;
    public string DeleteLabel => Entry.DeletedAtUtc is null ? "Delete entry" : "Restore entry";
    public string DeleteName => DeleteLabel + " " + Title;
    public ICommand OpenCommand { get; }
    public ICommand DeleteCommand { get; }
    public void Refresh(JournalEntry entry, Journal? journal)
    {
        Entry = entry;
        _journal = journal;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}

public sealed class JournalEditorViewModel : INotifyPropertyChanged
{
    public JournalEditorViewModel(Journal? journal, Func<JournalEditorViewModel, Task> save, Func<JournalEditorViewModel, Task> review)
    {
        Journal = journal;
        Name = journal?.Name ?? string.Empty;
        Description = journal?.Description ?? string.Empty;
        SaveCommand = new AsyncCommand(_ => save(this));
        ReviewCommand = new AsyncCommand(_ => review(this));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public Journal? Journal { get; private set; }
    internal JournalOperation Operation { get; } = new();
    public bool IsExisting => Journal is not null;
    public string Name { get; set; }
    public string Description { get; set; }
    public string SavedDetails { get; private set; } = string.Empty;
    public ICommand SaveCommand { get; }
    public ICommand ReviewCommand { get; }
    public void Review(Journal journal)
    {
        Journal = journal;
        SavedDetails = $"Saved name: {journal.Name}\n{journal.Description}\nYour draft above is unchanged.";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SavedDetails)));
    }
}

public sealed class JournalEntryEditorViewModel : INotifyPropertyChanged
{
    private readonly string _originalDateText;
    private readonly DateTimeOffset? _originalInstant;
    public JournalEntryEditorViewModel(JournalEntry? entry, IReadOnlyList<Journal> journals, Guid selectedJournal, bool readOnly,
        Func<JournalEntryEditorViewModel, Task> save, Func<JournalEntryEditorViewModel, Task> review)
    {
        Entry = entry;
        IsReadOnly = readOnly;
        Journals = journals.Where(journal => journal.DeletedAtUtc is null || journal.Id == entry?.JournalId)
            .Select(journal => new JournalOption(journal.Id, journal.Name)).ToArray();
        JournalId = entry?.JournalId ?? (Journals.Any(journal => journal.Id == selectedJournal) ? selectedJournal : Journals.Count > 0 ? Journals[0].Id : Guid.Empty);
        Title = entry?.Title ?? string.Empty;
        Content = entry?.Content ?? string.Empty;
        DateText = MainWindowViewModel.FormatLocalDateTime(entry?.OccurredAtUtc ?? DateTimeOffset.Now);
        _originalDateText = DateText;
        _originalInstant = entry?.OccurredAtUtc;
        TagsText = string.Join(", ", entry?.Tags ?? []);
        MoodRating = entry?.Mood ?? 0;
        SaveCommand = new AsyncCommand(_ => save(this), _ => CanEdit);
        ReviewCommand = new AsyncCommand(_ => review(this));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public JournalEntry? Entry { get; private set; }
    internal JournalOperation Operation { get; } = new();
    public bool IsExisting => Entry is not null && CanEdit;
    public bool IsReadOnly { get; }
    public bool CanEdit => !IsReadOnly;
    public IReadOnlyList<JournalOption> Journals { get; }
    public IReadOnlyList<JournalMoodOption> Moods { get; } = [new(0, "Not rated"), new(1, "1 · Very low"), new(2, "2 · Low"), new(3, "3 · A little low"),
        new(4, "4 · Neutral"), new(5, "5 · Good"), new(6, "6 · Very good"), new(7, "7 · Great")];
    public Guid JournalId { get; set; }
    public string Title { get; set; }
    public string Content { get; set; }
    public string DateText { get; set; }
    public string TagsText { get; set; }
    public int MoodRating { get; set; }
    public string SavedDetails { get; private set; } = string.Empty;
    public ICommand SaveCommand { get; }
    public ICommand ReviewCommand { get; }
    public JournalEntryDefinition Definition()
    {
        // Preserve the exact saved instant (including DST overlap choice) when the displayed minute is unchanged.
        DateTimeOffset occurred;
        if (_originalInstant is { } original && DateText == _originalDateText) occurred = original;
        else if (!MainWindowViewModel.TryParseLocalDateTime(DateText, out occurred))
            throw new SnookException(SnookErrorCode.ValidationFailed, "Enter a valid local date and time as YYYY-MM-DD HH:MM, or an ISO timestamp with its UTC offset.");
        return new JournalEntryDefinition(JournalId, Title, Content, occurred, MoodRating == 0 ? null : MoodRating,
            TagsText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }
    public void Review(JournalEntry entry, string journalName)
    {
        Entry = entry;
        SavedDetails = $"Saved in {journalName} · {MainWindowViewModel.FormatLocalDateTime(entry.OccurredAtUtc)}\n{entry.Title}\n{entry.Content}\nMood: {entry.Mood?.ToString(CultureInfo.InvariantCulture) ?? "not rated"}\nTags: {string.Join(", ", entry.Tags)}\nYour draft above is unchanged.";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SavedDetails)));
    }
}

internal sealed class JournalOperation
{
    private string? _fingerprint;
    private OperationRequest? _request;
    private readonly Guid _deviceId = Guid.NewGuid();
    public OperationRequest For(object value, long? revision)
    {
        var fingerprint = JsonSerializer.Serialize(new { value, revision });
        if (_request is null || fingerprint != _fingerprint)
        {
            _fingerprint = fingerprint;
            _request = new OperationRequest(Guid.NewGuid(), _deviceId, revision);
        }
        return _request;
    }
}

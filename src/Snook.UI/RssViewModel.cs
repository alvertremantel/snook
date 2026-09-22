using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.UI;

public sealed partial class MainWindowViewModel
{
    internal void ReportRssError(string message) => StatusMessage = message;
    private string _rssUrl = "";
    private string _rssSearch = "";
    private bool _rssUnreadOnly;
    private bool _rssStarredOnly;
    private bool _rssShowDeleted;
    private RssFeed? _selectedRssFeed;
    private int _rssOffset;
    private int _rssLoadVersion;
    private bool _rssHasMore;
    private bool _loadingRssFeeds;
    private readonly Dictionary<string, OperationRequest> _rssRequests = [];
    public bool IsRssVisible => CurrentSection == "RSS";
    public ObservableCollection<RssFeed> RssFeeds { get; } = [];
    public ObservableCollection<RssArticleRow> RssArticles { get; } = [];
    public bool HasNoRssArticles => RssArticles.Count == 0;
    public string RssUrl { get => _rssUrl; set => SetField(ref _rssUrl, value); }
    public string RssSearch { get => _rssSearch; set => SetField(ref _rssSearch, value); }
    public bool RssUnreadOnly { get => _rssUnreadOnly; set => SetField(ref _rssUnreadOnly, value); }
    public bool RssStarredOnly { get => _rssStarredOnly; set => SetField(ref _rssStarredOnly, value); }
    public bool RssShowDeleted { get => _rssShowDeleted; set { if (SetField(ref _rssShowDeleted, value)) _ = LoadRssAsync(); } }
    public RssFeed? SelectedRssFeed
    {
        get => _selectedRssFeed;
        set
        {
            if (!SetField(ref _selectedRssFeed, value)) return;
            RaisePropertyChanged(nameof(RssFeedStatus));
            if (!_loadingRssFeeds) { _rssOffset = 0; _ = LoadRssAsync(); }
        }
    }
    public string RssFeedStatus => SelectedRssFeed is not { } feed ? "All subscriptions · cached articles"
        : feed.DeletedAtUtc is not null ? "Deleted subscription · restore to read its cached articles"
        : feed.RefreshedAtUtc is { } at ? $"Last refreshed {at.ToLocalTime():g}" : "Subscribed · refresh to download articles";
    public string RssPageLabel => $"Articles {_rssOffset + 1}–{_rssOffset + RssArticles.Count}";
    public bool RssHasPrevious => _rssOffset > 0;
    public bool RssHasMore => _rssHasMore;
    public ICommand SubscribeRssCommand { get; private set; } = null!;
    public ICommand ClearRssUrlCommand { get; private set; } = null!;
    public ICommand RefreshRssCommand { get; private set; } = null!;
    public ICommand DeleteRssCommand { get; private set; } = null!;
    public ICommand ApplyRssFilterCommand { get; private set; } = null!;
    public ICommand AllRssFeedsCommand { get; private set; } = null!;
    public ICommand PreviousRssPageCommand { get; private set; } = null!;
    public ICommand NextRssPageCommand { get; private set; } = null!;

    private void InitializeRssCommands()
    {
        SubscribeRssCommand = new AsyncCommand(async _ => await RssActionAsync(async () =>
        {
            var url = RssUrl.Trim();
            var feed = await RssMutationAsync("subscribe:" + url, null, request => _backend.SubscribeRssAsync(url, request));
            if (RssUrl.Trim() == url) RssUrl = "";
            _selectedRssFeed = feed;
            _rssOffset = 0;
            StatusMessage = "Subscribed. Select Refresh feed to download articles.";
        }));
        ClearRssUrlCommand = new AsyncCommand(_ => { RssUrl = ""; return Task.CompletedTask; });
        RefreshRssCommand = new AsyncCommand(async _ => await RssActionAsync(async () =>
        {
            var feed = SelectedRssFeed ?? throw new SnookException(SnookErrorCode.ValidationFailed, "Select a subscription to refresh.");
            StatusMessage = "Downloading feed…";
            await RssMutationAsync($"refresh:{feed.Id}:{feed.Revision}", feed.Revision, request => _backend.RefreshRssAsync(feed.Id, request));
            StatusMessage = "Feed refreshed. Cached articles are available offline.";
        }));
        DeleteRssCommand = new AsyncCommand(async _ => await RssActionAsync(async () =>
        {
            var feed = SelectedRssFeed ?? throw new SnookException(SnookErrorCode.ValidationFailed, "Select a subscription first.");
            await RssMutationAsync($"delete:{feed.Id}:{feed.Revision}", feed.Revision,
                request => _backend.SetRssFeedDeletedAsync(feed.Id, feed.DeletedAtUtc is null, request));
            StatusMessage = feed.DeletedAtUtc is null ? "Subscription deleted. Show deleted to restore it and its articles." : "Subscription restored.";
        }));
        ApplyRssFilterCommand = new AsyncCommand(async _ => { _rssOffset = 0; await LoadRssAsync(); });
        AllRssFeedsCommand = new AsyncCommand(async _ => { _selectedRssFeed = null; _rssOffset = 0; await LoadRssAsync(); });
        PreviousRssPageCommand = new AsyncCommand(async _ => { _rssOffset = Math.Max(0, _rssOffset - 20); await LoadRssAsync(); });
        NextRssPageCommand = new AsyncCommand(async _ => { if (_rssHasMore) _rssOffset += 20; await LoadRssAsync(); });
    }
    private OperationRequest RssRequest(string key, long? revision = null)
    {
        if (!_rssRequests.TryGetValue(key, out var request))
            _rssRequests[key] = request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), revision);
        return request;
    }
    private async Task<T> RssMutationAsync<T>(string key, long? revision, Func<OperationRequest, Task<T>> mutation)
    {
        var result = await mutation(RssRequest(key, revision));
        _rssRequests.Remove(key);
        return result;
    }
    private async Task RssActionAsync(Func<Task> action)
    {
        try { await action(); await LoadRssAsync(); }
        catch (Exception exception)
        {
            await LoadRssAsync();
            StatusMessage = exception is SnookException ? exception.Message : "RSS action failed. Check your connection and retry.";
        }
    }
    private async Task LoadRssAsync()
    {
        if (!IsPresentationActive) return;
        var version = ++_rssLoadVersion;
        _rssHasMore = false;
        RaisePropertyChanged(nameof(RssHasMore));
        var token = _presentationReads.Token;
        var selectedId = _selectedRssFeed?.Id;
        try
        {
            var feeds = await _backend.GetRssFeedsAsync(RssShowDeleted, token);
            var selected = feeds.FirstOrDefault(feed => feed.Id == selectedId);
            var page = await _backend.GetRssArticlesAsync(new RssArticleQuery(selected?.Id, RssSearch,
                RssUnreadOnly, RssStarredOnly, _rssOffset, 20), token);
            if (version != _rssLoadVersion || token.IsCancellationRequested) return;
            _loadingRssFeeds = true;
            try
            {
                RssFeeds.Clear();
                foreach (var feed in feeds) RssFeeds.Add(feed);
                _selectedRssFeed = selected;
                RaisePropertyChanged(nameof(SelectedRssFeed));
            }
            finally { _loadingRssFeeds = false; }
            var visibleIds = page.Items.Select(article => article.Id).ToHashSet();
            foreach (var removed in RssArticles.Where(row => !visibleIds.Contains(row.Article.Id)).ToArray()) RssArticles.Remove(removed);
            for (var index = 0; index < page.Items.Count; index++)
            {
                var article = page.Items[index];
                var row = RssArticles.FirstOrDefault(row => row.Article.Id == article.Id);
                if (row is null)
                {
                    row = new RssArticleRow(article, feeds.FirstOrDefault(feed => feed.Id == article.FeedId)?.Title ?? "Feed", ChangeRssArticleAsync);
                    RssArticles.Insert(index, row);
                }
                else
                {
                    row.Refresh(article);
                    var oldIndex = RssArticles.IndexOf(row);
                    if (oldIndex != index) RssArticles.Move(oldIndex, index);
                }
            }
            _rssHasMore = page.HasMore;
            RaisePropertyChanged(nameof(HasNoRssArticles));
            RaisePropertyChanged(nameof(RssFeedStatus));
            RaisePropertyChanged(nameof(RssPageLabel));
            RaisePropertyChanged(nameof(RssHasMore));
            RaisePropertyChanged(nameof(RssHasPrevious));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { if (version == _rssLoadVersion) StatusMessage = exception.Message; }
    }
    private Task ChangeRssArticleAsync(RssArticle article, bool read, bool star)
        => RssActionAsync(async () =>
        {
            await RssMutationAsync($"article:{article.Id}:{article.Revision}:{read}:{star}", article.Revision,
                request => _backend.SetRssArticleStateAsync(article.Id, read, star, request));
            StatusMessage = "Article updated.";
        });
}

public sealed class RssArticleRow : INotifyPropertyChanged
{
    public RssArticleRow(RssArticle article, string feedTitle, Func<RssArticle, bool, bool, Task> change)
    {
        Article = article;
        FeedTitle = feedTitle;
        ToggleReadCommand = new AsyncCommand(_ => change(Article, !Article.IsRead, Article.IsStarred));
        ToggleStarCommand = new AsyncCommand(_ => change(Article, Article.IsRead, !Article.IsStarred));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public RssArticle Article { get; private set; }
    public void Refresh(RssArticle article)
    {
        if (article == Article) return;
        Article = article;
        foreach (var name in new[] { nameof(Article), nameof(StateLabel), nameof(ReadLabel), nameof(StarLabel) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public string FeedTitle { get; }
    public string StateLabel => $"{(Article.IsRead ? "Read" : "Unread")} · {(Article.IsStarred ? "Starred" : "Not starred")} · {FeedTitle}";
    public string DateLabel => Article.PublishedAtUtc?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "Publication date unavailable";
    public string ReadLabel => Article.IsRead ? "Mark unread" : "Mark read";
    public string StarLabel => Article.IsStarred ? "Unstar" : "Star";
    public bool HasUrl => Article.Url is not null;
    public ICommand ToggleReadCommand { get; }
    public ICommand ToggleStarCommand { get; }
}

using System.Globalization;
using System.Text.Json;
using Snook.Contracts;
using Snook.Domain;
using Xunit;

namespace Snook.Application.Tests;

public sealed class JournalCliTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task EmbeddedCliReadsAndWritesJournals()
    {
        var directory = Directory.CreateTempSubdirectory("snook-journal-cli-");
        try { await AssertJournalCliAsync(["--data-dir", directory.FullName, "--host", "embedded"]); }
        finally { directory.Delete(true); }
    }

    internal static async Task AssertJournalCliAsync(string[] prefix)
    {
        async Task<string> RunAsync(params string[] args)
        {
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            using var error = new StringWriter(CultureInfo.InvariantCulture);
            var code = await Snook.Cli.Program.RunAsync([.. prefix, .. args], output, error);
            Assert.True(code == 0, error.ToString());
            return output.ToString();
        }
        async Task<T> CallAsync<T>(string method, object arguments) => JsonSerializer.Deserialize<T>(
            await RunAsync("call", method, JsonSerializer.Serialize(arguments, JsonOptions)), JsonOptions)!;
        static OperationRequest Request(long? revision = null) => new(Guid.NewGuid(), Guid.NewGuid(), revision);

        var create = new { definition = new JournalDefinition("CLI journal", "Written from the command line"), request = Request() };
        var journal = await CallAsync<Journal>("create-journal", create);
        Assert.Equal(journal, await CallAsync<Journal>("create-journal", create));
        Assert.Contains(journal.Id.ToString(), await RunAsync("journals"), StringComparison.Ordinal);
        journal = await CallAsync<Journal>("update-journal", new { journalId = journal.Id, definition = new JournalDefinition("Renamed CLI journal"), request = Request(journal.Revision) });
        var definition = new JournalEntryDefinition(journal.Id, "CLI words", "A multiline entry.\nSecond line.", DateTimeOffset.UtcNow, 7, ["cli-only"]);
        var entryCreate = new { definition, request = Request() };
        var entry = await CallAsync<JournalEntry>("create-journal-entry", entryCreate);
        Assert.Equal(entry.Id, (await CallAsync<JournalEntry>("create-journal-entry", entryCreate)).Id);
        var query = JsonSerializer.Serialize(new JournalEntryQuery(journal.Id, Tag: "CLI-ONLY"), JsonOptions);
        var page = JsonSerializer.Deserialize<JournalEntryPage>(await RunAsync("journal-entries", query), JsonOptions)!;
        Assert.Equal(entry.Id, Assert.Single(page.Items).Id);
        Assert.Equal(7, page.Items[0].Mood);
        Assert.Equal("cli-only", Assert.Single(await CallAsync<string[]>("get-journal-tags", new { journalId = journal.Id })));
        entry = await CallAsync<JournalEntry>("update-journal-entry", new { entryId = entry.Id, definition = definition with { Content = "Revised", Mood = 1, Tags = ["new-tag"] }, request = Request(entry.Revision) });
        var fetched = await CallAsync<JournalEntry>("get-journal-entry", new { entryId = entry.Id });
        Assert.Equal("Revised", fetched.Content);
        Assert.Equal(1, fetched.Mood);
        Assert.Equal("new-tag", Assert.Single(fetched.Tags));
        entry = await CallAsync<JournalEntry>("set-journal-entry-deleted", new { entryId = entry.Id, deleted = true, request = Request(entry.Revision) });
        Assert.Empty(JsonSerializer.Deserialize<JournalEntryPage>(await RunAsync("journal-entries", JsonSerializer.Serialize(new JournalEntryQuery(journal.Id), JsonOptions)), JsonOptions)!.Items);
        entry = await CallAsync<JournalEntry>("set-journal-entry-deleted", new { entryId = entry.Id, deleted = false, request = Request(entry.Revision) });
        Assert.Null(entry.DeletedAtUtc);
        journal = await CallAsync<Journal>("set-journal-deleted", new { journalId = journal.Id, deleted = true, request = Request(journal.Revision) });
        Assert.DoesNotContain(journal.Id.ToString(), await RunAsync("journals"), StringComparison.Ordinal);
        Assert.Contains(journal.Id.ToString(), await RunAsync("journals", "--include-deleted"), StringComparison.Ordinal);
        journal = await CallAsync<Journal>("set-journal-deleted", new { journalId = journal.Id, deleted = false, request = Request(journal.Revision) });
        Assert.Null(journal.DeletedAtUtc);
    }
}

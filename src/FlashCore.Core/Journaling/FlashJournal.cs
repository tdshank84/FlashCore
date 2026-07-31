using System.Text.Json;
using FlashCore.Abstractions.Models;
using FlashCore.Core.Planning;

namespace FlashCore.Core.Journaling;

public sealed record FlashJournalEntry(
    string PlanId,
    int StepSequence,
    FlashOperation Operation,
    string Description,
    DateTimeOffset Timestamp,
    bool Completed,
    string? Error = null);

public interface IFlashJournal
{
    Task AppendAsync(FlashJournalEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlashJournalEntry>> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed class JsonFlashJournal(string path) : IFlashJournal
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task AppendAsync(FlashJournalEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = (await ReadInternalAsync(cancellationToken).ConfigureAwait(false)).ToList();
            entries.Add(entry);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, entries, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<FlashJournalEntry>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadInternalAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<FlashJournalEntry>> ReadInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return Array.Empty<FlashJournalEntry>();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<FlashJournalEntry>>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? [];
    }
}

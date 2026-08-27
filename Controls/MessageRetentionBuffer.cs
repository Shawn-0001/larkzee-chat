namespace LarkzeeChat.Controls;

/// <summary>
/// Bounded, process-local message retention for the visible conversation.
/// The buffer owns the message controls it receives and reports evictions so
/// the parent FlowLayoutPanel can remove them without retaining history.
/// </summary>
public sealed class MessageRetentionBuffer : IDisposable
{
    public const int MaximumMessageCount = 500;
    public const int MaximumTextCharacters = 100_000;
    public static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);

    private readonly LinkedList<Entry> _entries = new();
    private long _characterCount;
    private int _disposed;

    public int Count => _entries.Count;

    public long CharacterCount => _characterCount;

    public IReadOnlyList<Entry> Entries => _entries.ToArray();

    public IReadOnlyList<Entry> Add(
        Control control,
        string text,
        DateTimeOffset receivedAt)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(text);

        _entries.AddLast(new Entry(control, text, receivedAt));
        _characterCount += text.Length;
        return Prune(DateTimeOffset.Now);
    }

    public IReadOnlyList<Entry> Prune(DateTimeOffset now)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        List<Entry>? removed = null;

        while (_entries.First is not null && ShouldPruneOldest(now))
        {
            Entry entry = _entries.First.Value;
            _entries.RemoveFirst();
            _characterCount -= entry.Text.Length;
            (removed ??= new List<Entry>()).Add(entry);
        }

        return removed is null ? Array.Empty<Entry>() : removed;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        while (_entries.First is not null)
        {
            Entry entry = _entries.First.Value;
            _entries.RemoveFirst();
            entry.Control.Dispose();
        }

        _characterCount = 0;
    }

    private bool ShouldPruneOldest(DateTimeOffset now)
    {
        Entry oldest = _entries.First!.Value;
        return _entries.Count > MaximumMessageCount
            || _characterCount > MaximumTextCharacters
            || now - oldest.ReceivedAt > MaximumAge;
    }

    public readonly record struct Entry(
        Control Control,
        string Text,
        DateTimeOffset ReceivedAt);
}

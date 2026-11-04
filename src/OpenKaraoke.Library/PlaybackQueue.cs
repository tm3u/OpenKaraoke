namespace OpenKaraoke.Library;

public sealed class PlaybackQueue
{
    private readonly LibraryStore _store;
    public List<QueueEntry> Entries { get; } = [];
    public string? CurrentId { get; private set; }
    public Song? CurrentSong { get; private set; }
    public bool ShowPlayed { get; set; } = true;
    public string Mode { get; set; } = "instrumental";
    public int PendingCount => Entries.Count(e => e.Status == "pending");
    public PlaybackQueue(LibraryStore store)
    {
        _store = store;
        var saved = store.LoadSession();
        ShowPlayed = saved.ShowPlayed;
        Mode = saved.Mode == "original" ? "original" : "instrumental";
        Save();
    }
    public SavedSession Snapshot() => new(Entries.ToArray(), ShowPlayed, Mode);
    public void Save() => _store.SaveSession(Snapshot());
    public void Add(Song song)
    {
        var entry = new QueueEntry(Guid.NewGuid().ToString("N"), song);
        var snapshot = new SavedSession([.. Entries, entry], ShowPlayed, Mode);
        _store.Enqueue(song, snapshot);
        Entries.Add(entry);
    }
    public QueueEntry? Next() => Entries.FirstOrDefault(e => e.Status == "pending");
    public void Start(QueueEntry entry)
    {
        Complete();
        int index = Entries.FindIndex(e => e.Id == entry.Id);
        if (index < 0) throw new InvalidOperationException("待播歌曲不存在。");
        Entries[index] = entry with { Status = "playing" };
        CurrentId = entry.Id;
        CurrentSong = entry.Song;
        Mode = entry.Mode;
        Save();
    }
    public void Complete()
    {
        int index = Entries.FindIndex(e => e.Id == CurrentId);
        if (index >= 0) Entries[index] = Entries[index] with { Status = "played" };
        CurrentId = null;
        Save();
    }
    public void Replay()
    {
        if (CurrentSong == null || CurrentId != null) return;
        var entry = new QueueEntry(Guid.NewGuid().ToString("N"), CurrentSong, "playing", Mode);
        int pending = Entries.FindIndex(e => e.Status == "pending");
        Entries.Insert(pending < 0 ? Entries.Count : pending, entry);
        CurrentId = entry.Id;
        Save();
    }
    public void SetMode(string mode)
    {
        Mode = mode == "original" ? "original" : "instrumental";
        int index = Entries.FindIndex(e => e.Id == CurrentId);
        if (index >= 0) Entries[index] = Entries[index] with { Mode = Mode };
        Save();
    }
    public void Move(string id, int direction)
    {
        var pending = Entries.Where(e => e.Status == "pending").ToList();
        int at = pending.FindIndex(e => e.Id == id), next = at + Math.Sign(direction);
        if (at < 0 || next < 0 || next >= pending.Count) return;
        int a = Entries.FindIndex(e => e.Id == id), b = Entries.FindIndex(e => e.Id == pending[next].Id);
        (Entries[a], Entries[b]) = (Entries[b], Entries[a]);
        Save();
    }
    public void Remove(string id)
    {
        int index = Entries.FindIndex(e => e.Id == id && e.Status == "pending" && e.Id != CurrentId);
        if (index < 0) return;
        Entries.RemoveAt(index);
        Save();
    }
}

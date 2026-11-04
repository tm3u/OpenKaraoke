using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OpenKaraoke.Library;

public sealed record Song(string Id, string Title, string Artist, string MediaPath, double Duration,
    int OriginalTrack, int InstrumentalTrack, long Requests = 0)
{
    private string _title = Title?.Trim() ?? "", _artist = Artist?.Trim() ?? "";
    public string Title { get => _title; init => _title = value?.Trim() ?? ""; }
    public string Artist { get => _artist; init => _artist = value?.Trim() ?? ""; }
    public string Initial => System.Globalization.StringInfo.GetNextTextElement(Title);
    public string Clock => TimeSpan.FromSeconds(Duration).ToString(Duration >= 3600 ? @"hh\:mm\:ss" : @"m\:ss");
}
public sealed record QueueEntry(string Id, Song Song, string Status = "pending", string Mode = "instrumental");
public sealed record SavedSession(QueueEntry[] Entries, bool ShowPlayed = true, string Mode = "instrumental");
public sealed record SearchPage(Song[] Songs, int Total);

public sealed class LibraryStore
{
    public string DataDirectory { get; }
    public string MediaDirectory => Path.Combine(DataDirectory, "media");
    public string RecoveryMessage { get; private set; } = "";
    private readonly string _connectionString;

    public LibraryStore(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(MediaDirectory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(DataDirectory, "library.db"),
            Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 10 }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS songs(
              id TEXT PRIMARY KEY, title TEXT NOT NULL, artist TEXT NOT NULL, media_path TEXT NOT NULL,
              duration REAL NOT NULL, original_track INTEGER NOT NULL, instrumental_track INTEGER NOT NULL,
              requests INTEGER NOT NULL DEFAULT 0, search_key TEXT NOT NULL, deleted INTEGER NOT NULL DEFAULT 0,
              CHECK(original_track > 0 AND instrumental_track > 0 AND original_track <> instrumental_track));
            CREATE INDEX IF NOT EXISTS songs_popular ON songs(deleted,requests DESC,title,id);
            CREATE UNIQUE INDEX IF NOT EXISTS songs_active_identity ON songs(title,artist) WHERE deleted=0;
            CREATE TABLE IF NOT EXISTS session(id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
    private static string Normalize(string value) => value.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    private Song Read(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), ResolveMediaPath(r.GetString(3)),
        r.GetDouble(4), r.GetInt32(5), r.GetInt32(6), r.GetInt64(7));

    private string ResolveMediaPath(string path) => Path.GetFullPath(path, DataDirectory);

    private string StoredMediaPath(string path)
    {
        string full = ResolveMediaPath(path);
        return full.StartsWith(MediaDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(DataDirectory, full) : full;
    }

    public SearchPage Search(string query, int offset = 0, int pageSize = 60)
    {
        string normalized = Normalize(query);
        bool popular = normalized.Length == 0;
        if (popular && offset > 0) return new([], 10);
        using var db = Open();
        using var count = db.CreateCommand();
        count.CommandText = "SELECT count(*) FROM songs WHERE deleted=0 AND ($q='' OR instr(search_key,$q)>0)";
        count.Parameters.AddWithValue("$q", normalized);
        int total = Convert.ToInt32(count.ExecuteScalar());
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id,title,artist,media_path,duration,original_track,instrumental_track,requests
            FROM songs WHERE deleted=0 AND ($q='' OR instr(search_key,$q)>0)
            ORDER BY requests DESC,title,id LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$q", normalized);
        command.Parameters.AddWithValue("$limit", popular ? 10 : Math.Clamp(pageSize, 1, 100));
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        using var reader = command.ExecuteReader();
        var songs = new List<Song>();
        while (reader.Read()) songs.Add(Read(reader));
        return new(songs.ToArray(), popular ? Math.Min(total, 10) : total);
    }

    public void Add(Song song)
    {
        if (string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(song.Artist) || !File.Exists(song.MediaPath)
            || song.Duration <= 0 || song.OriginalTrack <= 0 || song.InstrumentalTrack <= 0 || song.OriginalTrack == song.InstrumentalTrack)
            throw new ArgumentException("歌曲必須包含歌名、歌手、有效影片及兩條不同的原唱／伴奏音軌。");
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO songs(id,title,artist,media_path,duration,original_track,instrumental_track,search_key)
            VALUES($id,$title,$artist,$path,$duration,$original,$instrumental,$search)
            """;
        command.Parameters.AddWithValue("$id", song.Id);
        command.Parameters.AddWithValue("$title", song.Title.Trim());
        command.Parameters.AddWithValue("$artist", song.Artist.Trim());
        command.Parameters.AddWithValue("$path", StoredMediaPath(song.MediaPath));
        command.Parameters.AddWithValue("$duration", song.Duration);
        command.Parameters.AddWithValue("$original", song.OriginalTrack);
        command.Parameters.AddWithValue("$instrumental", song.InstrumentalTrack);
        command.Parameters.AddWithValue("$search", Normalize(song.Title + "\n" + song.Artist));
        try { command.ExecuteNonQuery(); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        { throw new InvalidOperationException("歌曲庫已有這首歌曲。", error); }
    }

    public bool ContainsSong(string title,string artist)
    {
        using var db=Open();using var command=db.CreateCommand();
        command.CommandText="SELECT EXISTS(SELECT 1 FROM songs WHERE title=$title AND artist=$artist AND deleted=0)";
        command.Parameters.AddWithValue("$title",title.Trim());command.Parameters.AddWithValue("$artist",artist.Trim());
        return Convert.ToInt32(command.ExecuteScalar())!=0;
    }

    public Song? FindById(string id)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT id,title,artist,media_path,duration,original_track,instrumental_track,requests FROM songs WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void Remove(string id, string confirmation)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE songs SET deleted=1 WHERE id=$id AND title=$title AND deleted=0";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", confirmation);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("歌名不符，未移除歌曲。");
    }

    public void Enqueue(Song song, SavedSession session)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE songs SET requests=requests+1 WHERE id=$id AND deleted=0";
        command.Parameters.AddWithValue("$id", song.Id);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("歌曲已從歌曲庫移除。");
        Save(db, transaction, session);
        transaction.Commit();
    }
    public SavedSession LoadSession()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT json FROM session WHERE id=1";
        string? json = command.ExecuteScalar() as string;
        if (json == null) return new([]);
        var entries = new List<QueueEntry>(); var ids = new HashSet<string>();
        bool damaged = false, showPlayed = true; string mode = "instrumental";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Entries", out var saved) || saved.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("待播紀錄格式不正確。");
            if (root.TryGetProperty("ShowPlayed", out var show))
            {
                if (show.ValueKind is JsonValueKind.True or JsonValueKind.False) showPlayed = show.GetBoolean();
                else damaged = true;
            }
            if (root.TryGetProperty("Mode", out var value))
            {
                if (value.ValueKind == JsonValueKind.String && value.GetString() is "original" or "instrumental") mode = value.GetString()!;
                else damaged = true;
            }
            foreach (var element in saved.EnumerateArray())
            {
                try
                {
                    var entry = element.Deserialize<QueueEntry>();
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Id) || entry.Song is not {} song
                        || string.IsNullOrWhiteSpace(song.Id) || string.IsNullOrWhiteSpace(song.Title) || string.IsNullOrWhiteSpace(song.Artist)
                        || string.IsNullOrWhiteSpace(song.MediaPath) || !double.IsFinite(song.Duration) || song.Duration <= 0 || song.Duration >= TimeSpan.MaxValue.TotalSeconds
                        || song.OriginalTrack <= 0 || song.InstrumentalTrack <= 0 || song.OriginalTrack == song.InstrumentalTrack
                        || entry.Status is not ("pending" or "playing" or "played") || entry.Mode is not ("original" or "instrumental"))
                        throw new InvalidDataException("待播項目格式不正確。");
                    string path = FindById(song.Id)?.MediaPath ?? ResolveMediaPath(song.MediaPath);
                    if (!ids.Add(entry.Id)) throw new InvalidDataException("待播項目編號重複。");
                    entries.Add(entry with { Song = song with { MediaPath = path } });
                }
                catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException) { damaged = true; }
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException) { damaged = true; }
        var session = new SavedSession(entries.ToArray(), showPlayed, mode);
        if (damaged)
        {
            string backup = Path.Combine(DataDirectory, "session-recovery-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(backup, json);
            RecoveryMessage = "待播紀錄有異常，已保留有效項目；原始資料備份於 " + backup;
        }
        SaveSession(session);
        return session;
    }
    public void SaveSession(SavedSession session)
    {
        using var db = Open();
        Save(db, null, session);
    }
    private void Save(SqliteConnection db, SqliteTransaction? transaction, SavedSession session)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO session(id,json) VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET json=$json";
        var saved = session with { Entries = session.Entries.Select(entry => entry with
            { Song = entry.Song with { MediaPath = StoredMediaPath(entry.Song.MediaPath) } }).ToArray() };
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(saved));
        command.ExecuteNonQuery();
    }
}

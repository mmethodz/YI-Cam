using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace YiLocal.Core;

public sealed record StoragePolicy(double QuotaGiB = 20, double MinimumFreeGiB = 2,
    double SegmentMinutes = 10, bool Recycle = false, int KeepDays = 0)
{
    public void Validate()
    {
        if (!double.IsFinite(QuotaGiB) || QuotaGiB is < 0.1 or > 100000 ||
            !double.IsFinite(MinimumFreeGiB) || MinimumFreeGiB is < 0.1 or > 100000 ||
            !double.IsFinite(SegmentMinutes) || SegmentMinutes is < 0.1 or > 120 || KeepDays is < 0 or > 36500)
            throw new ArgumentException("Invalid storage policy.");
    }
}
public sealed record Clip(string Name, double Started, double Duration, int Width, int Height,
    long Bytes, bool Complete, bool Protected, bool Exists);

/// <summary>Use one instance per thread. Shares the Python catalogue format.</summary>
public sealed partial class RecordingLibrary : IDisposable
{
    readonly SqliteConnection connection;
    public string Root { get; }
    [GeneratedRegex(@"\AYI_\d{4}-\d\d-\d\d_\d\d-\d\d-\d\d_\d+x\d+_[a-f0-9]{6}\.mp4\z")]
    private static partial Regex ManagedName();
    public RecordingLibrary(string folder)
    {
        Root = Path.GetFullPath(folder); Directory.CreateDirectory(Root);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, ".yi-library.sqlite"), DefaultTimeout = 10 }.ToString());
        connection.Open();
        Execute("PRAGMA journal_mode=WAL");
        Execute("CREATE TABLE IF NOT EXISTS clips (name TEXT PRIMARY KEY, started REAL NOT NULL, duration REAL DEFAULT 0, width INTEGER, height INTEGER, bytes INTEGER DEFAULT 0, complete INTEGER DEFAULT 0, protected INTEGER DEFAULT 0)");
    }
    void Execute(string sql, params (string, object)[] values)
    {
        using var cmd = connection.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value);
        cmd.ExecuteNonQuery();
    }
    public string ClipPath(string name)
    {
        if (!ManagedName().IsMatch(name)) throw new ArgumentException("Not an app-managed recording name.");
        string path = Path.Combine(Root, name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Recording points outside its library.");
        return path;
    }
    public void Register(string name, int width, int height)
    {
        ClipPath(name);
        Execute("INSERT INTO clips(name,started,width,height) VALUES($n,$t,$w,$h)", ("$n", name),
            ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0), ("$w", width), ("$h", height));
    }
    public void Finish(string name, double duration) => Execute("UPDATE clips SET complete=1,duration=$d,bytes=$b WHERE name=$n",
        ("$n", name), ("$d", duration), ("$b", new FileInfo(ClipPath(name)).Length));
    public List<Clip> Clips()
    {
        using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT * FROM clips ORDER BY started DESC";
        using var reader = cmd.ExecuteReader(); var result = new List<Clip>();
        while (reader.Read())
        {
            string name = reader.GetString(0); long size = 0; bool exists = false;
            try { var file = new FileInfo(ClipPath(name)); exists = file.Exists; size = exists ? file.Length : 0; }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
            result.Add(new(name, reader.GetDouble(1), reader.GetDouble(2), reader.GetInt32(3), reader.GetInt32(4),
                size, reader.GetInt32(6) != 0, reader.GetInt32(7) != 0, exists));
        }
        return result;
    }
    public void Protect(string name, bool protect)
    {
        ClipPath(name); Execute("UPDATE clips SET protected=$p WHERE name=$n", ("$p", protect ? 1 : 0), ("$n", name));
    }
    public void Delete(string name)
    {
        // IMMEDIATE transaction excludes a simultaneous protect operation during recycling.
        using var tx = connection.BeginTransaction(deferred: false);
        using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT complete,protected FROM clips WHERE name=$n"; cmd.Parameters.AddWithValue("$n", name);
        using (var reader = cmd.ExecuteReader())
            if (!reader.Read() || reader.GetInt32(0) == 0 || reader.GetInt32(1) != 0)
                throw new InvalidOperationException("Only finished, unprotected recordings can be deleted.");
        File.Delete(ClipPath(name));
        cmd.CommandText = "DELETE FROM clips WHERE name=$n"; cmd.ExecuteNonQuery(); tx.Commit();
    }
    public int Enforce(StoragePolicy policy, string? active = null, long reserve = 1024 * 1024,
        Func<long>? freeBytes = null)
    {
        policy.Validate();
        freeBytes ??= () => new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace;
        var clips = Clips(); long total = clips.Sum(c => c.Bytes), free = freeBytes(); int deleted = 0;
        double limit = policy.QuotaGiB * (1L << 30), floor = policy.MinimumFreeGiB * (1L << 30);
        double cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - policy.KeepDays * 86400L;
        foreach (var clip in clips.AsEnumerable().Reverse())
        {
            bool over = total + reserve > limit || free - reserve < floor;
            if (!policy.Recycle || !(over || policy.KeepDays > 0 && clip.Started < cutoff) ||
                clip.Name == active || clip.Protected || !clip.Complete) continue;
            Delete(clip.Name); total -= clip.Bytes; free = freeBytes(); deleted++;
        }
        if (total + reserve > limit) throw new IOException("Recording budget reached. Free space, raise the budget, or enable recycling.");
        if (free - reserve < floor) throw new IOException("Recording stopped to preserve free disk space.");
        return deleted;
    }
    public void Dispose() => connection.Dispose();
}

public sealed class SegmentRecorder : IDisposable
{
    readonly RecordingLibrary library;
    readonly StoragePolicy policy;
    readonly FileStream writerLock;
    readonly Dictionary<int, byte[]> parameters = [];
    FragmentedMp4? writer;
    string? current;
    long started, lastCheck;
    string? epoch;
    public long Frames { get; private set; }
    public int Recycled { get; private set; }
    public string? Current => current;
    public SegmentRecorder(string folder, StoragePolicy policy)
    {
        policy.Validate(); this.policy = policy;
        Directory.CreateDirectory(folder);
        writerLock = new FileStream(Path.Combine(folder, ".yi-writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { library = new(folder); library.Enforce(policy); }
        catch { writerLock.Dispose(); throw; }
    }
    public void Write(VideoFrame frame, long time, int orderEpoch = 0)
    {
        string identity = $"{orderEpoch}:{frame.Generation}:{frame.Width}:{frame.Height}";
        if (epoch != identity) { CloseSegment(); parameters.Clear(); epoch = identity; }
        if (Environment.TickCount64 - lastCheck > 1000)
        { Recycled += library.Enforce(policy, current); lastCheck = Environment.TickCount64; }
        var nals = FragmentedMp4.Nals(frame.Data);
        foreach (var nal in nals.Where(n => (n[0] & 31) is 7 or 8))
        {
            int type = nal[0] & 31;
            if (parameters.TryGetValue(type, out var prior) && !prior.SequenceEqual(nal)) CloseSegment();
            parameters[type] = nal;
        }
        bool key = nals.Any(n => (n[0] & 31) == 5);
        if (!nals.Any(n => (n[0] & 31) is 1 or 5)) return;
        if (writer is not null && key && time - started >= policy.SegmentMinutes * 60000) CloseSegment();
        if (writer is null)
        {
            if (!key || !parameters.ContainsKey(7) || !parameters.ContainsKey(8)) return;
            Recycled += library.Enforce(policy);
            string name = $"YI_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{frame.Width}x{frame.Height}_{Guid.NewGuid().ToString("N")[..6]}.mp4";
            writer = new(library.ClipPath(name), frame.Width, frame.Height, parameters[7], parameters[8]);
            current = name; started = time;
            library.Register(name, frame.Width, frame.Height);
        }
        writer.Write(frame.Data, time - started); Frames++;
    }
    public void CloseSegment()
    {
        if (writer is null) return;
        var closing = writer; var name = current!; writer = null; current = null;
        closing.Dispose(); library.Finish(name, closing.DurationMilliseconds / 1000.0);
    }
    public void Dispose()
    {
        try { CloseSegment(); } finally { library.Dispose(); writerLock.Dispose(); }
    }
}

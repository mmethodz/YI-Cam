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
    long Bytes, bool Complete, bool Protected, bool Exists, ClipMetadata? Metadata = null, bool InUse = false);

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
        // Separate table leaves the existing Python catalogue schema and older readers intact.
        Execute("CREATE TABLE IF NOT EXISTS clip_details (name TEXT PRIMARY KEY, camera TEXT NOT NULL, profile TEXT NOT NULL, kind TEXT NOT NULL, target_fps REAL, capture_duration REAL DEFAULT 0, frames INTEGER DEFAULT 0)");
        Execute("CREATE TABLE IF NOT EXISTS clip_audio (name TEXT PRIMARY KEY, description TEXT NOT NULL)");
        Execute("CREATE TABLE IF NOT EXISTS clip_leases (name TEXT NOT NULL, owner TEXT NOT NULL, expires INTEGER NOT NULL, PRIMARY KEY(name,owner))");
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
    public void Register(string name, int width, int height, ClipMetadata? metadata = null)
    {
        ClipPath(name);
        Execute("INSERT INTO clips(name,started,width,height) VALUES($n,$t,$w,$h)", ("$n", name),
            ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0), ("$w", width), ("$h", height));
        if (metadata is not null)
            Execute("INSERT INTO clip_details(name,camera,profile,kind,target_fps) VALUES($n,$c,$p,$k,$f)",
                ("$n", name), ("$c", metadata.Camera), ("$p", metadata.Profile), ("$k", metadata.Kind), ("$f", (object?)metadata.TargetFps ?? DBNull.Value));
        if (metadata?.Audio is { } audio)
            Execute("INSERT INTO clip_audio(name,description) VALUES($n,$d)", ("$n", name), ("$d", audio));
    }
    public void Finish(string name, double duration, double? captureDuration = null, long frames = 0)
    {
        Execute("UPDATE clip_details SET capture_duration=$d,frames=$f WHERE name=$n", ("$n", name), ("$d", captureDuration ?? duration), ("$f", frames));
        Execute("UPDATE clips SET complete=1,duration=$d,bytes=$b WHERE name=$n",
            ("$n", name), ("$d", duration), ("$b", new FileInfo(ClipPath(name)).Length));
    }
    public List<Clip> Clips()
    {
        using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT c.*,d.camera,d.profile,d.kind,d.target_fps,d.capture_duration,d.frames,a.description,EXISTS(SELECT 1 FROM clip_leases l WHERE l.name=c.name AND l.expires>unixepoch()) FROM clips c LEFT JOIN clip_details d ON c.name=d.name LEFT JOIN clip_audio a ON c.name=a.name ORDER BY c.started DESC";
        using var reader = cmd.ExecuteReader(); var result = new List<Clip>();
        while (reader.Read())
        {
            string name = reader.GetString(0); long size = 0; bool exists = false;
            try { var file = new FileInfo(ClipPath(name)); exists = file.Exists; size = exists ? file.Length : 0; }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
            result.Add(new(name, reader.GetDouble(1), reader.GetDouble(2), reader.GetInt32(3), reader.GetInt32(4),
                size, reader.GetInt32(6) != 0, reader.GetInt32(7) != 0, exists,
                reader.IsDBNull(8) ? null : new(reader.GetString(8), reader.GetString(9), reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetDouble(11), reader.GetDouble(12), reader.GetInt64(13), reader.IsDBNull(14) ? null : reader.GetString(14)), reader.GetInt32(15) != 0));
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
        cmd.CommandText = "SELECT complete,protected,EXISTS(SELECT 1 FROM clip_leases l WHERE l.name=clips.name AND l.expires>unixepoch()) FROM clips WHERE name=$n"; cmd.Parameters.AddWithValue("$n", name);
        using (var reader = cmd.ExecuteReader())
            if (!reader.Read() || reader.GetInt32(0) == 0 || reader.GetInt32(1) != 0 || reader.GetInt32(2) != 0)
                throw new InvalidOperationException("Only finished, unprotected recordings that are not in use can be deleted.");
        File.Delete(ClipPath(name));
        cmd.CommandText = "DELETE FROM clip_details WHERE name=$n"; cmd.ExecuteNonQuery();
        cmd.CommandText = "DELETE FROM clip_audio WHERE name=$n"; cmd.ExecuteNonQuery();
        cmd.CommandText = "DELETE FROM clip_leases WHERE name=$n"; cmd.ExecuteNonQuery();
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
                clip.Name == active || clip.Protected || clip.InUse || !clip.Complete) continue;
            try { Delete(clip.Name); }
            catch (InvalidOperationException) { continue; } // Protected/leased after the initial catalogue snapshot.
            total -= clip.Bytes; free = freeBytes(); deleted++;
        }
        if (total + reserve > limit) throw new IOException("Recording budget reached. Free space, raise the budget, or enable recycling.");
        if (free - reserve < floor) throw new IOException("Recording stopped to preserve free disk space.");
        return deleted;
    }
    public void Dispose() => connection.Dispose();

    public ClipLease Hold(string name)
    {
        if (!File.Exists(ClipPath(name))) throw new IOException("Recording file is missing.");
        string owner = Guid.NewGuid().ToString("N");
        using var tx = connection.BeginTransaction(deferred: false);
        using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO clip_leases(name,owner,expires) SELECT name,$o,unixepoch()+120 FROM clips WHERE name=$n AND complete=1";
        cmd.Parameters.AddWithValue("$n", name); cmd.Parameters.AddWithValue("$o", owner);
        if (cmd.ExecuteNonQuery() != 1) throw new IOException("Choose a completed recording.");
        tx.Commit(); return new(Root, name, owner);
    }
    internal void RenewLease(string name, string owner) => Execute("UPDATE clip_leases SET expires=unixepoch()+120 WHERE name=$n AND owner=$o", ("$n", name), ("$o", owner));
    internal void ReleaseLease(string name, string owner) => Execute("DELETE FROM clip_leases WHERE name=$n AND owner=$o", ("$n", name), ("$o", owner));
}

public sealed class SegmentRecorder : IDisposable
{
    readonly RecordingLibrary library;
    readonly StoragePolicy policy;
    readonly RecordingOptions options;
    readonly string? ffmpeg;
    readonly string cameraName;
    readonly FileStream writerLock;
    readonly Dictionary<int, byte[]> parameters = [];
    FragmentedMp4? writer;
    FfmpegRecording? encoder;
    readonly List<Task> finishing = [];
    string? current;
    long started, lastCheck;
    string? epoch;
    AacConfiguration? audioConfiguration;
    ushort? audioSequence;
    long? audioTime;
    long? waitingForAudio;
    public long Frames { get; private set; }
    public int Recycled { get; private set; }
    public string? Current => current;
    public SegmentRecorder(string folder, StoragePolicy policy, RecordingOptions? options = null, string? ffmpeg = null, string cameraName = "")
    {
        policy.Validate(); this.policy = policy; this.options = options ?? new(); this.options.Validate();
        this.ffmpeg = ffmpeg; this.cameraName = cameraName;
        if (this.options.Encoding != RecordingEncoding.Original && !File.Exists(ffmpeg))
            throw new IOException("Choose an FFmpeg executable before using an encoding profile.");
        Directory.CreateDirectory(folder);
        writerLock = new FileStream(Path.Combine(folder, ".yi-writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { library = new(folder); library.Enforce(policy); }
        catch { writerLock.Dispose(); throw; }
    }
    public void Write(VideoFrame frame, long time, int orderEpoch = 0)
    {
        if (options.IncludeAudio && audioConfiguration is null)
        {
            waitingForAudio ??= Environment.TickCount64;
            if (Environment.TickCount64 - waitingForAudio > 15000)
                throw new IOException("No supported microphone audio arrived within 15 seconds. Disable audio to record video only.");
        }
        foreach (var done in finishing.Where(task => task.IsCompleted).ToArray())
        { finishing.Remove(done); done.GetAwaiter().GetResult(); }
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
        if (current is not null && key && time - started >= policy.SegmentMinutes * 60000) CloseSegment();
        if (current is null)
        {
            if (!key || !parameters.ContainsKey(7) || !parameters.ContainsKey(8) || options.IncludeAudio && audioConfiguration is null) return;
            Recycled += library.Enforce(policy);
            string name = $"YI_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{frame.Width}x{frame.Height}_{Guid.NewGuid().ToString("N")[..6]}.mp4";
            if (finishing.Count > 1) throw new IOException("Recording encoders are not finishing in time.");
            if (options.Encoding == RecordingEncoding.Original)
                writer = new(library.ClipPath(name), frame.Width, frame.Height, parameters[7], parameters[8], options.IncludeAudio ? audioConfiguration : null);
            else
                encoder = new(ffmpeg!, library.ClipPath(name), frame.Width, frame.Height, parameters[7], parameters[8], options, options.IncludeAudio ? audioConfiguration : null);
            current = name; started = time;
            library.Register(name, frame.Width, frame.Height, new(cameraName, options.Label,
                options.Mode == CaptureMode.Timelapse ? "Timelapse" : options.FramesPerSecond is not null ? "Low-rate" : "Continuous", options.FramesPerSecond,
                Audio: options.IncludeAudio ? $"AAC-LC {audioConfiguration!.SampleRate} Hz, {audioConfiguration.Channels} channel(s)" : null));
        }
        if (writer is not null) writer.Write(frame.Data, time - started);
        else encoder!.Write(frame.Data, time - started);
        Frames++;
    }
    public void WriteAudio(AudioFrame frame, long time)
    {
        if (!options.IncludeAudio) return;
        if (frame.Codec != 138) throw new NotSupportedException($"Unsupported camera audio codec {frame.Codec}.");
        var (configuration, _) = AacConfiguration.Parse(frame.Data);
        if (audioConfiguration is not null && (audioConfiguration != configuration ||
            audioSequence is { } prior && frame.Sequence != (ushort)(prior + 1) || audioTime is { } previous && time <= previous))
            CloseSegment(); // Preserve a discontinuity rather than silently stretch audio over missing packets.
        audioConfiguration = configuration; audioSequence = frame.Sequence; audioTime = time;
        if (current is null || time < started) return;
        if (writer is not null) writer.WriteAudio(frame.Data, time - started);
        else encoder!.Write(frame.Data, time - started, audio: true);
    }
    public void ConnectionEnded()
    {
        CloseSegment(); audioConfiguration = null; audioSequence = null; audioTime = null; waitingForAudio = null;
    }
    public void CloseSegment()
    {
        if (current is null) return;
        var name = current; current = null;
        if (writer is { } closing)
        {
            writer = null; closing.Dispose();
            library.Finish(name, closing.DurationMilliseconds / 1000.0, frames: closing.Frames);
        }
        else if (encoder is { } encoded)
        {
            encoder = null; string root = library.Root;
            // Finishing a segment must not hold up live preview or the next segment.
            finishing.Add(Task.Run(async () =>
            {
                var result = await encoded.FinishAsync();
                using var finishedLibrary = new RecordingLibrary(root);
                finishedLibrary.Finish(name, result.Duration, result.CaptureDuration, result.Frames);
            }));
        }
    }
    public void Dispose()
    {
        try { CloseSegment(); Task.WhenAll(finishing).GetAwaiter().GetResult(); }
        finally { library.Dispose(); writerLock.Dispose(); }
    }
}

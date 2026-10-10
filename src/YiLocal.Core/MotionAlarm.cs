namespace YiLocal.Core;

public enum AlarmOutput { Camera, Computer, Both }

public sealed record AlarmOptions(bool Enabled = false, double ThresholdPercent = 10,
    double? DurationSeconds = 30, double EntryGraceSeconds = 15, double ExitDelaySeconds = 60,
    string? SoundFile = null, string? SoundName = null, AlarmOutput Output = AlarmOutput.Camera, string? ComputerDevice = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Output)) throw new ArgumentOutOfRangeException(nameof(Output));
        if (!double.IsFinite(ThresholdPercent) || ThresholdPercent is < .1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(ThresholdPercent));
        if (DurationSeconds is { } duration && (!double.IsFinite(duration) || duration is < 1 or > 3600))
            throw new ArgumentOutOfRangeException(nameof(DurationSeconds));
        if (!double.IsFinite(EntryGraceSeconds) || EntryGraceSeconds is < 0 or > 300)
            throw new ArgumentOutOfRangeException(nameof(EntryGraceSeconds));
        if (!double.IsFinite(ExitDelaySeconds) || ExitDelaySeconds is < 0 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(ExitDelaySeconds));
    }
}

public enum AlarmPhase { Home, ExitDelay, Watching, EntryDelay, Sounding, WaitingForQuiet }

/// <summary>Independent second threshold. All times are monotonic PC milliseconds, not saved-video fps.
/// The owner must disarm on lost/stale analysis and stop the speaker whenever Sounding ends.</summary>
public sealed class MotionAlarmGate
{
    readonly AlarmOptions options;
    long deadline, lastSample = long.MinValue;
    long? quietSince, playbackStarted;
    int consecutive;
    public AlarmPhase Phase { get; private set; } = AlarmPhase.Home;
    public bool Armed => Phase != AlarmPhase.Home;
    public MotionAlarmGate(AlarmOptions options) { options.Validate(); this.options = options; }
    public void Arm(long now)
    {
        if (!options.Enabled) throw new InvalidOperationException(L.Get("EnableTheOptionalAlarmFirst"));
        Home();
        Phase = options.ExitDelaySeconds > 0 ? AlarmPhase.ExitDelay : AlarmPhase.Watching;
        deadline = now + (long)(options.ExitDelaySeconds * 1000);
    }
    public void Home()
    {
        Phase = AlarmPhase.Home; consecutive = 0; quietSince = playbackStarted = null; lastSample = long.MinValue;
    }
    public void Observe(double changedPercent, long now)
    {
        Tick(now);
        if (!double.IsFinite(changedPercent) || changedPercent is < 0 or > 100) return;
        if (lastSample != long.MinValue && (now <= lastSample || now - lastSample > 1500))
        { consecutive = 0; quietSince = null; }
        lastSample = now;
        if (Phase == AlarmPhase.WaitingForQuiet)
        {
            if (changedPercent >= options.ThresholdPercent) quietSince = null;
            else
            {
                quietSince ??= now;
                if (now - quietSince >= 2000) { Phase = AlarmPhase.Watching; consecutive = 0; }
            }
            return;
        }
        if (Phase != AlarmPhase.Watching) return;
        consecutive = changedPercent >= options.ThresholdPercent ? consecutive + 1 : 0;
        if (consecutive < 2) return;
        consecutive = 0;
        Phase = options.EntryGraceSeconds > 0 ? AlarmPhase.EntryDelay : AlarmPhase.Sounding;
        deadline = now + (long)(options.EntryGraceSeconds * 1000);
    }
    public void Tick(long now)
    {
        if (Phase == AlarmPhase.ExitDelay && now >= deadline) { Phase = AlarmPhase.Watching; consecutive = 0; }
        else if (Phase == AlarmPhase.EntryDelay && now >= deadline) Phase = AlarmPhase.Sounding;
        else if (Phase == AlarmPhase.Sounding && playbackStarted is { } start && options.DurationSeconds is { } duration && now - start >= duration * 1000)
            Stop();
    }
    public void PlaybackStarted(long now) { if (Phase == AlarmPhase.Sounding) playbackStarted ??= now; }
    // Silence this incident. Fresh, continuously quiet samples are required before another trigger.
    public void Stop()
    {
        if (Phase is AlarmPhase.EntryDelay or AlarmPhase.Sounding or AlarmPhase.WaitingForQuiet)
        { Phase = AlarmPhase.WaitingForQuiet; consecutive = 0; quietSince = playbackStarted = null; }
    }
    public double? RemainingSeconds(long now) => Phase switch
    {
        AlarmPhase.ExitDelay or AlarmPhase.EntryDelay => Math.Max(0, (deadline - now) / 1000.0),
        AlarmPhase.Sounding when options.DurationSeconds is { } duration => Math.Max(0, duration - (playbackStarted is { } start ? (now - start) / 1000.0 : 0)),
        _ => null
    };
}

namespace YiLocal.Core;

public sealed record ClipFilter(double? From = null, double? Until = null, string? Camera = null,
    string? Kind = null, bool? Protected = null)
{
    public bool Matches(Clip clip)
    {
        double captured = clip.Metadata is { CaptureDuration: > 0 } metadata ? metadata.CaptureDuration : clip.Duration;
        return (From is null || clip.Started + Math.Max(0, captured) >= From) &&
            (Until is null || clip.Started <= Until) &&
            (Camera is null || (clip.Metadata?.Camera ?? "") == Camera) &&
            (Kind is null || (clip.Metadata?.Kind ?? "Continuous") == Kind) &&
            (Protected is null || clip.Protected == Protected);
    }
}

/// <summary>An expiring catalogue lease prevents recycling without changing the owner's protection flag.</summary>
public sealed class ClipLease : IDisposable
{
    readonly string root, name, owner;
    bool disposed;
    internal ClipLease(string root, string name, string owner) { this.root = root; this.name = name; this.owner = owner; }
    public void Renew()
    {
        if (disposed) throw new ObjectDisposedException(nameof(ClipLease));
        using var library = new RecordingLibrary(root); library.RenewLease(name, owner);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        using var library = new RecordingLibrary(root); library.ReleaseLease(name, owner);
    }
}

using System.Diagnostics;
using YiLocal.Windows;

if (args is ["--settings"])
{
    SettingsChecks.Run();
    return;
}
if (args is ["--talk-encoder", var encoder, var output])
{
    await TalkEncoderChecks.RunAsync(encoder, output);
    return;
}

if (args.Length < 3) throw new ArgumentException("Arguments: FFmpeg path, recording path, duration [seek seconds] [sound true/false]");
double duration = double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
double start = args.Length > 3 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0;
bool sound = args.Length <= 4 || bool.Parse(args[4]);
using var playback = new RecordingPlayback(args[0], args[1], start, duration, sound, () => new SilentClockedOutput());
var timeout = Stopwatch.StartNew();
int pictures = 0;
while (timeout.Elapsed.TotalSeconds < 6 && playback.Position < Math.Min(duration - .1, start + 1.25))
{
    using var picture = playback.Take();
    if (picture is not null) pictures++;
    if (playback.Error is { } error) throw new Exception(error);
    await Task.Delay(10);
}
if (pictures < 10 || playback.Position < start + 1)
    throw new Exception($"Playback stalled: {pictures} pictures, position {playback.Position:0.000}, sound={sound}.");
Console.WriteLine($"Playback advanced with {pictures} rendered pictures, sound={sound}, seek={start:0.###}, position={playback.Position:0.###}.");

// A paced, bounded speaker sink exercises the real player/FFmpeg pipes without
// sending fixture tones or private camera audio to the machine's speakers.
sealed class SilentClockedOutput : IPcmPlaybackOutput
{
    readonly Stopwatch clock = new();
    double submitted;
    public double Seconds => Math.Min(Volatile.Read(ref submitted), clock.Elapsed.TotalSeconds);
    public async Task WriteAsync(byte[] pcm, CancellationToken cancellation)
    {
        while (submitted - Seconds > .64) await Task.Delay(5, cancellation);
        Interlocked.Exchange(ref submitted, submitted + pcm.Length / 32000.0);
        clock.Start();
    }
    public async Task DrainAsync(CancellationToken cancellation)
    { while (Seconds < submitted) await Task.Delay(5, cancellation); }
    public void Dispose() { clock.Stop(); }
}

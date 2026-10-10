using System.Diagnostics;
using YiLocal.Core;

namespace YiLocal.Windows;

/// <summary>A short-lived authenticated LAN session isolates talk failures from video and recordings.</summary>
internal sealed class Talkback : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    int disposing;
    int packetsSent;
    double inputPeak, maximumInputPeak;
    public double InputPeak => Volatile.Read(ref inputPeak);
    public double MaximumInputPeak => Volatile.Read(ref maximumInputPeak);
    public bool IsSpeakerTest { get; }
    public int PacketsSent => Volatile.Read(ref packetsSent);
    public string? Error { get; private set; }
    public Task Completion { get; }
    public Talkback(DeviceProfile profile, string executable, string? microphone, bool testSpeaker = false)
    {
        IsSpeakerTest = testSpeaker;
        Completion = Task.Run(async () =>
        {
            try
            {
                using var client = new CameraClient(profile.Ip, profile.Password, profile.Uid);
                await client.ConnectAsync(stop.Token);
                await client.FirmwareAsync().WaitAsync(stop.Token);
                stop.Token.ThrowIfCancellationRequested();
                try
                {
                    await client.StartSpeakerAsync(profile.StreamQuality, stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                    await using var encoder = new MicrophoneEncoder(executable, testSpeaker ? new SpeakerTestInput() : new PcmInput(microphone), peak =>
                    {
                        Volatile.Write(ref inputPeak, peak);
                        Volatile.Write(ref maximumInputPeak, Math.Max(maximumInputPeak, peak));
                    });
                    var clock = new Stopwatch();
                    await foreach (var packet in encoder.Packets(stop.Token))
                    {
                        if (!clock.IsRunning) clock.Start();
                        double target = PacketsSent * .064, delay = target - clock.Elapsed.TotalSeconds;
                        if (delay < -.8) throw new IOException("Talking fell behind real time. Start talking again to reconnect.");
                        if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), stop.Token);
                        stop.Token.ThrowIfCancellationRequested(); client.SendTalkAudio(packet);
                        Interlocked.Increment(ref packetsSent);
                        if (testSpeaker && PacketsSent >= 41) break;
                    }
                }
                finally
                {
                    if (client.Connected) await client.StopSpeakerAsync();
                }
            }
            catch (Exception e) { if (!stop.IsCancellationRequested) Error = e.Message; }
        });
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposing, 1) == 0)
        {
            stop.Cancel();
            try { await Completion; } finally { stop.Dispose(); }
        }
        else await Completion;
    }
}

/// <summary>Two quiet tones through the exact microphone encoder/transport path; never opens an input device.</summary>
internal sealed class SpeakerTestInput : IPcmCaptureInput
{
    readonly Stopwatch clock = Stopwatch.StartNew();
    int samples;
    public async Task<byte[]> ReadAsync(CancellationToken cancellation)
    {
        double delay = (samples + 1024) / 16000.0 - clock.Elapsed.TotalSeconds;
        if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), cancellation);
        var pcm = new byte[2048];
        for (int i = 0; i < 1024; i++, samples++)
        {
            double t = samples / 16000.0;
            int frequency = t is >= .3 and <= .9 ? 600 : t is >= 1.3 and <= 1.9 ? 900 : 0;
            short value = (short)(32767 * .02 * Math.Sin(2 * Math.PI * frequency * t));
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), value);
        }
        return pcm;
    }
    public void Dispose() { }
}

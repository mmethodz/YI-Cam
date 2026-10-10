using System.Diagnostics;
using YiLocal.Core;

namespace YiLocal.Windows;

/// <summary>A short-lived authenticated LAN session isolates talk failures from video and recordings.</summary>
internal sealed class Talkback : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    int disposing;
    int packetsSent;
    public int PacketsSent => Volatile.Read(ref packetsSent);
    public string? Error { get; private set; }
    public Task Completion { get; }
    public Talkback(DeviceProfile profile, string executable, string? microphone)
    {
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
                    await using var encoder = new MicrophoneEncoder(executable, new PcmInput(microphone));
                    var clock = new Stopwatch();
                    await foreach (var packet in encoder.Packets(stop.Token))
                    {
                        if (!clock.IsRunning) clock.Start();
                        double target = PacketsSent * .064, delay = target - clock.Elapsed.TotalSeconds;
                        if (delay < -.8) throw new IOException("Talking fell behind real time. Start talking again to reconnect.");
                        if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), stop.Token);
                        stop.Token.ThrowIfCancellationRequested(); client.SendTalkAudio(packet);
                        Interlocked.Increment(ref packetsSent);
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

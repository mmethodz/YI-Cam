using System.Diagnostics;
using YiLocal.Core;
using YiLocal.Windows;

static class TalkEncoderChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task RunAsync(string executable, string output)
    {
        var source = new SyntheticInput(); var clock = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var packets = new List<byte[]>(); double first = 0;
        await using (var encoder = new MicrophoneEncoder(executable, source))
        {
            await foreach (var packet in encoder.Packets(timeout.Token))
            {
                if (packets.Count == 0) first = clock.Elapsed.TotalSeconds;
                TalkAudio.Validate(packet); packets.Add(packet);
                if (packets.Count == 24) break;
            }
        }
        Require(first < 3 && source.Disposed && packets.Count == 24, "Live encoder stalled or retained the microphone.");
        await File.WriteAllBytesAsync(output, packets.SelectMany(p => p).ToArray());

        var missing = new SyntheticInput();
        try { await using var _ = new MicrophoneEncoder(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), missing); throw new Exception("Invalid FFmpeg started."); }
        catch (System.ComponentModel.Win32Exception) { }
        Require(missing.Disposed, "FFmpeg startup failure retained the microphone.");

        var failing = new FailingInput();
        await using (var encoder = new MicrophoneEncoder(executable, failing))
        {
            try { await foreach (var _ in encoder.Packets(timeout.Token)) { } throw new Exception("Failed microphone capture was accepted."); }
            catch (IOException e) { Require(e.Message.Contains("fixture capture ended"), "Capture failure was not reported."); }
        }
        Require(failing.Disposed, "Failed capture retained the microphone.");

        var waiting = new WaitingInput(); var cancelClock = Stopwatch.StartNew();
        using (var cancel = new CancellationTokenSource(100))
        await using (var encoder = new MicrophoneEncoder(executable, waiting))
        {
            try { await foreach (var _ in encoder.Packets(cancel.Token)) { } throw new Exception("Cancellation did not stop talk encoding."); }
            catch (OperationCanceledException) { }
        }
        Require(waiting.Disposed && cancelClock.Elapsed.TotalSeconds < 3, "Cancelled talk encoder did not release its input promptly.");
        Console.WriteLine($"Talk PCM → AAC passed: {packets.Count} packets, first packet {first:0.000}s; cancellation and failure cleanup passed.");
    }
    sealed class SyntheticInput : IPcmCaptureInput
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        int samples;
        public bool Disposed { get; private set; }
        public async Task<byte[]> ReadAsync(CancellationToken cancellation)
        {
            double due = (samples + 1024) / 16000.0;
            double wait = due - clock.Elapsed.TotalSeconds;
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), cancellation);
            var pcm = new byte[2048];
            for (int i = 0; i < 1024; i++, samples++)
            {
                short value = (short)(1000 * Math.Sin(2 * Math.PI * 600 * samples / 16000.0));
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), value);
            }
            return pcm;
        }
        public void Dispose() { Disposed = true; }
    }
    sealed class FailingInput : IPcmCaptureInput
    {
        public bool Disposed { get; private set; }
        public Task<byte[]> ReadAsync(CancellationToken cancellation) => throw new IOException("fixture capture ended");
        public void Dispose() { Disposed = true; }
    }
    sealed class WaitingInput : IPcmCaptureInput
    {
        public bool Disposed { get; private set; }
        public async Task<byte[]> ReadAsync(CancellationToken cancellation) { await Task.Delay(Timeout.Infinite, cancellation); return []; }
        public void Dispose() { Disposed = true; }
    }
}

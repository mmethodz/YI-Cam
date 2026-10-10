using System.Diagnostics;
using YiLocal.Core;
using YiLocal.Windows;

static class AlarmAudioChecks
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static async Task RunAsync(string ffmpeg, string output)
    {
        Directory.CreateDirectory(output);
        string source = Path.Combine(output, "source.wav"), mp3 = Path.Combine(output, "source.mp3");
        // Test only synthetic audio and the public-domain bundled asset; all playback goes to silent sinks.
        using (var writer = new BinaryWriter(File.Create(source)))
        {
            const int count = 48000 * 2;
            writer.Write("RIFF"u8); writer.Write(36 + count * 4); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)2); writer.Write(48000); writer.Write(192000); writer.Write((short)4); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(count * 4);
            for (int i = 0; i < count; i++) { short sample = (short)(2000 * Math.Sin(2 * Math.PI * 1100 * i / 48000)); writer.Write(sample); writer.Write(sample); }
        }
        var converted = await AlarmAudio.ImportAsync(ffmpeg, source, output, CancellationToken.None);
        Check(converted.Clip.Seconds is > 1.9 and < 2.3, "WAV import duration changed.");
        var pcm = await AlarmAudio.DecodeAsync(ffmpeg, converted.Clip, CancellationToken.None);
        Check(pcm.Length is > 60000 and < 80000 && MicrophoneEncoder.Peak(pcm) > .3, "Import failed to normalize/downmix/resample audio.");
        var info = MediaTools.StartInfo(ffmpeg, "-v", "error", "-nostdin", "-y", "-i", source, mp3);
        using (var process = Process.Start(info)!) { await process.WaitForExitAsync(); Check(process.ExitCode == 0, "MP3 fixture generation failed."); }
        var importedMp3 = await AlarmAudio.ImportAsync(ffmpeg, mp3, output, CancellationToken.None);
        Check(importedMp3.Clip.Seconds is > 1.9 and < 2.3, "MP3 import failed.");
        File.Delete(mp3); Check(AlarmAudio.Load(importedMp3.Path).Packets.Count == importedMp3.Clip.Packets.Count, "Imported audio depended on its original file.");
        var bundled = await AlarmAudio.DefaultAsync(ffmpeg, CancellationToken.None);
        Check(bundled.Seconds is > 7.9 and < 8.3, "Bundled public-domain siren missing or invalid.");
        File.WriteAllBytes(Path.Combine(output, "default.aac"), bundled.Packets.SelectMany(packet => packet).ToArray());
        byte[] invalid = [1, 2, 3, 4];
        try { AlarmAudio.Parse(invalid); throw new Exception("Truncated audio was accepted."); } catch (InvalidDataException) { }
        int sent = 0;
        var shortClip = new AlarmClip(converted.Clip.Packets.Take(8).ToArray());
        var clock = Stopwatch.StartNew();
        await CameraAlarm.SendLoopAsync(shortClip, 1.1, packet =>
        {
            Check(ReferenceEquals(packet, shortClip.Packets[sent % 8]), "Camera alarm did not repeat the sample."); TalkAudio.Validate(packet); sent++;
        }, CancellationToken.None);
        Check(sent == 18 && clock.Elapsed.TotalSeconds >= 1, "Camera alarm pacing/duration failed.");
        using (var stop = new CancellationTokenSource(250))
        {
            sent = 0;
            try { await CameraAlarm.SendLoopAsync(shortClip, null, _ => sent++, stop.Token); } catch (OperationCanceledException) { }
            Check(sent is >= 2 and <= 5, "Latched camera alarm ignored cancellation or burst queued audio.");
        }
        using var sink = new CountingOutput();
        int starts = 0;
        await ComputerAlarm.PlayAsync(pcm[..16000], 1.25, sink, () => starts++, CancellationToken.None);
        Check(sink.Bytes == 40000 && starts > 1 && sink.Drained, "PC alarm looping/duration failed.");
        using var stalled = new StalledOutput();
        var elapsed = Stopwatch.StartNew();
        await using (var active = new ComputerAlarm(pcm, "fake", null, () => stalled))
        { await Task.Delay(50); }
        Check(elapsed.Elapsed.TotalSeconds < 1, "Stop left a pending PC output running.");
        using (var unresponsive = new StalledOutput())
        await using (var active = new ComputerAlarm(pcm, "fake", null, () => unresponsive))
        {
            await active.Completion.WaitAsync(TimeSpan.FromSeconds(4));
            Check(active.Error?.Contains("stopped accepting") == true, "Stalled PC output did not fail with a bounded error.");
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await AlarmAudio.ImportAsync(ffmpeg, source, output, cancelled.Token); throw new Exception("Cancelled import completed."); }
        catch (OperationCanceledException) { }
        File.WriteAllText(Path.Combine(output, "broken.mp3"), "not audio");
        try { await AlarmAudio.ImportAsync(ffmpeg, Path.Combine(output, "broken.mp3"), output, CancellationToken.None); throw new Exception("Invalid source accepted."); }
        catch (IOException) { }
        Console.WriteLine("Alarm audio: WAV/MP3/default import, bounded AAC, loudness, looping, exact PC duration and cancellation passed without audible playback.");
    }
    sealed class CountingOutput : IPcmPlaybackOutput
    {
        public int Bytes; public bool Drained;
        public double Seconds => Bytes / 32000.0;
        public Task WriteAsync(byte[] pcm, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); Bytes += pcm.Length; return Task.CompletedTask; }
        public Task DrainAsync(CancellationToken cancellation) { Drained = true; return Task.CompletedTask; }
        public void Dispose() { }
    }
    sealed class StalledOutput : IPcmPlaybackOutput
    {
        public double Seconds => 0;
        public Task WriteAsync(byte[] pcm, CancellationToken cancellation) => Task.Delay(Timeout.Infinite, cancellation);
        public Task DrainAsync(CancellationToken cancellation) => Task.CompletedTask;
        public void Dispose() { }
    }
}

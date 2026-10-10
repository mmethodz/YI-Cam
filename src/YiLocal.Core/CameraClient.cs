using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace YiLocal.Core;

public sealed record CameraReply(ushort Command, ushort Number, uint AuthenticationResult, byte[] Data, byte Unsupported);
public sealed record VideoFrame(byte[] Data, ushort Sequence, int Width, int Height, uint Seconds,
    uint Milliseconds, bool Keyframe, byte Generation, ushort Codec);
public sealed record CameraSettings(byte Hardware, byte NightVision, byte Tracking, byte Rotation = 0);

public sealed class CameraAuthenticationException(uint result) : UnauthorizedAccessException(
    L.Format("CameraRejectedTheDeviceKey0", result) + (result == 1 ? L.Get("RefreshTheSavedKeyInCameraSetupUsingImportFromRunning") : ""))
{
    public uint Result { get; } = result;
}

public static class Wire
{
    public static ushort U16(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt16BigEndian(b);
    public static uint U32(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32BigEndian(b);
    public static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); return b; }
    public static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    public static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    public static byte[] Packet(byte type, byte[]? payload = null)
    {
        payload ??= [];
        return Join([0xf1, type], U16(checked((ushort)payload.Length)), payload);
    }
    public static byte[] Auth(string key, string nonce)
    {
        var digest = HMACSHA1.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes("user=xiaoyiuser&nonce=" + nonce));
        var auth = new byte[32];
        Encoding.ASCII.GetBytes(nonce + "," + Convert.ToBase64String(digest)[..15]).CopyTo(auth, 0);
        return auth;
    }
}

public sealed class ReliableChannel
{
    public ushort Expected { get; set; }
    readonly Dictionary<ushort, byte[]> pending = [];
    readonly List<byte> buffer = [];
    public List<(byte Kind, byte Ability, byte[] Body)> Feed(ushort sequence, byte[] data)
    {
        var distance = (ushort)(sequence - Expected);
        if (distance >= 32768) return [];
        if (distance > 4096) throw new InvalidDataException(L.Get("CameraPacketSequenceExceedsReceiveWindow"));
        pending.TryAdd(sequence, data);
        if (pending.Sum(p => p.Value.Length) > 8 * 1024 * 1024) throw new InvalidDataException(L.Get("CameraReceiveBufferExceededItsLimit"));
        while (pending.Remove(Expected, out var bytes)) { buffer.AddRange(bytes); Expected++; }
        var messages = new List<(byte, byte, byte[])>();
        while (buffer.Count >= 8)
        {
            var header = buffer.GetRange(0, 8).ToArray();
            int size = checked((int)Wire.U32(header.AsSpan(4)));
            if (header[0] is < 1 or > 3 || header[1] is < 1 or > 3 || size > 8 * 1024 * 1024)
                throw new InvalidDataException(L.Get("InvalidCameraMessageHeader"));
            if (buffer.Count < 8 + size) break;
            messages.Add((header[1], header[2], buffer.GetRange(8, size).ToArray()));
            buffer.RemoveRange(0, 8 + size);
        }
        return messages;
    }
}

public sealed class CameraClient : IDisposable
{
    readonly IPAddress address;
    readonly string password;
    readonly string? expectedUid;
    readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    readonly Aes aes = Aes.Create();
    readonly object sendLock = new();
    readonly Dictionary<(byte Channel, ushort Sequence), (byte[] Packet, long Sent, int Attempts)> unacked = [];
    readonly ConcurrentDictionary<ushort, (ushort Response, TaskCompletionSource<CameraReply> Completion)> requests = new();
    readonly ReliableChannel[] channels = Enumerable.Range(0, 6).Select(_ => new ReliableChannel()).ToArray();
    readonly CancellationTokenSource stop = new();
    readonly Channel<VideoFrame> video = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(300) { SingleWriter = true });
    readonly Channel<AudioFrame> audio = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(128) { SingleWriter = true });
    readonly string noncePrefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()[..7];
    IPEndPoint? peer;
    Task? receiver;
    ushort sequence, number;
    ushort audioSequence;
    uint talkFrames;
    bool speaking;
    byte generation;
    long lastReceived;
    bool disposed;
    public string? Uid { get; private set; }
    public bool Connected { get; private set; }
    public Exception? Error { get; private set; }
    public ChannelReader<VideoFrame> Frames => video.Reader;
    public ChannelReader<AudioFrame> AudioFrames => audio.Reader;
    static long Now => Environment.TickCount64;

    public CameraClient(string ip, string deviceKey, string? uid = null)
    {
        if (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException(L.Get("EnterTheCameraSPrivateIPv4Address"));
        var a = parsed.GetAddressBytes();
        if (!(a[0] == 10 || a[0] == 192 && a[1] == 168 || a[0] == 172 && a[1] is >= 16 and <= 31 || a[0] == 127))
            throw new ArgumentException(L.Get("OnlyAPrivateLANCameraAddressIsAccepted"));
        if (Encoding.UTF8.GetByteCount(deviceKey) != 15) throw new ArgumentException(L.Get("A15ByteDevicePairingKeyIsRequired"));
        address = parsed; password = deviceKey; expectedUid = uid;
        aes.Key = Encoding.UTF8.GetBytes(deviceKey + "0");
        socket.ReceiveBufferSize = 4 * 1024 * 1024;
        socket.ReceiveTimeout = 150;
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
    }

    public async Task ConnectAsync(CancellationToken cancellation = default)
    {
        await Task.Run(() =>
        {
            long deadline = Now + 5000, sent = 0;
            var buffer = new byte[65535];
            while (Now < deadline)
            {
                cancellation.ThrowIfCancellationRequested();
                if (Now - sent > 500) { socket.SendTo(Wire.Packet(0x30), new IPEndPoint(address, 32108)); sent = Now; }
                var packet = Receive(buffer);
                if (packet is null) continue;
                var (bytes, source) = packet.Value;
                if (!source.Address.Equals(address) || bytes.Length < 24 || bytes[0] != 0xf1 || bytes[1] != 0x41) continue;
                var uid = bytes[4..24];
                Uid = Convert.ToHexString(uid).ToLowerInvariant();
                if (expectedUid is not null && !string.Equals(Uid, expectedUid, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(L.Get("ADifferentCameraAnsweredAtThisAddress"));
                peer = source;
                Send(Wire.Packet(0x41, uid));
                while (Now < deadline)
                {
                    cancellation.ThrowIfCancellationRequested();
                    packet = Receive(buffer);
                    if (packet is null) { Send(Wire.Packet(0x41, uid)); continue; }
                    (bytes, source) = packet.Value;
                    if (source.Equals(peer) && bytes.Length >= 4 && bytes[0] == 0xf1 && bytes[1] == 0x42)
                    {
                        Connected = true; lastReceived = Now;
                        receiver = Task.Run(ReceiveLoop);
                        return;
                    }
                }
            }
            throw new TimeoutException(L.Get("TheCameraDidNotAnswerLANDiscovery"));
        }, cancellation);
    }

    (byte[] Bytes, IPEndPoint Peer)? Receive(byte[] buffer)
    {
        EndPoint source = new IPEndPoint(IPAddress.Any, 0);
        try { int n = socket.ReceiveFrom(buffer, ref source); return (buffer[..n], (IPEndPoint)source); }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock) { return null; }
    }

    void Send(byte[] packet)
    {
        lock (sendLock)
        {
            if (peer is null) throw new InvalidOperationException(L.Get("CameraIsDisconnected"));
            socket.SendTo(packet, peer);
        }
    }

    public async Task<CameraReply?> CommandAsync(ushort command, byte[]? data = null, ushort? response = null,
                                                CancellationToken cancellation = default)
    {
        if (!Connected) throw new IOException(L.Get("CameraIsDisconnected"), Error);
        data ??= [];
        ushort requestNumber;
        var completion = new TaskCompletionSource<CameraReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sendLock)
        {
            requestNumber = ++number;
            string nonce = noncePrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            var body = Wire.Join(Wire.U16(command), Wire.U16(requestNumber), [0, 0], Wire.U16(checked((ushort)data.Length)), Wire.Auth(password, nonce), data);
            var message = Wire.Join([2, 3, 0, 0], Wire.U32((uint)body.Length), body);
            ushort seq = sequence++;
            var packet = Wire.Packet(0xd0, Wire.Join([0xd1, 0], Wire.U16(seq), message));
            if (response.HasValue) requests[requestNumber] = (response.Value, completion);
            unacked[(0, seq)] = (packet, Now, 0);
            Send(packet);
        }
        if (!response.HasValue) return null;
        try
        {
            var reply = await completion.Task.WaitAsync(TimeSpan.FromSeconds(4), cancellation);
            if (reply.AuthenticationResult != 0) throw new CameraAuthenticationException(reply.AuthenticationResult);
            if (reply.Unsupported != 0) throw new NotSupportedException(L.Format("CameraDoesNotSupportCommand0x0X4", command));
            return reply;
        }
        finally { requests.TryRemove(requestNumber, out _); }
    }

    void ReceiveLoop()
    {
        try
        {
            var buffer = new byte[65535];
            while (!stop.IsCancellationRequested)
            {
                var received = Receive(buffer);
                if (received is not null && received.Value.Peer.Equals(peer))
                {
                    byte[] p = received.Value.Bytes;
                    if (p.Length < 4 || p[0] != 0xf1 || Wire.U16(p.AsSpan(2)) != p.Length - 4) continue;
                    lastReceived = Now;
                    switch (p[1])
                    {
                        case 0xe0: Send(Wire.Packet(0xe1)); break;
                        case 0xf0: throw new IOException(L.Get("CameraEndedTheSession"));
                        case 0xd1 when p.Length >= 8 && p[4] == 0xd1 && p[5] <= 1:
                            int count = Wire.U16(p.AsSpan(6));
                            if (p.Length == 8 + count * 2)
                                lock (sendLock) for (int i = 8; i < p.Length; i += 2) unacked.Remove((p[5], Wire.U16(p.AsSpan(i))));
                            break;
                        case 0xd0 when p.Length >= 8 && p[4] == 0xd1 && p[5] < 6:
                            byte channel = p[5]; ushort seq = Wire.U16(p.AsSpan(6));
                            Send(Wire.Packet(0xd1, Wire.Join([0xd1, channel, 0, 1], Wire.U16(seq))));
                            foreach (var message in channels[channel].Feed(seq, p[8..])) Handle(channel, message.Kind, message.Ability, message.Body);
                            break;
                    }
                }
                if (Now - lastReceived > 10000) throw new TimeoutException(L.Get("CameraConnectionWasLost"));
                lock (sendLock)
                    foreach (var item in unacked.ToArray())
                        if (Now - item.Value.Sent > 300)
                        {
                            if (item.Value.Attempts >= 12) throw new TimeoutException(L.Get("CameraDidNotAcknowledgeACommand"));
                            Send(item.Value.Packet);
                            unacked[item.Key] = (item.Value.Packet, Now, item.Value.Attempts + 1);
                        }
            }
        }
        catch (Exception e) { if (!stop.IsCancellationRequested) Error = e; }
        finally
        {
            Connected = false;
            foreach (var pending in requests.Values) pending.Completion.TrySetException(Error ?? new IOException(L.Get("CameraDisconnected")));
            video.Writer.TryComplete(Error);
            audio.Writer.TryComplete(Error);
        }
    }

    void Handle(byte channel, byte kind, byte ability, byte[] body)
    {
        if (kind == 3 && body.Length >= 40)
        {
            ushort cmd = Wire.U16(body), nr = Wire.U16(body.AsSpan(2)), extra = Wire.U16(body.AsSpan(4)), size = Wire.U16(body.AsSpan(6));
            uint result = Wire.U32(body.AsSpan(8));
            if (40 + extra + size > body.Length) throw new InvalidDataException(L.Get("TruncatedCommandResponse"));
            if (requests.TryGetValue(nr, out var request) && (request.Response == cmd || result != 0))
                request.Completion.TrySetResult(new(cmd, nr, result, body[(40 + extra)..(40 + extra + size)], ability));
        }
        else if (kind == 2 && channel == 1 && body.Length >= 24)
        {
            var data = body[24..]; int encrypted = data.Length / 16 * 16;
            if (encrypted > 0) aes.DecryptEcb(data.AsSpan(0, encrypted), PaddingMode.None).CopyTo(data, 0);
            var frame = new AudioFrame(data, Wire.U16(body.AsSpan(6)), Wire.U32(body.AsSpan(12)), Wire.U32(body.AsSpan(20)), Wire.U16(body), body[2]);
            if (!audio.Writer.TryWrite(frame)) throw new IOException(L.Get("AudioProcessingFellBehindReconnectToRecover"));
        }
        else if (kind == 1 && body.Length >= 24)
        {
            byte[] data = body[24..]; bool keyframe = channel is 2 or 4;
            if (keyframe && data.Length >= 36) aes.DecryptEcb(data.AsSpan(4, 32), PaddingMode.None).CopyTo(data, 4);
            var frame = new VideoFrame(data, Wire.U16(body.AsSpan(6)), Wire.U16(body.AsSpan(8)), Wire.U16(body.AsSpan(10)),
                Wire.U32(body.AsSpan(12)), Wire.U32(body.AsSpan(20)), keyframe, body[5], Wire.U16(body));
            if (!video.Writer.TryWrite(frame)) throw new IOException(L.Get("VideoProcessingFellBehindReconnectToRecover"));
        }
    }

    public async Task<string> FirmwareAsync() => Encoding.ASCII.GetString((await CommandAsync(0x1300, response: 0x1301))!.Data).TrimEnd('\0');
    public async Task<IReadOnlyList<CameraAlert>> AlertHistoryAsync(uint from, uint to, CancellationToken cancellation = default)
    {
        if (to < from) throw new ArgumentException(L.Get("AlertTimeRangeIsReversed"));
        var reply = await CommandAsync(0x5c06, Wire.Join(new byte[4], Wire.U32(from), Wire.U32(to)), 0x5c07, cancellation);
        return CameraAlert.Parse(reply!.Data);
    }
    public async Task<CameraSettings> SettingsAsync()
    {
        var data = (await CommandAsync(0x0330, new byte[4], 0x0331))!.Data;
        if (data.Length < 92 || data[8] != 253) throw new NotSupportedException(L.Get("ThisVersionSupportsTheVerifiedHardware253SettingsLayout"));
        return new(data[8], data[91], data[68], data[54]);
    }
    public Task StartVideoAsync(byte quality = 1) => CommandAsync(0x2345, [++generation, quality, 1, 0]);
    public Task StopVideoAsync() => CommandAsync(0x02ff, new byte[8]);
    public Task StartAudioAsync() => CommandAsync(0x0300, new byte[8]);
    public Task StopAudioAsync() => CommandAsync(0x0301, new byte[8]);
    /// <summary>Use a dedicated talk session: briefly starts/stops video to initialize firmware audio decryption.</summary>
    public async Task StartSpeakerAsync(byte quality = 1, CancellationToken cancellation = default)
    {
        if (quality > 2) throw new ArgumentOutOfRangeException(nameof(quality));
        cancellation.ThrowIfCancellationRequested();
        await StartVideoAsync(quality);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try { await Frames.ReadAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            { throw new TimeoutException(L.Get("CameraDidNotInitializeTheTalkStream")); }
        }
        // A transport ACK precedes firmware work. Match the settling intervals of the audible probe.
        await Task.Delay(300, cancellation);
        await StopVideoAsync();
        await FlushCommandsAsync(cancellation);
        await Task.Delay(300, cancellation);
        while (Frames.TryRead(out _)) { }
        await CommandAsync(0x0350, new byte[4], cancellation: cancellation);
        await FlushCommandsAsync(cancellation);
        await Task.Delay(200, cancellation);
        lock (sendLock) { talkFrames = 0; speaking = true; }
    }
    public async Task StopSpeakerAsync()
    {
        lock (sendLock)
        {
            speaking = false;
            // Do not retransmit stale speech after stop. This session is then closed by its owner.
            foreach (var key in unacked.Keys.Where(key => key.Channel == 1).ToArray()) unacked.Remove(key);
        }
        if (!Connected) return;
        await CommandAsync(0x0351, new byte[8]);
        await FlushCommandsAsync();
    }
    async Task FlushCommandsAsync(CancellationToken cancellation = default)
    {
        long deadline = Now + 2000;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Connected) throw new IOException(L.Get("TalkSessionDisconnected"), Error);
            lock (sendLock) { if (!unacked.Keys.Any(key => key.Channel == 0)) return; }
            if (Now >= deadline) throw new TimeoutException(L.Get("CameraDidNotAcknowledgeSpeakerControl"));
            await Task.Delay(10, cancellation);
        }
    }
    public void SendTalkAudio(byte[] adts)
    {
        lock (sendLock)
        {
            if (!Connected || !speaking) throw new IOException(L.Get("CameraSpeakerIsNotStarted"), Error);
            if (unacked.Keys.Count(key => key.Channel == 1) >= 16)
                throw new IOException(L.Get("TalkTransportFellBehindStopAndRestartTalking"));
            var message = TalkAudio.Message(adts, password, ++talkFrames);
            ushort seq = audioSequence++;
            var packet = Wire.Packet(0xd0, Wire.Join([0xd1, 1], Wire.U16(seq), message));
            unacked[(1, seq)] = (packet, Now, 0); Send(packet);
        }
    }
    public Task QualityAsync(uint quality)
    {
        if (quality > 2) throw new ArgumentOutOfRangeException(nameof(quality));
        return CommandAsync(0x1311, Wire.Join(Wire.U32(quality), Wire.U32(++generation)), 0x1312);
    }
    public Task NightVisionAsync(uint mode)
    {
        if (mode > 2) throw new ArgumentOutOfRangeException(nameof(mode));
        return CommandAsync(0x1380, Wire.U32(mode), 0x1381);
    }
    public Task TrackingAsync(bool enabled) => CommandAsync(0x400b, Wire.U32(enabled ? 1u : 0u), 0x400c);
    public Task RotateAsync(bool enabled) => CommandAsync(0x131f, Wire.U32(enabled ? 1u : 0u), 0x1320);
    public async Task<uint> GimbalRestoreDelayAsync()
    {
        var data = (await CommandAsync(0x1396, new byte[4], 0x1397))!.Data;
        if (data.Length != 4) throw new InvalidDataException(L.Get("UnrecognizedGimbalRestoreResponse"));
        return Wire.U32(data);
    }
    public async Task GimbalRestoreAsync(bool enabled)
    {
        uint value = enabled ? 20u : 0u; // The observed mobile switch uses 20 for on, zero for off.
        await CommandAsync(0x1394, Wire.U32(value), 0x1395);
        // The reference firmware returns device info here, despite the mobile SDK's uint32 callback.
        if (await GimbalRestoreDelayAsync() != value) throw new IOException(L.Get("CameraDidNotConfirmTheGimbalRestoreSetting"));
    }
    public Task StopMovingAsync() => CommandAsync(0x4013, new byte[4]);
    public async Task MoveAsync(uint direction)
    {
        if (direction is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(direction));
        try { await CommandAsync(0x4012, Wire.Join(Wire.U32(direction), new byte[4])); await Task.Delay(120); }
        finally { if (Connected) await StopMovingAsync(); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        stop.Cancel();
        try { if (peer is not null) Send(Wire.Packet(0xf0)); } catch (SocketException) { }
        socket.Close();
        try { receiver?.Wait(1000); } catch (AggregateException) { }
        aes.Dispose(); stop.Dispose();
    }
}

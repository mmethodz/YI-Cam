using YiLocal.Core;

namespace YiLocal.Windows;

internal sealed class GridCamera(CameraRegistration registration, DeviceProfile profile, Action<Action> dispatch) : IAsyncDisposable
{
    readonly object gate = new();
    VideoPreview? decoder;
    string? identity, ffmpeg;
    CameraSession? session;
    public CameraRegistration Registration { get; set; } = registration;
    public DeviceProfile Profile { get; } = profile;
    public CameraSession? Session => session;
    public bool Recording => session?.Recording == true;
    public Panel View { get; } = new() { Dock = DockStyle.Fill, Padding = new Padding(5), BorderStyle = BorderStyle.FixedSingle };
    public PictureBox Picture { get; } = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(26, 29, 34), SizeMode = PictureBoxSizeMode.Zoom };
    public Label Status { get; } = new() { Text = L.Get("Disconnected"), Dock = DockStyle.Bottom, Height = 44, AutoEllipsis = true };
    public Button Connect { get; } = new() { Text = L.Get("Connect"), AutoSize = true };
    public Button Record { get; } = new() { Text = L.Get("Record"), AutoSize = true, Enabled = false };
    public Label Title { get; } = new() { Text = profile.Name, Dock = DockStyle.Top, AutoSize = true };
    public void Start(string? executable)
    {
        if (session is not null) return; ffmpeg = executable;
        var active = new CameraSession(Profile); session = active; Connect.Text = L.Get("Disconnect"); Record.Enabled = true;
        active.Status += text => dispatch(() => { if (session == active) Status.Text = text; });
        active.RecordingChanged += value => dispatch(() =>
        {
            if (session != active) return;
            Record.Text = value ? L.Get("StopRecording") : L.Get("Record");
            if (!value) Status.Text = L.Get("Recording.OffStatus");
        });
        active.Settings += _ => { lock (gate) { decoder?.Dispose(); decoder = null; identity = null; } };
        active.PairingRejected += () => dispatch(() => { if (session == active) Status.Text = L.Get("DeviceKeyRejectedEditThisCameraAndImportItsRefreshedPairing"); });
        active.Frame += (frame, time, epoch) =>
        {
            lock (gate)
            {
                string next = $"{epoch}:{frame.Generation}:{frame.Width}:{frame.Height}";
                if (identity != next || decoder?.Failed == true) { decoder?.Dispose(); decoder = null; identity = next; }
                if (decoder is null && frame.Keyframe && File.Exists(ffmpeg))
                    try { decoder = new(ffmpeg!, 480, 270); }
                    catch (Exception e) { dispatch(() => Status.Text = L.Get("Preview") + e.Message); }
                if (decoder is not null && !decoder.Push(frame.Data)) { decoder.Dispose(); decoder = null; }
            }
        };
        active.Start();
    }
    public void PaintFrame()
    {
        Bitmap? next; lock (gate) next = decoder?.Take();
        if (next is not null) { var previous = Picture.Image; Picture.Image = next; previous?.Dispose(); }
    }
    public async Task Stop()
    {
        var previous = session; session = null;
        try { if (previous is not null) await previous.DisposeAsync(); }
        finally
        {
            lock (gate) { decoder?.Dispose(); decoder = null; identity = null; }
            var image = Picture.Image; Picture.Image = null; image?.Dispose();
            Connect.Text = L.Get("Connect"); Record.Text = L.Get("Record"); Record.Enabled = false; Status.Text = L.Get("Disconnected");
        }
    }
    public async ValueTask DisposeAsync() { await Stop(); View.Dispose(); }
}

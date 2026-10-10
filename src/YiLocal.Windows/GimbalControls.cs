using YiLocal.Core;

namespace YiLocal.Windows;

/// <summary>Camera readback and local arrow preferences are deliberately separate.</summary>
internal sealed class GimbalControls : FlowLayoutPanel
{
    readonly Func<CameraSession?> session;
    readonly Func<DeviceProfile?> profile;
    readonly Func<Func<Task>, Task> guard;
    readonly CheckBox rotation = Choice("Rotate image 180°");
    readonly CheckBox restore = Choice("Restore gimbal · experimental");
    readonly CheckBox reversePan = Choice("Reverse left / right controls");
    readonly CheckBox reverseTilt = Choice("Reverse up / down controls");
    int revision;
    bool rotationRead, restoreRead;
    static CheckBox Choice(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(224, 0) };

    public GimbalControls(Func<CameraSession?> session, Func<DeviceProfile?> profile, Func<Func<Task>, Task> guard)
    {
        this.session = session; this.profile = profile; this.guard = guard;
        AutoSize = true; FlowDirection = FlowDirection.TopDown; WrapContents = false; Enabled = false;
        Controls.Add(rotation); Controls.Add(restore); Controls.Add(reversePan); Controls.Add(reverseTilt);
        Controls.Add(new Label { Text = "Direction switches swap OpenYI's arrows for this camera.", AutoSize = true, MaximumSize = new Size(224, 0) });
        rotation.Click += (_, _) => _ = guard(() => ChangeCameraSetting(rotation, false));
        restore.Click += (_, _) => _ = guard(() => ChangeCameraSetting(restore, true));
        reversePan.Click += (_, _) => _ = guard(() => SaveDirectionPreference(true));
        reverseTilt.Click += (_, _) => _ = guard(() => SaveDirectionPreference(false));
    }

    public async void Refresh(CameraSettings? settings)
    {
        int version = ++revision;
        Enabled = settings is not null;
        restore.Enabled = false;
        if (settings is null) return;
        rotation.Checked = rotationRead = settings.Rotation != 0;
        rotation.Enabled = settings.Rotation <= 1;
        reversePan.Checked = profile()?.ReversePanControls == true;
        reverseTilt.Checked = profile()?.ReverseTiltControls == true;
        restore.Text = "Restore gimbal · reading…";
        var client = session()?.Client;
        try
        {
            if (client is null) return;
            uint value = await client.GimbalRestoreDelayAsync();
            if (version != revision || IsDisposed || session()?.Client != client) return;
            restore.Checked = restoreRead = value != 0;
            restore.Text = "Restore gimbal · experimental"; restore.Enabled = true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or NotSupportedException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
        {
            if (version == revision && !IsDisposed)
            { restore.Text = "Restore gimbal · unavailable"; restore.Enabled = false; }
        }
    }

    async Task ChangeCameraSetting(CheckBox control, bool gimbal)
    {
        var client = session()?.Client ?? throw new IOException("Camera is disconnected.");
        bool wanted = control.Checked, before = gimbal ? restoreRead : rotationRead;
        control.Enabled = false;
        try
        {
            bool actual;
            if (gimbal)
            { await client.GimbalRestoreAsync(wanted); actual = await client.GimbalRestoreDelayAsync() != 0; restoreRead = actual; }
            else
            { await client.RotateAsync(wanted); actual = (await client.SettingsAsync()).Rotation != 0; rotationRead = actual; }
            control.Checked = actual;
            if (actual != wanted) throw new IOException("Camera readback differs from the requested setting.");
        }
        catch { control.Checked = before; throw; }
        finally { if (!IsDisposed) control.Enabled = session()?.Client == client; }
    }

    Task SaveDirectionPreference(bool pan)
    {
        var device = profile() ?? throw new IOException("No saved camera.");
        bool oldPan = device.ReversePanControls, oldTilt = device.ReverseTiltControls;
        try
        {
            if (pan) device.ReversePanControls = reversePan.Checked; else device.ReverseTiltControls = reverseTilt.Checked;
            device.Save();
        }
        catch
        {
            device.ReversePanControls = reversePan.Checked = oldPan;
            device.ReverseTiltControls = reverseTilt.Checked = oldTilt; throw;
        }
        return Task.CompletedTask;
    }
}

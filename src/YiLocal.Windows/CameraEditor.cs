using YiLocal.Core;

namespace YiLocal.Windows;

internal sealed class CameraEditor : Form
{
    readonly TextBox name = new() { Width = 430 };
    readonly TextBox ip = new() { Width = 430 };
    readonly TextBox key = new() { Width = 430, UseSystemPasswordChar = true };
    readonly Label state = new() { AutoSize = true, MaximumSize = new Size(530, 0) };
    readonly DeviceProfile? previous;
    bool busy;
    public DeviceProfile? Verified { get; private set; }
    public CameraEditor(DeviceProfile? previous = null)
    {
        this.previous = previous; Text = previous is null ? "Add camera · experimental" : "Edit camera · experimental";
        ClientSize = new Size(590, 430); MinimumSize = new Size(610, 460); StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12) }; Controls.Add(body);
        void Field(string label, TextBox box) { body.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 10, 3, 3) }); body.Controls.Add(box); }
        Field("Camera name", name); Field("Camera IPv4 address on your LAN", ip); Field("Device pairing key · blank keeps the existing key", key);
        name.Text = previous?.Name ?? "Camera"; ip.Text = previous?.Ip ?? "";
        void Add(string label, Func<CancellationToken, Task<DeviceProfile>> read)
        {
            var button = new Button { Text = label, AutoSize = true };
            button.Click += async (_, _) =>
            {
                if (busy) return; busy = true; body.Enabled = false;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                try
                {
                    state.Text = "Reading and verifying the local pairing…";
                    var candidate = await read(timeout.Token);
                    if (string.IsNullOrWhiteSpace(candidate.Name) || candidate.Name.Length > 100) throw new ArgumentException("Use a camera name of 1–100 characters.");
                    Verified = await PairingImport.VerifyAsync(candidate, timeout.Token);
                    busy = false;
                    DialogResult = DialogResult.OK;
                }
                catch (Exception e) { state.Text = e is OperationCanceledException ? "Pairing verification timed out." : e.Message; }
                finally { busy = false; body.Enabled = true; }
            };
            body.Controls.Add(button);
        }
        Add("Verify and save", _ => Task.FromResult(new DeviceProfile { Name = name.Text.Trim(), Ip = ip.Text.Trim(), Password = key.Text.Length > 0 ? key.Text : previous?.Password ?? "", Uid = previous?.Uid }));
        Add("Import from running YI IoT", token => VendorClientImporter.ReadProfileAsync(ip.Text.Trim(), name.Text.Trim(), previous?.Uid, token));
        Add("Import encrypted pairing profile…", _ =>
        {
            using var dialog = new OpenFileDialog { Filter = "Encrypted Windows pairing profile|*.dpapi" };
            if (dialog.ShowDialog(this) != DialogResult.OK) throw new OperationCanceledException();
            var imported = DeviceProfile.Load(dialog.FileName);
            if (previous?.Uid is { } uid && !string.Equals(uid, imported.Uid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The imported profile belongs to another camera. Add it as a separate entry.");
            return Task.FromResult(imported);
        });
        body.Controls.Add(new Label { Text = "Open this camera's live view in YI IoT before importing from it. The new key is saved only after LAN authentication succeeds.", AutoSize = true, MaximumSize = new Size(530, 0), Margin = new Padding(3, 10, 3, 3) });
        body.Controls.Add(state);
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; state.Text = "Wait for pairing verification to finish."; } };
    }
}

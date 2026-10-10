using YiLocal.Core;

namespace YiLocal.Windows;

internal sealed class CameraEditor : Form
{
    readonly TextBox name = new() { Width = 430 };
    readonly TextBox ip = new() { Width = 430 };
    readonly TextBox key = new() { Width = 430, UseSystemPasswordChar = true };
    readonly CheckBox localPlain = new() { AutoSize = true, Text = L.Get("LocalPlainMode") };
    readonly Label state = new() { AutoSize = true, MaximumSize = new Size(530, 0) };
    readonly DeviceProfile? previous;
    bool busy;
    public DeviceProfile? Verified { get; private set; }
    public CameraEditor(DeviceProfile? previous = null)
    {
        this.previous = previous; Text = previous is null ? L.Get("AddCameraExperimental") : L.Get("EditCameraExperimental");
        ClientSize = new Size(990, 690); MinimumSize = new Size(750, 570); StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
        var pages = new TabControl { Dock = DockStyle.Fill }; Controls.Add(pages);
        var localPage = new TabPage(L.Get("Setup.LocalTab")); pages.TabPages.Add(localPage);
        var local = new LocalSetupPanel(verified =>
        {
            if (previous?.Uid is { } uid && !string.Equals(uid, verified.Uid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L.Get("TheImportedProfileBelongsToAnotherCameraAddItAsA"));
            Verified = verified; DialogResult = DialogResult.OK; return Task.CompletedTask;
        }, () => previous);
        localPage.Controls.Add(local);
        var manualPage = new TabPage(L.Get("Setup.ManualTab")); pages.TabPages.Add(manualPage);
        local.BusyChanged += active => manualPage.Enabled = !active;
        var body = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty }; manualPage.Controls.Add(new ScrollableColumn(body));
        void Field(string label, TextBox box) { body.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 10, 3, 3) }); body.Controls.Add(box); }
        Field(L.Get("CameraName"), name); Field(L.Get("CameraIPv4AddressOnYourLAN"), ip); Field(L.Get("DevicePairingKeyBlankKeepsTheExistingKey"), key);
        name.Text = previous?.Name ?? L.Get("Camera"); ip.Text = previous?.Ip ?? "";
        localPlain.Checked = previous?.Protocol == CameraProtocol.LocalPlain;
        key.Enabled = !localPlain.Checked;
        localPlain.CheckedChanged += (_, _) => key.Enabled = !localPlain.Checked;
        body.Controls.Add(localPlain);
        void Add(string label, Func<CancellationToken, Task<DeviceProfile>> read)
        {
            var button = new Button { Text = label, AutoSize = true };
            button.Click += async (_, _) =>
            {
                if (busy || local.Busy) return; busy = true; body.Enabled = localPage.Enabled = false;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                try
                {
                    state.Text = L.Get("ReadingAndVerifyingTheLocalPairing");
                    var candidate = await read(timeout.Token);
                    if (string.IsNullOrWhiteSpace(candidate.Name) || candidate.Name.Length > 100) throw new ArgumentException(L.Get("UseACameraNameOf1100Characters"));
                    Verified = await PairingImport.VerifyAsync(candidate, timeout.Token);
                    busy = false;
                    DialogResult = DialogResult.OK;
                }
                catch (Exception e) { state.Text = e is OperationCanceledException ? L.Get("PairingVerificationTimedOut") : e.Message; }
                finally { busy = false; if (!IsDisposed) body.Enabled = localPage.Enabled = true; }
            };
            body.Controls.Add(button);
        }
        Add(L.Get("VerifyAndSave"), _ => Task.FromResult(new DeviceProfile { Name = name.Text.Trim(), Ip = ip.Text.Trim(),
            Protocol = localPlain.Checked ? CameraProtocol.LocalPlain : CameraProtocol.Stock,
            Password = localPlain.Checked ? "" : key.Text.Length > 0 ? key.Text : previous?.Password ?? "", Uid = previous?.Uid }));
        Add(L.Get("ImportFromRunningYIIoT"), token => VendorClientImporter.ReadProfileAsync(ip.Text.Trim(), name.Text.Trim(), previous?.Uid, token));
        Add(L.Get("ImportEncryptedPairingProfile"), _ =>
        {
            using var dialog = new OpenFileDialog { Filter = L.Get("EncryptedWindowsPairingProfileDpapi") };
            if (dialog.ShowDialog(this) != DialogResult.OK) throw new OperationCanceledException();
            var imported = DeviceProfile.Load(dialog.FileName);
            if (previous?.Uid is { } uid && !string.Equals(uid, imported.Uid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L.Get("TheImportedProfileBelongsToAnotherCameraAddItAsA"));
            return Task.FromResult(imported);
        });
        body.Controls.Add(new Label { Text = L.Get("OpenThisCameraSLiveViewInYIIoTBeforeImporting"), AutoSize = true, MaximumSize = new Size(530, 0), Margin = new Padding(3, 10, 3, 3) });
        body.Controls.Add(state);
        FormClosing += (_, e) => { if (busy || local.Busy && Verified is null) { e.Cancel = true; state.Text = L.Get("WaitForPairingVerificationToFinish"); } };
    }
}

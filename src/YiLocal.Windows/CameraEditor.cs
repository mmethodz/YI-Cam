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
        this.previous = previous; Text = previous is null ? L.Get("AddCameraExperimental") : L.Get("EditCameraExperimental");
        ClientSize = new Size(590, 430); MinimumSize = new Size(610, 460); StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
        var body = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty }; Controls.Add(new ScrollableColumn(body));
        void Field(string label, TextBox box) { body.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 10, 3, 3) }); body.Controls.Add(box); }
        Field(L.Get("CameraName"), name); Field(L.Get("CameraIPv4AddressOnYourLAN"), ip); Field(L.Get("DevicePairingKeyBlankKeepsTheExistingKey"), key);
        name.Text = previous?.Name ?? L.Get("Camera"); ip.Text = previous?.Ip ?? "";
        void Add(string label, Func<CancellationToken, Task<DeviceProfile>> read)
        {
            var button = new Button { Text = label, AutoSize = true };
            button.Click += async (_, _) =>
            {
                if (busy) return; busy = true; body.Enabled = false;
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
                finally { busy = false; body.Enabled = true; }
            };
            body.Controls.Add(button);
        }
        Add(L.Get("VerifyAndSave"), _ => Task.FromResult(new DeviceProfile { Name = name.Text.Trim(), Ip = ip.Text.Trim(), Password = key.Text.Length > 0 ? key.Text : previous?.Password ?? "", Uid = previous?.Uid }));
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
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; state.Text = L.Get("WaitForPairingVerificationToFinish"); } };
    }
}

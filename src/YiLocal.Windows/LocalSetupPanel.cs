using YiLocal.Core;

namespace YiLocal.Windows;

/// <summary>Local01 onboarding. Secrets stay transient until a verified device profile is saved by the caller.</summary>
internal sealed class LocalSetupPanel : UserControl
{
    readonly Func<DeviceProfile, Task> save;
    readonly Func<DeviceProfile?> existing;
    readonly ComboBox ssid = new() { Width = 360, DropDownStyle = ComboBoxStyle.DropDown };
    readonly TextBox wifiPassword = new() { Width = 360, UseSystemPasswordChar = true };
    readonly CheckBox openNetwork = new() { AutoSize = true, Text = L.Get("Setup.OpenNetwork") };
    readonly ComboBox address = new() { Width = 240, DropDownStyle = ComboBoxStyle.DropDown };
    readonly TextBox name = new() { Width = 240 };
    readonly TextBox maintenancePassword = new() { Width = 250, UseSystemPasswordChar = true, Text = LocalFirmwarePairing.DefaultMaintenancePassword };
    readonly ComboBox region = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    readonly Label state = new() { AutoSize = true, MaximumSize = new Size(760, 0), Margin = new Padding(8), AccessibleRole = AccessibleRole.StatusBar };
    readonly PictureBox qr = new() { Size = new Size(300, 300), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, AccessibleName = L.Get("Setup.QrPreview") };
    readonly FlowLayoutPanel fields = Column();
    readonly Button cancel = new() { Text = L.Get("Setup.Cancel"), AutoSize = true, Visible = false };
    CancellationTokenSource? operation;
    byte[]? imageBytes;
    bool detected;
    bool applyingNetwork;
    public bool Busy => operation is not null;
    public event Action<bool>? BusyChanged;

    public LocalSetupPanel(Func<DeviceProfile, Task> save, Func<DeviceProfile?> existing)
    {
        this.save = save; this.existing = existing;
        Dock = DockStyle.Fill;
        var content = Column(); Controls.Add(new ScrollableColumn(content));
        content.Controls.Add(Note(L.Get("Setup.LocalOnly"), 840));
        var columns = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty, MaximumSize = new Size(1040, 0) };
        fields.Width = 490;
        columns.Controls.Add(fields);
        var preview = Column(); preview.Margin = new Padding(18, 8, 8, 8);
        preview.Controls.Add(qr); preview.Controls.Add(Note(L.Get("Setup.ScanInstructions"), 310));
        columns.Controls.Add(preview); content.Controls.Add(columns);

        fields.Controls.Add(Heading(L.Get("Setup.StepWifi")));
        Field(fields, L.Get("WiFiNameSSID"), ssid);
        fields.Controls.Add(Row(Action(L.Get("Setup.UsePcWifi"), DetectWifi), Action(L.Get("Setup.UseCameraWifi"), async token =>
        {
            var camera = existing() ?? throw new InvalidOperationException(L.Get("Setup.NoExistingCamera"));
            ApplyNetwork(await LocalFirmwarePairing.ReadWifiAsync(camera.Ip, maintenancePassword.Text, token));
            state.Text = L.Get("Setup.CameraWifiLoaded");
        })));
        Field(fields, L.Get("WiFiPassword"), wifiPassword);
        var show = new CheckBox { Text = L.Get("Setup.ShowPassword"), AutoSize = true };
        show.CheckedChanged += (_, _) => wifiPassword.UseSystemPasswordChar = !show.Checked;
        openNetwork.CheckedChanged += (_, _) => { wifiPassword.Enabled = !openNetwork.Checked; InvalidateQr(); };
        fields.Controls.Add(Row(openNetwork, show));
        fields.Controls.Add(Row(Action(L.Get("Setup.ShowQr"), _ => { MakeQr(); return Task.CompletedTask; }),
            Action(L.Get("SaveQRImage"), _ =>
            {
                MakeQr();
                using var dialog = new SaveFileDialog { Filter = L.Get("PNGImagePng"), FileName = "OpenYI-setup.png", OverwritePrompt = true };
                if (dialog.ShowDialog(this) == DialogResult.OK) { File.WriteAllBytes(dialog.FileName, imageBytes!); state.Text = L.Get("Setup.QrSaved"); }
                return Task.CompletedTask;
            })));
        fields.Controls.Add(Note(L.Get("Setup.QrPrivacy"), 470));

        fields.Controls.Add(Heading(L.Get("Setup.StepConnect")));
        fields.Controls.Add(Row(address, Action(L.Get("Setup.FindCameras"), async token =>
        {
            string prior = address.Text;
            var cameras = await CameraDiscovery.FindAsync(token);
            address.Items.Clear(); address.Items.AddRange(cameras.Cast<object>().ToArray());
            var selected = cameras.FirstOrDefault(c => c.Ip == prior);
            if (selected is not null) address.SelectedItem = selected;
            else if (cameras.Count == 1) address.SelectedIndex = 0;
            else address.Text = prior;
            state.Text = cameras.Count == 0 ? L.Get("Setup.NoneFound") : L.Format("Setup.FoundCameras", cameras.Count);
        })));
        address.AccessibleName = L.Get("CameraIPv4AddressOnYourLAN");
        Field(fields, L.Get("CameraName"), name);
        name.Text = existing()?.Name ?? L.Get("Camera"); address.Text = existing()?.Ip ?? "";
        fields.Controls.Add(Action(L.Get("Setup.ConnectSave"), async token =>
        {
            string ip = address.Text.Trim();
            string? uid = address.SelectedItem is DiscoveredCamera found && found.Ip == ip ? found.Uid : null;
            var candidate = await LocalFirmwarePairing.ReadProfileAsync(ip, name.Text, maintenancePassword.Text, uid, token);
            var verified = await PairingImport.VerifyAsync(candidate, token);
            token.ThrowIfCancellationRequested();
            await save(verified);
            if (!IsDisposed) { wifiPassword.Clear(); ssid.Items.Clear(); InvalidateQr(); state.Text = L.Get("Setup.Saved"); }
        }));

        var advanced = Column(); advanced.Visible = false;
        region.Items.AddRange(["EU", "US", "CN"]); region.SelectedIndex = 0;
        Field(advanced, L.Get("LocalQrRegion"), region);
        Field(advanced, L.Get("Setup.MaintenancePassword"), maintenancePassword);
        advanced.Controls.Add(Note(L.Get("Setup.MaintenanceScope"), 470));
        var toggle = new Button { Text = L.Get("Setup.Advanced"), AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
        toggle.Click += (_, _) => advanced.Visible = !advanced.Visible;
        fields.Controls.Add(toggle); fields.Controls.Add(advanced);
        content.Controls.Add(state); content.Controls.Add(cancel);
        cancel.Click += (_, _) => operation?.Cancel();
        ssid.TextChanged += (_, _) =>
        {
            if (!Busy && !applyingNetwork)
            {
                if (ssid.SelectedItem is WindowsWifiNetwork item && ssid.Text == item.Network.Ssid) ApplyPcNetwork(item);
                else { wifiPassword.Clear(); openNetwork.Checked = false; }
            }
            InvalidateQr();
        };
        ssid.SelectionChangeCommitted += (_, _) => { if (ssid.SelectedItem is WindowsWifiNetwork item) ApplyPcNetwork(item); };
        wifiPassword.TextChanged += (_, _) => InvalidateQr(); region.SelectedIndexChanged += (_, _) => InvalidateQr();
        VisibleChanged += (_, _) =>
        {
            if (Visible && !detected && FindForm()?.Visible == true)
            {
                detected = true;
                if (existing() is { } previous) { address.Text = previous.Ip; name.Text = previous.Name; }
                _ = Run(DetectWifi);
            }
        };
    }
    static FlowLayoutPanel Column() => new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
    static FlowLayoutPanel Row(params Control[] controls) { var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty }; row.Controls.AddRange(controls); return row; }
    static Label Note(string text, int width) => new() { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(3, 2, 3, 2) };
    static Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Margin = new Padding(3, 8, 3, 3) };
    static void Field(Control panel, string text, Control input) { panel.Controls.Add(Note(text, 470)); panel.Controls.Add(input); input.AccessibleName = text; }
    Button Action(string text, Func<CancellationToken, Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(3, 5, 3, 3) };
        button.Click += (_, _) => _ = Run(action); return button;
    }
    async Task Run(Func<CancellationToken, Task> action)
    {
        if (Busy) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); operation = timeout;
        BusyChanged?.Invoke(true);
        fields.Enabled = false; cancel.Visible = true; state.Text = L.Get("Setup.Working");
        try { await action(timeout.Token); }
        catch (OperationCanceledException) { if (!IsDisposed) state.Text = L.Get("Setup.Canceled"); }
        catch (Exception error) { if (!IsDisposed) state.Text = error.Message; }
        finally { operation = null; if (!IsDisposed) { fields.Enabled = true; cancel.Visible = false; BusyChanged?.Invoke(false); } }
    }
    async Task DetectWifi(CancellationToken token)
    {
        var networks = await Task.Run(WindowsWifi.ReadAvailable, token);
        token.ThrowIfCancellationRequested();
        ApplyWifiNetworks(networks);
    }
    internal void ApplyWifiNetworks(IReadOnlyList<WindowsWifiNetwork> networks)
    {
        if (networks.Count == 0) { state.Text = L.Get("Setup.NoPcWifi"); return; }
        string previous = ssid.Text;
        ssid.Items.Clear(); ssid.Items.AddRange(networks.Cast<object>().ToArray());
        int selected = WindowsWifi.DefaultNetwork(networks, previous);
        if (selected < 0) { ssid.Text = previous; state.Text = L.Get("Setup.ChoosePcWifi"); return; }
        ssid.SelectedIndex = selected; ApplyPcNetwork(networks[selected]);
    }
    void ApplyPcNetwork(WindowsWifiNetwork item)
    {
        ApplyNetwork(item.Network);
        state.Text = L.Get(item.Network.Password is null ? "Setup.PcWifiPasswordUnavailable"
            : item.Connected ? "Setup.PcWifiLoaded" : "Setup.SavedPcWifiLoaded");
    }
    void ApplyNetwork(WifiSetupNetwork network)
    {
        applyingNetwork = true;
        try { ssid.Text = network.Ssid; wifiPassword.Text = network.Password ?? ""; openNetwork.Checked = network.Password == ""; }
        finally { applyingNetwork = false; }
    }
    void MakeQr()
    {
        if (!openNetwork.Checked && wifiPassword.Text.Length == 0) throw new ArgumentException(L.Get("Setup.EnterWifiPassword"));
        imageBytes = QrProvisioning.Png(QrProvisioning.ComposeLocal(ssid.Text, openNetwork.Checked ? "" : wifiPassword.Text, (string)region.SelectedItem!));
        using var stream = new MemoryStream(imageBytes); using var source = Image.FromStream(stream);
        var old = qr.Image; qr.Image = new Bitmap(source); old?.Dispose();
        state.Text = L.Get("Setup.ScanInstructions");
    }
    void InvalidateQr() { imageBytes = null; var old = qr.Image; qr.Image = null; old?.Dispose(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { operation?.Cancel(); InvalidateQr(); }
        base.Dispose(disposing);
    }
}

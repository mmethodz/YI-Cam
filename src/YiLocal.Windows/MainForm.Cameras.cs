using YiLocal.Core;

namespace YiLocal.Windows;

public sealed partial class MainForm
{
    CameraRegistry? cameraRegistry;
    readonly CheckBox multipleCameras = new() { Text = "Enable additional cameras · experimental", AutoSize = true };
    readonly TableLayoutPanel cameraGrid = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly PictureBox primaryGridPicture = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(26, 29, 34), SizeMode = PictureBoxSizeMode.Zoom };
    readonly Label primaryGridTitle = new() { Text = "Primary camera", Dock = DockStyle.Top, AutoSize = true };
    readonly Label primaryGridState = new() { Text = "Disconnected", Dock = DockStyle.Bottom, Height = 44, AutoEllipsis = true };
    readonly Button primaryGridConnect = new() { Text = "Connect", AutoSize = true };
    readonly Button primaryGridRecord = new() { Text = "Record", AutoSize = true, Enabled = false };
    readonly Panel primaryGridPanel = new() { Dock = DockStyle.Fill, Padding = new Padding(5), BorderStyle = BorderStyle.FixedSingle };
    readonly List<GridCamera> extraCameras = [];
    bool gridBusy;
    bool keepingAwake, arrangingGrid;
    bool AnyRecording => session?.Recording == true || extraCameras.Any(camera => camera.Recording);
    void EnsurePrimaryDistinct(DeviceProfile candidate)
    {
        if (extraCameras.Any(camera => string.Equals(camera.Profile.Ip, candidate.Ip, StringComparison.OrdinalIgnoreCase) ||
            candidate.Uid is not null && string.Equals(camera.Profile.Uid, candidate.Uid, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This camera already has an additional-camera entry. Keep one configuration for each camera.");
    }

    void BuildCameras()
    {
        var page = Page("Cameras · experimental"); var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(Label("Experimental: one physical camera tested. Each additional camera has an independent session, encrypted pairing and recording folder. The Storage budget applies to each camera; allow disk/CPU capacity for their combined use.", 1080), 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.Add(multipleCameras);
        actions.Controls.Add(Button("Add camera…", () => _ = Guard(() => EditExtraCamera(null))));
        actions.Controls.Add(Button("Connect configured cameras", () => _ = Guard(async () =>
        {
            if (gridBusy || importing) return;
            if (session is null && profile is not null) await ToggleConnection();
            if (multipleCameras.Checked)
                foreach (var camera in extraCameras.Where(camera => camera.Registration.Enabled))
                { cameraRegistry!.EnsureDistinct(camera.Profile, profile, camera.Registration.Id); camera.Start(preferences.Ffmpeg); }
            UpdatePrimaryGridState();
        })));
        actions.Controls.Add(Button("Disconnect all", () => _ = Guard(async () =>
        {
            if (gridBusy) return; gridBusy = true;
            try { await StopExtraCameras(); await DisconnectAsync(); } finally { gridBusy = false; }
        })));
        actions.Controls.Add(Button("Arrange 1 / 2 columns", () => { cameraGrid.Tag = cameraGrid.Tag is true ? false : true; ArrangeCameras(); }));
        layout.Controls.Add(actions, 0, 1); layout.Controls.Add(cameraGrid, 0, 2); page.Controls.Add(layout);
        var primaryActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        primaryActions.Controls.Add(primaryGridConnect); primaryActions.Controls.Add(primaryGridRecord);
        primaryActions.Controls.Add(Button("Live controls", () => tabs.SelectedIndex = 0));
        primaryGridConnect.Click += (_, _) => _ = Guard(async () => { if (connect.Enabled && !gridBusy) await ToggleConnection(); UpdatePrimaryGridState(); });
        primaryGridRecord.Click += (_, _) => _ = Guard(ToggleRecordingAsync);
        primaryGridPanel.Controls.Add(primaryGridPicture); primaryGridPanel.Controls.Add(primaryGridTitle); primaryGridPanel.Controls.Add(primaryGridState); primaryGridPanel.Controls.Add(primaryActions);
        multipleCameras.Click += (_, _) => _ = Guard(async () =>
        {
            if (gridBusy) { multipleCameras.Checked = preferences.ExperimentalMultipleCameras; return; }
            gridBusy = true;
            try
            {
                if (!multipleCameras.Checked) await StopExtraCameras();
                preferences.ExperimentalMultipleCameras = multipleCameras.Checked; preferences.Save();
                foreach (var camera in extraCameras) camera.Connect.Enabled = multipleCameras.Checked && camera.Registration.Enabled;
            }
            finally { gridBusy = false; }
        });
        cameraGrid.SizeChanged += (_, _) => ArrangeCameras();
    }
    void LoadCameras()
    {
        try
        {
            cameraRegistry = new(); multipleCameras.Checked = preferences.ExperimentalMultipleCameras;
            foreach (var registration in cameraRegistry.Entries)
                try { AddCameraTile(registration, cameraRegistry.LoadProfile(registration.Id)); }
                catch (Exception e) { status.Text = "Additional camera could not be loaded: " + e.Message; }
        }
        catch (Exception e) { status.Text = "Camera registry could not be loaded: " + e.Message; }
        ArrangeCameras(); UpdatePrimaryGridState();
    }
    void ArrangeCameras()
    {
        if (arrangingGrid) return; arrangingGrid = true;
        cameraGrid.SuspendLayout(); cameraGrid.Controls.Clear(); cameraGrid.ColumnStyles.Clear(); cameraGrid.RowStyles.Clear();
        int count = 1 + extraCameras.Count, columns = cameraGrid.Tag is true || count == 1 ? 1 : 2;
        cameraGrid.ColumnCount = columns; cameraGrid.RowCount = (count + columns - 1) / columns;
        for (int column = 0; column < columns; column++) cameraGrid.ColumnStyles.Add(new(SizeType.Percent, 100f / columns));
        for (int row = 0; row < cameraGrid.RowCount; row++) cameraGrid.RowStyles.Add(new(SizeType.Absolute, cameraGrid.RowCount == 1 ? Math.Max(300, cameraGrid.ClientSize.Height - 8) : 300));
        cameraGrid.Controls.Add(primaryGridPanel, 0, 0);
        for (int i = 0; i < extraCameras.Count; i++) cameraGrid.Controls.Add(extraCameras[i].View, (i + 1) % columns, (i + 1) / columns);
        cameraGrid.ResumeLayout(); arrangingGrid = false;
    }
    void AddCameraTile(CameraRegistration registration, DeviceProfile device)
    {
        var camera = new GridCamera(registration, device, Ui); extraCameras.Add(camera);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        actions.Controls.Add(camera.Connect); actions.Controls.Add(camera.Record);
        var enabled = new CheckBox { Text = "Enabled", AutoSize = true, Checked = registration.Enabled }; actions.Controls.Add(enabled);
        enabled.Click += (_, _) => _ = Guard(async () =>
        {
            if (gridBusy) { enabled.Checked = camera.Registration.Enabled; return; }
            gridBusy = true;
            try
            {
                if (!enabled.Checked) await camera.Stop();
                cameraRegistry!.SetEnabled(registration.Id, enabled.Checked);
                camera.Registration = camera.Registration with { Enabled = enabled.Checked };
                camera.Connect.Enabled = multipleCameras.Checked && enabled.Checked;
            }
            finally { gridBusy = false; }
        });
        actions.Controls.Add(Button("Controls…", () => _ = Guard(() => ShowExtraControls(camera))));
        actions.Controls.Add(Button("Edit…", () => _ = Guard(() => EditExtraCamera(camera))));
        actions.Controls.Add(Button("Remove…", () => _ = Guard(async () =>
        {
            if (gridBusy) return;
            if (MessageBox.Show(this, "Remove this additional camera's configuration and encrypted pairing? Its recordings will remain on disk.", "Remove camera", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            gridBusy = true;
            try { await camera.Stop(); cameraRegistry!.Remove(registration.Id); extraCameras.Remove(camera); await camera.DisposeAsync(); ArrangeCameras(); }
            finally { gridBusy = false; }
        })));
        camera.View.Controls.Add(camera.Picture); camera.View.Controls.Add(camera.Title); camera.View.Controls.Add(camera.Status); camera.View.Controls.Add(actions);
        camera.Connect.Enabled = multipleCameras.Checked && registration.Enabled;
        camera.Connect.Click += (_, _) => _ = Guard(async () =>
        {
            if (gridBusy || !multipleCameras.Checked) return;
            camera.Connect.Enabled = false; gridBusy = true;
            try
            {
                if (camera.Session is not null) await camera.Stop();
                else { cameraRegistry!.EnsureDistinct(device, profile, registration.Id); camera.Start(preferences.Ffmpeg); }
            }
            finally { gridBusy = false; camera.Connect.Enabled = multipleCameras.Checked && camera.Registration.Enabled; }
        });
        camera.Record.Click += (_, _) => _ = Guard(async () =>
        {
            if (gridBusy || camera.Session is not { } active) return;
            camera.Record.Enabled = false; gridBusy = true;
            try
            {
                if (active.Recording) await Task.Run(active.StopRecording);
                else active.StartRecording(CameraRegistry.RecordingFolder(preferences.Folder, registration.Id), preferences.Storage, preferences.Recording, preferences.Ffmpeg);
            }
            finally { gridBusy = false; camera.Record.Enabled = camera.Session is not null; }
        });
    }
    async Task EditExtraCamera(GridCamera? existing)
    {
        if (gridBusy || importing) return;
        if (cameraRegistry is null) throw new IOException("Resolve the camera registry error before adding a camera.");
        gridBusy = true;
        try
        {
            using var dialog = new CameraEditor(existing?.Profile);
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Verified is not { } verified)
                return;
            cameraRegistry.EnsureDistinct(verified, profile, existing?.Registration.Id);
            bool resumeRecording = existing?.Recording == true;
            if (existing is not null) await existing.Stop();
            var registration = cameraRegistry.SaveVerified(verified, profile, existing?.Registration.Id);
            if (existing is not null) { extraCameras.Remove(existing); await existing.DisposeAsync(); }
            AddCameraTile(registration, verified); ArrangeCameras();
            var added = extraCameras[^1];
            if (multipleCameras.Checked && registration.Enabled)
            {
                added.Start(preferences.Ffmpeg);
                if (resumeRecording) added.Session!.StartRecording(CameraRegistry.RecordingFolder(preferences.Folder, registration.Id), preferences.Storage, preferences.Recording, preferences.Ffmpeg);
            }
            status.Text = "Additional camera verified and saved. Multi-camera hardware compatibility remains experimental.";
        }
        finally { gridBusy = false; }
    }
    async Task ShowExtraControls(GridCamera camera)
    {
        if (gridBusy) return;
        var active = camera.Session ?? throw new IOException("Connect this camera first.");
        var client = active.Client ?? throw new IOException("Wait for this camera to connect.");
        var settings = await client.SettingsAsync();
        using var dialog = new Form { Text = camera.Profile.Name + " · controls", ClientSize = new Size(400, 340), StartPosition = FormStartPosition.CenterParent, Font = Font };
        var body = Column(); dialog.Controls.Add(body);
        var qualityChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 310 };
        qualityChoice.Items.AddRange(["HD", "SD", "Automatic quality"]); qualityChoice.SelectedIndex = active.Quality switch { 2 => 1, 0 => 2, _ => 0 }; body.Controls.Add(qualityChoice);
        qualityChoice.SelectionChangeCommitted += (_, _) => _ = Guard(() => active.SetQualityAsync(new byte[] { 1, 2, 0 }[qualityChoice.SelectedIndex]));
        var lightChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 310 };
        lightChoice.Items.AddRange(["Infrared in darkness", "Colour lights", "Automatic lighting"]); lightChoice.SelectedIndex = Math.Min(2, (int)settings.NightVision); body.Controls.Add(lightChoice);
        lightChoice.SelectionChangeCommitted += (_, _) => _ = Guard(async () => { await client.NightVisionAsync((uint)lightChoice.SelectedIndex); lightChoice.SelectedIndex = (await client.SettingsAsync()).NightVision; });
        var follow = new CheckBox { Text = "Motion tracking", Checked = settings.Tracking != 0, AutoSize = true }; body.Controls.Add(follow);
        follow.Click += (_, _) => _ = Guard(async () => { await client.TrackingAsync(follow.Checked); follow.Checked = (await client.SettingsAsync()).Tracking != 0; });
        var directions = new FlowLayoutPanel { AutoSize = true };
        foreach (var (name, direction) in new[] { ("Up", 1u), ("Down", 2u), ("Left", 3u), ("Right", 4u) })
            directions.Controls.Add(Button(name, () => _ = Guard(() => client.MoveAsync(direction))));
        directions.Controls.Add(Button("Stop", () => _ = Guard(client.StopMovingAsync))); body.Controls.Add(directions);
        body.Controls.Add(Label("These controls affect this camera only. Tracking controls do not provide a recording trigger.", 340));
        dialog.ShowDialog(this);
    }
    async Task StopExtraCameras()
    {
        await Task.WhenAll(extraCameras.Select(camera => camera.Stop()));
    }
    void UpdatePrimaryGridState(string? message = null)
    {
        primaryGridTitle.Text = (profile?.Name ?? "No saved camera") + " · primary";
        primaryGridConnect.Text = session is null ? "Connect" : "Disconnect";
        primaryGridRecord.Enabled = record.Enabled; primaryGridRecord.Text = session?.Recording == true ? "Stop recording" : "Record";
        if (message is not null) primaryGridState.Text = message;
        else if (session is null) primaryGridState.Text = "Disconnected";
    }
    void SetPrimaryGridImage(Image? image) => primaryGridPicture.Image = image; // Owned/disposed by the original live preview.
    void UpdateCameraGrid()
    {
        foreach (var camera in extraCameras) camera.PaintFrame();
        UpdatePrimaryGridState();
        UpdateKeepAwake();
    }
    void UpdateKeepAwake()
    {
        bool wanted = AnyRecording;
        if (wanted == keepingAwake) return;
        SetThreadExecutionState(wanted ? 0x80000001u : 0x80000000u); keepingAwake = wanted;
    }
}

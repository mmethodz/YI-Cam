using System.Diagnostics;
using YiLocal.Core;

namespace YiLocal.Windows;

internal sealed class RecordingListView : ListView
{
    public event Action? ViewportChanged;
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg is 0x115 or 0x20a) ViewportChanged?.Invoke();
    }
}

public sealed partial class MainForm
{
    sealed record BrowserEntry(string Root, Clip Clip);
    readonly DateTimePicker recordedFrom = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", ShowCheckBox = true, Checked = false, Width = 200 };
    readonly DateTimePicker recordedUntil = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", ShowCheckBox = true, Checked = false, Width = 200 };
    readonly ComboBox filterCamera = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    readonly ComboBox filterKind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125 };
    readonly ComboBox filterProtection = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125 };
    readonly Label browserCount = new() { AutoSize = true };
    readonly PictureBox playbackPicture = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(26, 29, 34), SizeMode = PictureBoxSizeMode.Zoom };
    readonly Label playbackDetails = new() { Dock = DockStyle.Fill, Text = "Select a recording to inspect it, then click Play.", Padding = new Padding(10), AutoEllipsis = true };
    readonly TrackBar playbackSeek = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 10000, SmallChange = 100, LargeChange = 500, TickStyle = TickStyle.None, Enabled = false, AccessibleName = "Playback position" };
    readonly Label playbackTime = new() { AutoSize = true, Text = "00:00 / 00:00" };
    readonly Button playbackButton = new() { AutoSize = true, Text = "Play", Enabled = false };
    readonly CheckBox playbackSound = new() { AutoSize = true, Text = "Sound", Checked = true, Enabled = false };
    readonly ImageList thumbnails = new() { ImageSize = new Size(96, 54), ColorDepth = ColorDepth.Depth24Bit };
    readonly Dictionary<string, Bitmap> thumbnailImages = [];
    readonly SemaphoreSlim thumbnailWorker = new(1);
    CancellationTokenSource? thumbnailStop;
    BrowserEntry? selectedRecording;
    RecordingPlayback? playback;
    ClipLease? playbackLease, exportLease;
    double pausedAt;
    long lastLeaseRenewal;
    bool scrubbing, refreshingBrowser;
    int browserPage;
    const int BrowserPageSize = 100;
    IEnumerable<string> RecordingRoots() => new[] { preferences.Folder }.Concat(
        CameraRegistry.ExistingRecordingFolders(preferences.Folder));

    void BuildRecordings()
    {
        var page = Page("Recordings"); var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, AccessibleName = "Recording browser" };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, AccessibleName = "Recording filters" };
        filters.Controls.Add(new Label { Text = "From", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }); filters.Controls.Add(recordedFrom);
        filters.Controls.Add(new Label { Text = "Until", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }); filters.Controls.Add(recordedUntil);
        filterCamera.Items.Add("All cameras"); filterCamera.SelectedIndex = 0;
        filterKind.Items.AddRange(["All types", "Continuous", "Low-rate", "Timelapse", "Motion"]); filterKind.SelectedIndex = 0;
        filterProtection.Items.AddRange(["All recordings", "Protected", "Unprotected"]); filterProtection.SelectedIndex = 0;
        filterCamera.AccessibleName = "Filter camera"; filterKind.AccessibleName = "Recording type"; filterProtection.AccessibleName = "Protection filter";
        filters.Controls.Add(filterCamera); filters.Controls.Add(filterKind); filters.Controls.Add(filterProtection);
        filters.Controls.Add(Button("Apply filters", () => { browserPage = 0; RefreshClips(); }));
        filters.Controls.Add(Button("Clear filters", () => { recordedFrom.Checked = recordedUntil.Checked = false; filterCamera.SelectedIndex = filterKind.SelectedIndex = filterProtection.SelectedIndex = 0; browserPage = 0; RefreshClips(); }));
        layout.Controls.Add(filters, 0, 0);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(1000, 530), SplitterDistance = 220, Panel1MinSize = 100, Panel2MinSize = 180 };
        foreach (var (text, width) in new[] { ("Recorded", 265), ("Camera", 115), ("Playback", 90), ("Captured", 90), ("Resolution", 105), ("Profile / type", 210), ("Size", 90), ("Status", 150) }) clips.Columns.Add(text, width);
        clips.SmallImageList = thumbnails; split.Panel1.Controls.Add(clips);
        clips.SelectedIndexChanged += (_, _) => { if (!refreshingBrowser) SelectRecording(); };
        clips.DoubleClick += (_, _) => _ = Guard(TogglePlayback);
        clips.ViewportChanged += () => _ = LoadVisibleThumbnails();
        var player = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        player.ColumnStyles.Add(new(SizeType.Percent, 100)); player.ColumnStyles.Add(new(SizeType.Absolute, 300));
        player.RowStyles.Add(new(SizeType.Percent, 100)); player.RowStyles.Add(new(SizeType.Absolute, 36)); player.RowStyles.Add(new(SizeType.AutoSize));
        player.Controls.Add(playbackPicture, 0, 0); player.Controls.Add(playbackDetails, 1, 0); player.Controls.Add(playbackSeek, 0, 1); player.SetColumnSpan(playbackSeek, 2);
        var transport = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        transport.Controls.Add(playbackButton); playbackButton.Click += (_, _) => _ = Guard(TogglePlayback);
        transport.Controls.Add(Button("Stop", () => { StopPlayback(); pausedAt = 0; UpdatePlaybackTime(); }));
        transport.Controls.Add(playbackSound); playbackSound.Click += (_, _) => _ = Guard(async () => { if (playback is not null) { PausePlayback(); await StartPlayback(); } });
        transport.Controls.Add(playbackTime); player.Controls.Add(transport, 0, 2); player.SetColumnSpan(transport, 2);
        playbackSeek.MouseDown += (_, _) => scrubbing = true;
        playbackSeek.MouseUp += (_, _) => { scrubbing = false; _ = Guard(SeekPlayback); };
        playbackSeek.KeyUp += (_, _) => _ = Guard(SeekPlayback);
        split.Panel2.Controls.Add(player); layout.Controls.Add(split, 0, 1);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(Button("Refresh", RefreshClips));
        buttons.Controls.Add(Button("Previous page", () => { browserPage = Math.Max(0, browserPage - 1); RefreshClips(); }));
        buttons.Controls.Add(Button("Next page", () => { browserPage++; RefreshClips(); }));
        buttons.Controls.Add(browserCount);
        buttons.Controls.Add(Button("Protect / unprotect", () => ClipAction((library, clip) => library.Protect(clip.Name, !clip.Protected))));
        buttons.Controls.Add(Button("Export original…", () => _ = Export(false)));
        buttons.Controls.Add(Button("Export 4K (upscaled)…", () => _ = Export(true)));
        buttons.Controls.Add(Button("Delete…", () => ClipAction((library, clip) =>
        {
            if (clip.Protected || !clip.Complete) throw new InvalidOperationException("Only finished, unprotected recordings can be deleted.");
            if (MessageBox.Show(this, "Permanently delete this recording?\n" + clip.Name, "Delete recording", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            { StopPlayback(); library.Delete(clip.Name); }
        })));
        buttons.Controls.Add(Button("Open folder", () => _ = Guard(() =>
        {
            string root = selectedRecording?.Root ?? preferences.Folder; Directory.CreateDirectory(root);
            Process.Start(new ProcessStartInfo(root) { UseShellExecute = true }); return Task.CompletedTask;
        })));
        layout.Controls.Add(buttons, 0, 2); page.Controls.Add(layout);
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedIndex == 1) RefreshClips();
            else if (playback is not null) PausePlayback();
        };
    }
    static string EntryKey(BrowserEntry entry) => Path.Combine(entry.Root, entry.Clip.Name);
    static string Duration(double value) => TimeSpan.FromSeconds(Math.Max(0, value)).ToString(@"hh\:mm\:ss");
    void RefreshClips()
    {
        if (refreshingBrowser) return; refreshingBrowser = true;
        try
        {
            string? selectedKey = selectedRecording is { } current ? EntryKey(current) : null;
            var entries = RecordingRoots().Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(root =>
            { using var library = new RecordingLibrary(root); return library.Clips().Select(clip => new BrowserEntry(library.Root, clip)).ToArray(); }).OrderByDescending(entry => entry.Clip.Started).ToArray();
            string? camera = filterCamera.SelectedIndex > 0 ? filterCamera.Text : null;
            filterCamera.Items.Clear(); filterCamera.Items.Add("All cameras");
            foreach (var name in entries.Select(entry => entry.Clip.Metadata?.Camera ?? "").Distinct().Order()) filterCamera.Items.Add(name.Length > 0 ? name : "(Unspecified / legacy)");
            filterCamera.SelectedIndex = camera is not null && filterCamera.Items.Contains(camera) ? filterCamera.Items.IndexOf(camera) : 0;
            double? from = recordedFrom.Checked ? new DateTimeOffset(recordedFrom.Value).ToUnixTimeMilliseconds() / 1000.0 : null;
            double? until = recordedUntil.Checked ? new DateTimeOffset(recordedUntil.Value).ToUnixTimeMilliseconds() / 1000.0 : null;
            if (from > until) throw new ArgumentException("The filter end must be after its start.");
            var filter = new ClipFilter(from, until, filterCamera.SelectedIndex == 0 ? null : filterCamera.Text == "(Unspecified / legacy)" ? "" : filterCamera.Text,
                filterKind.SelectedIndex == 0 ? null : filterKind.Text, filterProtection.SelectedIndex == 0 ? null : filterProtection.SelectedIndex == 1);
            var matching = entries.Where(entry => filter.Matches(entry.Clip)).ToArray();
            browserPage = Math.Clamp(browserPage, 0, Math.Max(0, (matching.Length - 1) / BrowserPageSize));
            thumbnailStop?.Cancel(); thumbnailStop?.Dispose(); thumbnailStop = new();
            clips.BeginUpdate(); clips.Items.Clear(); thumbnails.Images.Clear();
            foreach (var bitmap in thumbnailImages.Values) bitmap.Dispose(); thumbnailImages.Clear();
            foreach (var entry in matching.Skip(browserPage * BrowserPageSize).Take(BrowserPageSize))
            {
                var clip = entry.Clip; var metadata = clip.Metadata;
                var item = new ListViewItem([DateTimeOffset.FromUnixTimeMilliseconds((long)(clip.Started * 1000)).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    string.IsNullOrEmpty(metadata?.Camera) ? "Unspecified" : metadata.Camera, Duration(clip.Duration), Duration(metadata is { CaptureDuration: > 0 } ? metadata.CaptureDuration : clip.Duration),
                    $"{clip.Width} × {clip.Height}", $"{metadata?.Profile ?? "Original stream"} · {metadata?.Kind ?? "Continuous"}", $"{clip.Bytes / 1048576.0:0.0} MiB",
                    !clip.Exists ? "Missing" : !clip.Complete ? "Active / interrupted" : clip.Protected ? "Protected" : clip.InUse ? "In use" : "Saved"])
                { Tag = entry, Selected = EntryKey(entry) == selectedKey, ToolTipText = clip.Name };
                clips.Items.Add(item);
            }
            browserCount.Text = $"{matching.Length} matching · page {browserPage + 1}/{Math.Max(1, (matching.Length + BrowserPageSize - 1) / BrowserPageSize)}";
            if (clips.SelectedItems.Count == 0 && selectedRecording is not null) { StopPlayback(); selectedRecording = null; playbackButton.Enabled = playbackSeek.Enabled = false; playbackDetails.Text = "Select a recording."; }
        }
        catch (Exception e) { status.Text = e.Message; }
        finally { clips.EndUpdate(); refreshingBrowser = false; }
        _ = LoadVisibleThumbnails();
    }
    async Task LoadVisibleThumbnails()
    {
        if (tabs.SelectedIndex != 1 || thumbnailStop is null || !File.Exists(preferences.Ffmpeg)) return;
        var cancellation = thumbnailStop.Token;
        if (!await thumbnailWorker.WaitAsync(0)) return;
        try
        {
            var visible = clips.Items.Cast<ListViewItem>().Where(item => item.Bounds.IntersectsWith(clips.ClientRectangle)).ToArray();
            foreach (var item in visible)
            {
                if (cancellation.IsCancellationRequested) break;
                var entry = (BrowserEntry)item.Tag!; string key = EntryKey(entry);
                if (!entry.Clip.Exists || !entry.Clip.Complete || thumbnails.Images.ContainsKey(key)) continue;
                try
                {
                    using var library = new RecordingLibrary(entry.Root); using var lease = library.Hold(entry.Clip.Name);
                    var bitmap = await MediaTools.Thumbnail(preferences.Ffmpeg!, library.ClipPath(entry.Clip.Name), Math.Min(.5, entry.Clip.Duration / 2), cancellation);
                    if (cancellation.IsCancellationRequested) { bitmap.Dispose(); break; }
                    thumbnailImages[key] = bitmap; thumbnails.Images.Add(key, bitmap); item.ImageKey = key;
                    if (selectedRecording is { } selected && EntryKey(selected) == key && playback is null && pausedAt == 0) SetPlaybackPicture(new Bitmap(bitmap));
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ArgumentException) { }
            }
        }
        finally
        {
            thumbnailWorker.Release();
            if (cancellation.IsCancellationRequested && !closing) Ui(() => _ = LoadVisibleThumbnails());
        }
    }
    void SelectRecording()
    {
        if (clips.SelectedItems.Count == 0) return;
        var entry = (BrowserEntry)clips.SelectedItems[0].Tag!;
        if (selectedRecording is { } selected && EntryKey(selected) == EntryKey(entry)) { selectedRecording = entry; return; }
        StopPlayback(); selectedRecording = entry; pausedAt = 0;
        var clip = entry.Clip; var metadata = clip.Metadata;
        playbackButton.Enabled = playbackSeek.Enabled = clip.Complete && clip.Exists && clip.Duration > 0;
        playbackSound.Enabled = metadata?.Audio is not null;
        playbackDetails.Text = $"{(string.IsNullOrEmpty(metadata?.Camera) ? "Unspecified camera" : metadata.Camera)}\n{DateTimeOffset.FromUnixTimeMilliseconds((long)(clip.Started * 1000)).LocalDateTime:g}\n" +
            $"{clip.Width} × {clip.Height}\n{metadata?.Profile ?? "Original stream"}\n{metadata?.Kind ?? "Continuous"} · {(metadata?.TargetFps is { } fps ? $"{fps:0.##} fps" : "source fps")}\n" +
            $"{metadata?.Audio ?? "No audio"}\nPlayback {Duration(clip.Duration)}\nCaptured {Duration(metadata is { CaptureDuration: > 0 } ? metadata.CaptureDuration : clip.Duration)}\n{clip.Bytes / 1048576.0:0.0} MiB\n{clip.Name}";
        SetPlaybackPicture(thumbnailImages.TryGetValue(EntryKey(entry), out var image) ? new Bitmap(image) : null); UpdatePlaybackTime(); _ = LoadVisibleThumbnails();
    }
    void SetPlaybackPicture(Bitmap? bitmap) { var old = playbackPicture.Image; playbackPicture.Image = bitmap; old?.Dispose(); }
    async Task TogglePlayback() { if (playback is null) await StartPlayback(); else PausePlayback(); }
    Task SeekPlayback()
    {
        if (selectedRecording is null) return Task.CompletedTask;
        double target = selectedRecording.Clip.Duration * playbackSeek.Value / 10000.0;
        StopPlayback(); pausedAt = Math.Clamp(target, 0, Math.Max(0, selectedRecording.Clip.Duration - .08)); return StartPlayback();
    }
    async Task StartPlayback()
    {
        if (selectedRecording is not { } entry || !entry.Clip.Complete || !entry.Clip.Exists) return;
        if (!File.Exists(preferences.Ffmpeg)) throw new IOException("Choose FFmpeg in Storage for the recording player.");
        if (pausedAt >= entry.Clip.Duration - .05) pausedAt = 0;
        if (listen.Checked) { listen.Checked = false; ClearAudio(); if (session is { } active) await active.SetMonitoringAsync(false); }
        using var library = new RecordingLibrary(entry.Root); playbackLease = library.Hold(entry.Clip.Name);
        try { playback = new(preferences.Ffmpeg!, library.ClipPath(entry.Clip.Name), pausedAt, entry.Clip.Duration, playbackSound.Checked && entry.Clip.Metadata?.Audio is not null); }
        catch { playbackLease.Dispose(); playbackLease = null; throw; }
        lastLeaseRenewal = Environment.TickCount64; playbackButton.Text = "Pause";
    }
    void PausePlayback() { if (playback is not null) pausedAt = playback.Position; StopPlayback(); UpdatePlaybackTime(); }
    void StopPlayback()
    {
        playback?.Dispose(); playback = null;
        var lease = playbackLease; playbackLease = null;
        try { lease?.Dispose(); } catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException) { status.Text = "Playback closed; its catalogue reservation will expire: " + e.Message; }
        playbackButton.Text = "Play";
    }
    void UpdatePlaybackTime()
    {
        double position = playback?.Position ?? pausedAt, total = selectedRecording?.Clip.Duration ?? 0;
        playbackTime.Text = $"{Duration(position)} / {Duration(total)}";
        if (!scrubbing) playbackSeek.Value = total > 0 ? Math.Clamp((int)(position / total * 10000), 0, 10000) : 0;
    }
    void UpdatePlayback()
    {
        if (playback is { } playing)
        {
            if (playing.Take() is { } bitmap) SetPlaybackPicture(bitmap);
            UpdatePlaybackTime();
            if (playing.Finished) { pausedAt = playing.Position; if (playing.Error is { } error) status.Text = "Playback: " + error; StopPlayback(); }
        }
        if (Environment.TickCount64 - lastLeaseRenewal > 30000)
        {
            try { playbackLease?.Renew(); exportLease?.Renew(); }
            catch (Exception e) { status.Text = "Recording reservation: " + e.Message; StopPlayback(); }
            lastLeaseRenewal = Environment.TickCount64;
        }
    }
    void ClipAction(Action<RecordingLibrary, Clip> action) => _ = Guard(() =>
    {
        if (exporting) throw new InvalidOperationException("Wait for the export to finish before changing clips.");
        if (clips.SelectedItems.Count == 0) return Task.CompletedTask;
        var entry = (BrowserEntry)clips.SelectedItems[0].Tag!;
        using var library = new RecordingLibrary(entry.Root); action(library, entry.Clip); RefreshClips(); return Task.CompletedTask;
    });
    async Task Export(bool upscale) => await Guard(async () =>
    {
        if (exporting || clips.SelectedItems.Count == 0) return;
        var entry = (BrowserEntry)clips.SelectedItems[0].Tag!; var clip = entry.Clip;
        if (!clip.Complete || !clip.Exists) throw new IOException("Choose a completed recording.");
        if (upscale && !File.Exists(preferences.Ffmpeg)) throw new IOException("Choose an FFmpeg executable in Storage first.");
        using var dialog = new SaveFileDialog { Filter = "MP4 video|*.mp4", FileName = (upscale ? "4K_upscaled_" : "Export_") + clip.Name };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (File.Exists(dialog.FileName)) throw new IOException("Choose a new filename; exports do not overwrite existing files.");
        using var library = new RecordingLibrary(entry.Root); exportLease = library.Hold(clip.Name); exporting = true;
        try
        {
            status.Text = upscale ? "Exporting 4K upscale…" : "Exporting original…";
            if (upscale) await MediaTools.Export4K(preferences.Ffmpeg!, library.ClipPath(clip.Name), dialog.FileName, exportStop.Token);
            else await Task.Run(() => File.Copy(library.ClipPath(clip.Name), dialog.FileName, false));
            status.Text = "Export saved: " + dialog.FileName;
        }
        finally { exportLease.Dispose(); exportLease = null; exporting = false; RefreshClips(); }
    });
}

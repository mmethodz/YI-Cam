using YiLocal.Core;

namespace YiLocal.Windows;

public sealed partial class MainForm
{
    void BuildProvisioning()
    {
        var body = Column(); Page("Setup QR · experimental").Controls.Add(body);
        body.Controls.Add(Label("Generate a QR image locally, save it as PNG, and display it on your phone for the camera to scan."));
        body.Controls.Add(Label("The format is reverse engineered. Fresh setup still needs a vendor-issued binding token; account-free provisioning and Wi-Fi change have not been physically verified. This tool does not reset or reconfigure your connected camera."));
        var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 470 };
        mode.Items.AddRange(["Fresh setup — existing binding token required", "Change Wi-Fi — experimental t=1 format"]); mode.SelectedIndex = 0;
        var ssid = new TextBox { Width = 420 };
        var password = new TextBox { Width = 420, UseSystemPasswordChar = true };
        var token = new TextBox { Width = 420, UseSystemPasswordChar = true };
        var deviceId = new TextBox { Width = 420, Enabled = false };
        body.Controls.Add(Label("QR format")); body.Controls.Add(mode);
        body.Controls.Add(Label("Wi-Fi name (SSID)")); body.Controls.Add(ssid);
        body.Controls.Add(Label("Wi-Fi password")); body.Controls.Add(password);
        body.Controls.Add(Label("Existing binding token · distinct from the device pairing key")); body.Controls.Add(token);
        body.Controls.Add(Label("Device ID shown in the vendor app · required for Wi-Fi change")); body.Controls.Add(deviceId);
        mode.SelectedIndexChanged += (_, _) => { token.Enabled = mode.SelectedIndex == 0; deviceId.Enabled = mode.SelectedIndex == 1; };
        body.Controls.Add(Label("The saved PNG contains recoverable Wi-Fi credentials. These fields are not saved in app preferences or logs."));
        body.Controls.Add(Button("Save QR image…", () => _ = Guard(() =>
        {
            byte[] png = QrProvisioning.Png(QrProvisioning.Compose(ssid.Text, password.Text, token.Text, mode.SelectedIndex == 1, deviceId.Text));
            using var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "OpenYI-setup.png", OverwritePrompt = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) { File.WriteAllBytes(dialog.FileName, png); status.Text = "QR image saved. Camera acceptance remains experimental."; }
            return Task.CompletedTask;
        })));
        body.Controls.Add(Button("Clear credentials", () => { ssid.Clear(); password.Clear(); token.Clear(); deviceId.Clear(); }));
    }
}

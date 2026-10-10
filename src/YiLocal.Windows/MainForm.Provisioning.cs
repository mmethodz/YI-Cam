using YiLocal.Core;

namespace YiLocal.Windows;

public sealed partial class MainForm
{
    void BuildProvisioning()
    {
        var body = Column(); Page(L.Get("SetupQRExperimental")).Controls.Add(new ScrollableColumn(body));
        body.Controls.Add(Label(L.Get("GenerateAQRImageLocallySaveItAsPNGAndDisplay")));
        body.Controls.Add(Label(L.Get("TheFormatIsReverseEngineeredFreshSetupStillNeedsAVendor")));
        var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 470 };
        mode.Items.AddRange([L.Get("FreshSetupExistingBindingTokenRequired"), L.Get("ChangeWiFiExperimentalT1Format")]); mode.SelectedIndex = 0;
        var ssid = new TextBox { Width = 420 };
        var password = new TextBox { Width = 420, UseSystemPasswordChar = true };
        var token = new TextBox { Width = 420, UseSystemPasswordChar = true };
        var deviceId = new TextBox { Width = 420, Enabled = false };
        body.Controls.Add(Label(L.Get("QRFormat"))); body.Controls.Add(mode);
        body.Controls.Add(Label(L.Get("WiFiNameSSID"))); body.Controls.Add(ssid);
        body.Controls.Add(Label(L.Get("WiFiPassword"))); body.Controls.Add(password);
        body.Controls.Add(Label(L.Get("ExistingBindingTokenDistinctFromTheDevicePairingKey"))); body.Controls.Add(token);
        body.Controls.Add(Label(L.Get("DeviceIDShownInTheVendorAppRequiredForWiFi"))); body.Controls.Add(deviceId);
        mode.SelectedIndexChanged += (_, _) => { token.Enabled = mode.SelectedIndex == 0; deviceId.Enabled = mode.SelectedIndex == 1; };
        body.Controls.Add(Label(L.Get("TheSavedPNGContainsRecoverableWiFiCredentialsTheseFieldsAre")));
        body.Controls.Add(Button(L.Get("SaveQRImage"), () => _ = Guard(() =>
        {
            byte[] png = QrProvisioning.Png(QrProvisioning.Compose(ssid.Text, password.Text, token.Text, mode.SelectedIndex == 1, deviceId.Text));
            using var dialog = new SaveFileDialog { Filter = L.Get("PNGImagePng"), FileName = "OpenYI-setup.png", OverwritePrompt = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) { File.WriteAllBytes(dialog.FileName, png); status.Text = L.Get("QRImageSavedCameraAcceptanceRemainsExperimental"); }
            return Task.CompletedTask;
        })));
        body.Controls.Add(Button(L.Get("ClearCredentials"), () => { ssid.Clear(); password.Clear(); token.Clear(); deviceId.Clear(); }));
    }
}

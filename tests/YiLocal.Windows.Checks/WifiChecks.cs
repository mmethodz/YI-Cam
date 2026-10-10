using System.Security;
using System.Text;
using YiLocal.Core;
using YiLocal.Windows;
using L = YiLocal.Core.Localization.Texts;

static class WifiChecks
{
    static void Require(bool test, string message) { if (!test) throw new Exception(message); }
    static string Profile(string encryption, string protection, string password) =>
        $"<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><MSM><security><authEncryption><encryption>{encryption}</encryption></authEncryption><sharedKey><protected>{protection}</protected><keyMaterial>{SecurityElement.Escape(password)}</keyMaterial></sharedKey></security></MSM></WLANProfile>";
    static string Saved(string ssid, string? hex, string protection = "false") => Profile("AES", protection, "fixture password")
        .Replace("<MSM>", $"<name>Renamed Windows profile</name><SSIDConfig><SSID>{(hex is null ? "" : $"<hex>{hex}</hex>")}<name>{SecurityElement.Escape(ssid)}</name></SSID></SSIDConfig><MSM>");
    public static void Run()
    {
        Require(WindowsWifi.ProfilePassword(Profile("AES", "false", "  päss<& word  ")) == "  päss<& word  ", "Wi-Fi password characters or whitespace changed.");
        Require(WindowsWifi.ProfilePassword(Profile("AES", "true", "encrypted")) is null, "Encrypted key was exposed as a password.");
        Require(WindowsWifi.ProfilePassword(Profile("none", "true", "")) == "", "Open network not distinguished from an unreadable password.");
        Require(WindowsWifi.ProfilePassword("<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><MSM><security/></MSM></WLANProfile>") is null, "Absent key treated as open network.");
        try { WindowsWifi.ProfilePassword("<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///c:/unknown'>]><x>&x;</x>"); throw new Exception("External XML entity accepted."); }
        catch (System.Xml.XmlException) { }
        string unicodeSsid = "  Mökki & koti  ";
        var saved = WindowsWifi.ProfileNetworks(Saved("Lossy display name", Convert.ToHexString(Encoding.UTF8.GetBytes(unicodeSsid))));
        Require(saved.Count == 1 && saved[0].Ssid == unicodeSsid && saved[0].Password == "fixture password", "Saved SSID bytes or credentials changed; profile name must not be used as SSID.");
        Require(WindowsWifi.ProfileNetworks(Saved("Name only", null)).Single().Ssid == "Name only", "Name-only SSID profile not read.");
        Require(WindowsWifi.ProfileNetworks(Saved("Private", null, "true")).Single().Password is null, "Saved protected key exposed.");
        foreach (var hex in new[] { "FF", "0", "GG", "00", "", new string('A', 66) })
            Require(WindowsWifi.ProfileNetworks(Saved("Misleading fallback", hex)).Count == 0, "Invalid SSID bytes silently replaced by display name.");
        Require(WindowsWifi.ProfileNetworks(Profile("AES", "false", "fixture")).Count == 0, "Missing SSID accepted.");

        WindowsWifiNetwork first = new(new("House", "one"), false), second = new(new("Cabin", "two"), false), connected = new(new("Office", "three"), true);
        Require(WindowsWifi.DefaultNetwork([first], "") == 0, "Single saved network should fill on Ethernet.");
        Require(WindowsWifi.DefaultNetwork([first, second], "") == -1, "Ambiguous saved network silently chosen.");
        Require(WindowsWifi.DefaultNetwork([first, second], "Cabin") == 1, "Existing SSID selection lost.");
        Require(WindowsWifi.DefaultNetwork([first, second], "cabin") == -1, "SSID selection must be case-sensitive.");
        Require(WindowsWifi.DefaultNetwork([first, connected], "") == 1, "Current connection not preferred.");
        Require(WindowsWifi.DefaultNetwork([], "") == -1, "Empty network list selected.");
        Require(!first.ToString().Contains("one"), "Network choice exposes password.");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckPanel(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Wi-Fi setup form regression.", failure);
        Console.WriteLine("Windows Wi-Fi: saved SSIDs, protected/missing/open passwords, ambiguous selection, form selection/editing, Unicode and XML safety passed.");
    }

    static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    static void CheckPanel()
    {
        foreach (string language in new[] { "en", "fi" })
        {
            YiLocal.Core.Localization.Texts.Initialize(language);
            using var panel = new LocalSetupPanel(_ => Task.CompletedTask, () => null);
            var controls = Descendants(panel).ToArray();
            var ssid = controls.OfType<ComboBox>().Single(c => c.AccessibleName == L.Get("WiFiNameSSID"));
            var password = controls.OfType<TextBox>().Single(c => c.AccessibleName == L.Get("WiFiPassword"));
            var open = controls.OfType<CheckBox>().Single(c => c.Text == L.Get("Setup.OpenNetwork"));
            WindowsWifiNetwork first = new(new("Home fixture", "first fixture password"), false), second = new(new("Cabin fixture", "second fixture password"), false);
            panel.ApplyWifiNetworks([first, second]);
            Require(ssid.SelectedIndex == -1 && password.Text == "", "Form guessed an ambiguous saved network.");
            ssid.SelectedIndex = 1;
            Require(ssid.Text == second.Network.Ssid && password.Text == second.Network.Password && !open.Checked, "Selecting a saved SSID failed to load its password.");
            ssid.SelectedIndex = 0;
            Require(password.Text == first.Network.Password, "Switching networks left the previous password.");
            ssid.Text = "Manual fixture";
            Require(password.Text == "" && !open.Checked, "Manual SSID edit kept another network's password.");
            panel.ApplyWifiNetworks([new(new("Open fixture", ""), false)]);
            Require(open.Checked && password.Text == "", "Known open network not applied.");
            panel.ApplyWifiNetworks([new(new("Restricted fixture", null), false)]);
            Require(!open.Checked && password.Text == "", "Unavailable password mistaken for open network.");
            Require(controls.OfType<Label>().Any(c => c.Text == L.Get("Setup.PcWifiPasswordUnavailable")), "Missing password not explained.");
        }
    }
}

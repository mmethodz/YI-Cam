using System.Security;
using YiLocal.Windows;

static class WifiChecks
{
    static void Require(bool test, string message) { if (!test) throw new Exception(message); }
    static string Profile(string encryption, string protection, string password) =>
        $"<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><MSM><security><authEncryption><encryption>{encryption}</encryption></authEncryption><sharedKey><protected>{protection}</protected><keyMaterial>{SecurityElement.Escape(password)}</keyMaterial></sharedKey></security></MSM></WLANProfile>";
    public static void Run()
    {
        Require(WindowsWifi.ProfilePassword(Profile("AES", "false", "  päss<& word  ")) == "  päss<& word  ", "Wi-Fi password characters or whitespace changed.");
        Require(WindowsWifi.ProfilePassword(Profile("AES", "true", "encrypted")) is null, "Encrypted key was exposed as a password.");
        Require(WindowsWifi.ProfilePassword(Profile("none", "true", "")) == "", "Open network not distinguished from an unreadable password.");
        Require(WindowsWifi.ProfilePassword("<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><MSM><security/></MSM></WLANProfile>") is null, "Absent key treated as open network.");
        try { WindowsWifi.ProfilePassword("<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///c:/unknown'>]><x>&x;</x>"); throw new Exception("External XML entity accepted."); }
        catch (System.Xml.XmlException) { }
        Console.WriteLine("Windows Wi-Fi profile parsing: protected/missing/open networks, character preservation and external-entity rejection passed.");
    }
}

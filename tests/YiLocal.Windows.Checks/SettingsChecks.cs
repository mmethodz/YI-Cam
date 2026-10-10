using System.Text.Json.Nodes;
using YiLocal.Core;
using YiLocal.Windows;

static class SettingsChecks
{
    static void Require(bool test, string message) { if (!test) throw new Exception(message); }
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "openyi-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "settings.json");
        try
        {
            var saved = new Preferences
            {
                Folder = Path.Combine(root, "recordings"), Ffmpeg = "ffmpeg-fixture.exe",
                Storage = new(27, 3, 15, true, 12),
                Recording = new(RecordingEncoding.Balanced, 5, CaptureMode.Motion, true, 17, 3),
                ExperimentalMultipleCameras = true, Microphone = "Fixture microphone",
                Alarm = new(true, 15, null, 12, 120, "fixture.aac", "My sound.mp3", AlarmOutput.Both, "Fixture speakers")
            };
            saved.Save(path);
            var loaded = Preferences.Load(out string? notice, path);
            Require(notice is null && loaded.Storage == saved.Storage && loaded.Recording == saved.Recording && loaded.Folder == saved.Folder &&
                loaded.Microphone == saved.Microphone && loaded.ExperimentalMultipleCameras && loaded.Alarm == saved.Alarm, "Settings did not round-trip.");
            saved.Save(path); // Retain a complete prior generation.
            File.WriteAllText(path, "{truncated");
            loaded = Preferences.Load(out notice, path);
            Require(notice?.Contains("backup") == true && loaded.Recording == saved.Recording && loaded.Folder == saved.Folder, "Backup recovery lost settings.");
            loaded.Save(path);
            Require(Directory.GetFiles(root, "*.invalid-*").Length == 1, "Damaged settings were silently overwritten.");
            var document = JsonNode.Parse(File.ReadAllText(path))!;
            document["Alarm"] = new JsonObject { ["ThresholdPercent"] = -4 };
            File.WriteAllText(path, document.ToJsonString());
            loaded = Preferences.Load(out notice, path);
            Require(notice?.Contains("Alarm") == true && !loaded.Alarm.Enabled && loaded.Recording == saved.Recording,
                "An invalid alarm setting enabled the alarm or discarded recording choices.");
            document["Recording"] = new JsonObject { ["Encoding"] = 999 };
            File.WriteAllText(path, document.ToJsonString());
            loaded = Preferences.Load(out notice, path);
            Require(notice?.Contains("Recording") == true && loaded.Storage == saved.Storage && loaded.Folder == saved.Folder,
                "An invalid capture profile discarded unrelated storage settings.");
            document["Storage"] = null;
            File.WriteAllText(path, document.ToJsonString());
            loaded = Preferences.Load(out notice, path);
            Require(loaded.Storage == new StoragePolicy() && loaded.Folder == saved.Folder && notice is not null, "Null section recovery failed.");
            File.WriteAllText(path, "{\"Recording\":{\"Encoding\":1,\"FramesPerSecond\":5,\"Mode\":0,\"IncludeAudio\":true}}");
            loaded = Preferences.Load(out notice, path);
            Require(notice is null && loaded.Recording.PostMotionSeconds == 30 && loaded.Recording.MotionThresholdPercent == 2 &&
                !loaded.Alarm.Enabled && loaded.Alarm.Output == AlarmOutput.Camera,
                "Older settings lost new optional defaults.");
            Console.WriteLine("Settings persistence, backup recovery, section isolation and legacy defaults passed.");
        }
        finally { Directory.Delete(root, true); }
    }
}

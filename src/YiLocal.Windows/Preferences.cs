using System.Text.Json;
using YiLocal.Core;

namespace YiLocal.Windows;

internal sealed class Preferences
{
    public string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "YI Local");
    public string? Ffmpeg { get; set; }
    public StoragePolicy Storage { get; set; } = new();
    public RecordingOptions Recording { get; set; } = new();
    public AlarmOptions Alarm { get; set; } = new();
    public bool ExperimentalMultipleCameras { get; set; }
    public string? Microphone { get; set; }
    public string Language { get; set; } = "en";
    public static string FilePath => Path.Combine(DeviceProfile.SettingsDirectory, "settings.json");
    public Preferences Copy() => (Preferences)MemberwiseClone();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Preferences Load(out string? notice, string? path = null)
    {
        path ??= FilePath;
        notice = null;
        if (!File.Exists(path)) return new();
        try { return Read(File.ReadAllText(path), out notice); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            notice = L.Get("SettingsCouldNotBeRead") + e.Message;
            try
            {
                if (File.Exists(path + ".bak"))
                {
                    var recovered = Read(File.ReadAllText(path + ".bak"), out string? backupNotice);
                    notice = L.Get("RecoveredThePreviousSettingsBackup") + notice + " " + backupNotice;
                    return recovered;
                }
            }
            catch (Exception backup) when (backup is IOException or UnauthorizedAccessException or JsonException) { }
            return new();
        }
    }
    static Preferences Read(string json, out string? notice)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException(L.Get("ExpectedASettingsObject"));
        var result = new Preferences(); var issues = new List<string>();
        // A damaged section must not discard unrelated storage/capture choices.
        void ReadSection<T>(string name, Action<T> apply)
        {
            if (!document.RootElement.TryGetProperty(name, out var element)) return;
            try { apply(element.Deserialize<T>()!); }
            catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException or NullReferenceException)
            { issues.Add(name + ": " + e.Message); }
        }
        ReadSection<string>(nameof(Folder), value => { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(L.Get("EmptyRecordingFolder")); result.Folder = Path.GetFullPath(value); });
        ReadSection<string?>(nameof(Ffmpeg), value => result.Ffmpeg = value);
        ReadSection<StoragePolicy>(nameof(Storage), value => { value.Validate(); result.Storage = value; });
        ReadSection<RecordingOptions>(nameof(Recording), value => { value.Validate(); result.Recording = value; });
        ReadSection<AlarmOptions>(nameof(Alarm), value => { value.Validate(); result.Alarm = value; });
        ReadSection<bool>(nameof(ExperimentalMultipleCameras), value => result.ExperimentalMultipleCameras = value);
        ReadSection<string?>(nameof(Microphone), value => result.Microphone = value);
        ReadSection<string?>(nameof(Language), value => result.Language = L.ResolveLanguage(value));
        notice = issues.Count == 0 ? null : L.Get("SomeSavedSettingsNeedAttention") + string.Join("; ", issues);
        return result;
    }
    public void Save(string? path = null)
    {
        path ??= FilePath;
        Storage.Validate(); Recording.Validate(); Alarm.Validate();
        if (string.IsNullOrWhiteSpace(Folder)) throw new ArgumentException(L.Get("ChooseARecordingFolder"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
            if (File.Exists(path))
            {
                bool valid;
                try { Read(File.ReadAllText(path), out string? issue); valid = issue is null; }
                catch (JsonException) { valid = false; }
                if (valid) File.Replace(temporary, path, path + ".bak");
                else
                {
                    File.Copy(path, path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
                    File.Move(temporary, path, true);
                }
            }
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

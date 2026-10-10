using System.Text.Json;

namespace YiLocal.Core;

public sealed record CameraRegistration(string Id, bool Enabled = true);

/// <summary>Additional experimental cameras. The primary profile and its recording root stay unchanged.</summary>
public sealed class CameraRegistry
{
    readonly string directory;
    string IndexPath => Path.Combine(directory, "cameras.json");
    public IReadOnlyList<CameraRegistration> Entries { get; private set; } = [];
    public CameraRegistry(string? directory = null)
    {
        this.directory = Path.GetFullPath(directory ?? DeviceProfile.SettingsDirectory);
        if (File.Exists(IndexPath))
        {
            var entries = JsonSerializer.Deserialize<List<CameraRegistration>>(File.ReadAllText(IndexPath)) ?? [];
            if (entries.Count > 7 || entries.Select(entry => entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
                throw new InvalidDataException(L.Get("CameraRegistryMustContainAtMostSevenDistinctAdditionalCameras"));
            foreach (var entry in entries) ValidateId(entry.Id);
            Entries = entries;
        }
    }
    static string ValidateId(string id) => Guid.TryParseExact(id, "N", out var parsed) ? parsed.ToString("N") : throw new ArgumentException(L.Get("InvalidCameraRegistrationID"));
    public string ProfilePath(string id) => Path.Combine(directory, "camera-profiles", ValidateId(id) + ".dpapi");
    public static string RecordingFolder(string root, string id) => Path.Combine(Path.GetFullPath(root), "Cameras", ValidateId(id));
    public static IEnumerable<string> ExistingRecordingFolders(string root)
    {
        string parent = Path.Combine(Path.GetFullPath(root), "Cameras");
        if (!Directory.Exists(parent) || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) yield break;
        foreach (string folder in Directory.EnumerateDirectories(parent))
            if (Guid.TryParseExact(Path.GetFileName(folder), "N", out _) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0 && File.Exists(Path.Combine(folder, ".yi-library.sqlite")))
                yield return folder;
    }
    public DeviceProfile LoadProfile(string id) => DeviceProfile.Load(ProfilePath(id));
    public void EnsureDistinct(DeviceProfile profile, DeviceProfile? primary = null, string? exceptId = null)
    {
        static bool Same(DeviceProfile left, DeviceProfile right) =>
            string.Equals(left.Ip, right.Ip, StringComparison.OrdinalIgnoreCase) ||
            left.Uid is not null && string.Equals(left.Uid, right.Uid, StringComparison.OrdinalIgnoreCase);
        if (primary is not null && Same(profile, primary)) throw new InvalidOperationException(L.Get("ThisCameraIsAlreadyThePrimaryCamera"));
        foreach (var entry in Entries.Where(entry => !string.Equals(entry.Id, exceptId, StringComparison.OrdinalIgnoreCase)))
            if (Same(profile, LoadProfile(entry.Id))) throw new InvalidOperationException(L.Get("ThisCameraAlreadyHasAConfigurationEditThatEntryInstead"));
    }
    public CameraRegistration SaveVerified(DeviceProfile profile, DeviceProfile? primary = null, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(profile.Uid)) throw new InvalidOperationException(L.Get("VerifyTheCameraIdentityBeforeSavingIt"));
        EnsureDistinct(profile, primary, id);
        if (id is null && Entries.Count >= 7) throw new InvalidOperationException(L.Get("TheExperimentalGridSupportsEightCamerasIncludingThePrimary"));
        if (id is not null && !Entries.Any(entry => entry.Id == id)) throw new ArgumentException(L.Get("UnknownCameraRegistration"));
        bool adding = id is null; id ??= Guid.NewGuid().ToString("N");
        var registration = Entries.SingleOrDefault(entry => entry.Id == id) ?? new CameraRegistration(id);
        var next = Entries.Where(entry => entry.Id != id).Append(registration).ToArray();
        // All constraints are checked before replacing a device key.
        profile.Save(ProfilePath(id)); if (adding) SaveIndex(next); return registration;
    }
    public void SetEnabled(string id, bool enabled)
    {
        if (!Entries.Any(entry => entry.Id == id)) throw new ArgumentException(L.Get("UnknownCameraRegistration"));
        SaveIndex(Entries.Select(entry => entry.Id == id ? entry with { Enabled = enabled } : entry).ToArray());
    }
    public void Remove(string id)
    {
        string path = ProfilePath(id);
        if (!Entries.Any(entry => entry.Id == id)) throw new ArgumentException(L.Get("UnknownCameraRegistration"));
        SaveIndex(Entries.Where(entry => entry.Id != id).ToArray()); File.Delete(path);
    }
    void SaveIndex(IReadOnlyList<CameraRegistration> entries)
    {
        Directory.CreateDirectory(directory); string temporary = IndexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, IndexPath, true); Entries = entries; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

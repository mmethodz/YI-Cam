namespace YiLocal.Core;

public static class PairingImport
{
    /// <summary>Only replace the saved profile after the candidate key authenticates to the pinned camera.</summary>
    public static Task<DeviceProfile> VerifyAndSaveAsync(DeviceProfile candidate, CancellationToken cancellation = default) =>
        VerifyAndSaveAsync(candidate, async (profile, token) =>
        {
            using var camera = new CameraClient(profile.Ip, profile.Password, profile.Uid);
            await camera.ConnectAsync(token);
            await camera.FirmwareAsync().WaitAsync(token);
            return camera.Uid ?? throw new InvalidDataException("The camera did not return its identity.");
        }, profile => profile.Save(), cancellation);

    internal static async Task<DeviceProfile> VerifyAndSaveAsync(DeviceProfile candidate,
        Func<DeviceProfile, CancellationToken, Task<string>> verify, Action<DeviceProfile> save, CancellationToken cancellation = default)
    {
        // Work on a copy so a failure cannot mutate the UI's current profile.
        var verified = new DeviceProfile { Ip = candidate.Ip, Name = candidate.Name, Password = candidate.Password, Uid = candidate.Uid };
        cancellation.ThrowIfCancellationRequested();
        string uid = await verify(verified, cancellation);
        if (string.IsNullOrEmpty(uid) || verified.Uid is not null && !uid.Equals(verified.Uid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A different camera answered. The saved pairing was not changed.");
        verified.Uid = uid;
        cancellation.ThrowIfCancellationRequested();
        save(verified);
        return verified;
    }
}

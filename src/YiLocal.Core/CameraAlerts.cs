namespace YiLocal.Core;

/// <summary>Historical alerts are not a continuous motion-active signal.</summary>
public sealed record CameraAlert(uint Category, uint Started, uint Duration)
{
    public static IReadOnlyList<CameraAlert> Parse(byte[] payload)
    {
        if (payload.Length < 4) throw new InvalidDataException("Missing alert count.");
        uint count = Wire.U32(payload);
        if (count > 2048 || payload.Length != 4L + count * 12)
            throw new InvalidDataException("Invalid or truncated alert history.");
        var alerts = new List<CameraAlert>();
        for (int offset = 4; offset < payload.Length; offset += 12)
            alerts.Add(new(Wire.U32(payload.AsSpan(offset)), Wire.U32(payload.AsSpan(offset + 4)), Wire.U32(payload.AsSpan(offset + 8))));
        return alerts;
    }
}

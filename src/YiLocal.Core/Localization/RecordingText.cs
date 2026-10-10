using System.Text.RegularExpressions;

namespace YiLocal.Core.Localization;

/// <summary>Translate legacy catalogue values at the display boundary, never when writing/filtering data.</summary>
public static partial class RecordingText
{
    public static string Profile(string? value) => value switch
    {
        null or "Original stream" => Texts.Get("Profile.Original"),
        "H.264 balanced (CRF 28)" => Texts.Get("Profile.Balanced"),
        "H.264 smaller (CRF 32)" => Texts.Get("Profile.Small"),
        _ => value
    };
    public static string Kind(string? value) => value switch
    {
        null or "Continuous" => Texts.Get("Kind.Continuous"),
        "Low-rate" => Texts.Get("Kind.LowRate"),
        "Timelapse" => Texts.Get("Kind.Timelapse"),
        "Motion" => Texts.Get("Kind.Motion"),
        _ => value
    };
    public static string? FilterKind(int index) => index switch
    {
        1 => "Continuous", 2 => "Low-rate", 3 => "Timelapse", 4 => "Motion", _ => null
    };
    public static string Audio(string? value)
    {
        if (value is null) return Texts.Get("NoAudio");
        var match = AacDescription().Match(value);
        return match.Success ? Texts.Format("Audio.Metadata", match.Groups[1].Value, match.Groups[2].Value) : value;
    }
    [GeneratedRegex(@"^AAC-LC (\d+) Hz, (\d+) channel\(s\)$", RegexOptions.CultureInvariant)]
    private static partial Regex AacDescription();
}

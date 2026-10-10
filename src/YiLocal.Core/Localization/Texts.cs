using System.Globalization;
using System.Resources;
using System.Text.Json;

namespace YiLocal.Core.Localization;

public sealed record AppLanguage(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Display resources only. Protocol, catalogue and preference identifiers stay invariant.</summary>
public static class Texts
{
    static readonly ResourceManager resources = new("YiLocal.Core.Localization.Strings", typeof(Texts).Assembly);
    static CultureInfo culture = CultureInfo.GetCultureInfo("en");
    public static CultureInfo Culture => culture;
    public static IReadOnlyList<AppLanguage> Languages { get; } = LoadLanguages();

    static IReadOnlyList<AppLanguage> LoadLanguages()
    {
        using var stream = typeof(Texts).Assembly.GetManifestResourceStream("OpenYI.languages.json")
            ?? throw new InvalidOperationException("Missing language catalogue.");
        var languages = JsonSerializer.Deserialize<AppLanguage[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return Array.AsReadOnly(languages);
    }

    /// <summary>Resolve a saved language or region to an installed translation; English is always the default.</summary>
    public static string ResolveLanguage(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code))
            try
            {
                for (var candidate = CultureInfo.GetCultureInfo(code); candidate != CultureInfo.InvariantCulture; candidate = candidate.Parent)
                    if (Languages.FirstOrDefault(l => string.Equals(l.Code, candidate.Name, StringComparison.OrdinalIgnoreCase)) is { } match)
                        return match.Code;
            }
            catch (CultureNotFoundException) { }
        return "en";
    }

    /// <summary>Call before creating controls. Changes to the saved preference take effect on the next launch.</summary>
    public static void Initialize(string? code)
    {
        culture = CultureInfo.GetCultureInfo(ResolveLanguage(code));
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        // Keep Windows regional number/date formats; in particular, never change wire/FFmpeg serialization.
    }

    public static string Get(string key) => resources.GetString(key, culture)
        ?? throw new MissingManifestResourceException($"Missing OpenYI resource: {key}");
    public static string Format(string key, params object?[] arguments) => string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
}

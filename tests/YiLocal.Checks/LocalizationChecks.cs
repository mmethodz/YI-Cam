using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using YiLocal.Core;
using YiLocal.Core.Localization;

static class LocalizationChecks
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static Dictionary<string, string> Read(string path) => XDocument.Load(path).Root!.Elements("data")
        .ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);
    static string[] Placeholders(string value) => Regex.Matches(value, @"\{\d+(?:,-?\d+)?(?::[^{}]*)?\}")
        .Select(m => m.Value).Order().ToArray();

    public static void Run()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "YiLocal.sln"))) root = root.Parent;
        if (root is null) throw new Exception("Run localization checks from the source repository.");
        string resourcesPath = Path.Combine(root.FullName, "src", "YiLocal.Core", "Localization");
        var english = Read(Path.Combine(resourcesPath, "Strings.resx"));
        var manager = new ResourceManager("YiLocal.Core.Localization.Strings", typeof(Texts).Assembly);
        Require(Texts.Languages[0] == new AppLanguage("en", "English") && Texts.Culture.Name == "en", "English must be the default, independent of the OS language.");
        Require(Texts.Languages.Select(l => l.Code).Distinct().Count() == Texts.Languages.Count, "Duplicate language codes.");
        foreach (var language in Texts.Languages)
        {
            Require(!string.IsNullOrWhiteSpace(language.Name) && CultureInfo.GetCultureInfo(language.Code).Name == language.Code, "Invalid language catalogue entry.");
            var translated = language.Code == "en" ? english : Read(Path.Combine(resourcesPath, $"Strings.{language.Code}.resx"));
            Require(english.Keys.Order().SequenceEqual(translated.Keys.Order()), $"Incomplete/unknown keys in {language.Code}.");
            foreach (var (key, text) in translated)
            {
                Require(!string.IsNullOrWhiteSpace(text), $"Empty translation: {language.Code}/{key}");
                Require(Placeholders(english[key]).SequenceEqual(Placeholders(text)), $"Changed placeholders or formats: {language.Code}/{key}");
                var format = CompositeFormat.Parse(text);
                _ = string.Format(CultureInfo.InvariantCulture, format, Enumerable.Range(0, format.MinimumArgumentCount).Select(i => (object)new FormatProbe(i)).ToArray());
                string? compiled = manager.GetString(key, CultureInfo.GetCultureInfo(language.Code));
                Require(compiled?.Replace("\r\n", "\n") == text.Replace("\r\n", "\n"), $"Missing/mismatched compiled resource: {language.Code}/{key}");
                if (english[key].Contains('|'))
                {
                    var before = english[key].Split('|'); var after = text.Split('|');
                    Require(before.Length % 2 == 0 && after.Length == before.Length && before.Where((_, i) => i % 2 == 1).SequenceEqual(after.Where((_, i) => i % 2 == 1)), $"Changed file dialog patterns: {key}");
                }
            }
        }
        foreach (string file in Directory.GetFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            foreach (Match match in Regex.Matches(File.ReadAllText(file), "(?:L|Texts)\\.(?:Get|Format)\\(\"([^\"]+)\""))
                Require(english.ContainsKey(match.Groups[1].Value), $"Unknown resource {match.Groups[1].Value} in {Path.GetFileName(file)}");

        var regional = CultureInfo.CurrentCulture;
        string initial = JsonSerializer.Serialize(new RecordingOptions(RecordingEncoding.Balanced, .5, CaptureMode.Motion));
        string temp = Path.Combine(Path.GetTempPath(), "openyi-languages-" + Guid.NewGuid().ToString("N"));
        try
        {
            Texts.Initialize("fi-FI");
            Require(Texts.Culture.Name == "fi" && CultureInfo.CurrentCulture == regional, "Language changed regional/protocol formatting.");
            Require(Texts.Get("ConnectCamera") == "Yhdistä kameraan", "Finnish satellite not loaded.");
            Require(Task.Run(() => Texts.Get("ConnectCamera")).Result == "Yhdistä kameraan", "Worker-thread status lost the language.");
            Require(manager.GetString("ConnectCamera", CultureInfo.GetCultureInfo("fi-FI")) == "Yhdistä kameraan", "Regional fallback failed.");
            Require(manager.GetString("ConnectCamera", CultureInfo.GetCultureInfo("sv")) == english["ConnectCamera"], "English fallback failed.");
            Require(Texts.ResolveLanguage(null) == "en" && Texts.ResolveLanguage("unavailable") == "en" && Texts.ResolveLanguage("FI-fi") == "fi", "Saved language fallback failed.");
            var options = new RecordingOptions(RecordingEncoding.Balanced, .5, CaptureMode.Motion);
            Require(options.Label == "H.264 balanced (CRF 28)" && JsonSerializer.Serialize(options) == initial, "Language changed saved recording data.");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
            Require(options.Filter!.Contains("t*0.5") && !options.Filter.Contains("t*0,5"), "Finnish decimal separator leaked into FFmpeg.");
            Require(RecordingText.Profile(options.Label) == "H.264 tasapainoinen (CRF 28)" && RecordingText.Kind("Motion") == "Liike", "Catalogue display did not translate.");
            Require(RecordingText.Profile("Community profile") == "Community profile" && RecordingText.Kind("Future kind") == "Future kind", "Unknown metadata was overwritten.");
            Require(RecordingText.Audio("AAC-LC 16000 Hz, 1 channel(s)") == "AAC-LC 16000 Hz · kanavia: 1", "Existing audio metadata did not translate.");
            using var library = new RecordingLibrary(temp);
            for (int i = 1; i <= 4; i++)
            {
                string name = $"YI_2026-01-01_00-00-0{i}_1280x720_abcdef.mp4";
                File.WriteAllBytes(library.ClipPath(name), [1]);
                library.Register(name, 1280, 720, new("Etupiha", options.Label, RecordingText.FilterKind(i)!)); library.Finish(name, 1);
            }
            foreach (string code in new[] { "en", "fi" })
            {
                Texts.Initialize(code);
                for (int i = 1; i <= 4; i++)
                {
                    var matching = library.Clips().Where(new ClipFilter(Camera: "Etupiha", Kind: RecordingText.FilterKind(i)).Matches).ToArray();
                    Require(matching.Length == 1 && matching[0].Metadata!.Profile == options.Label, "Changing language broke an existing catalogue filter.");
                }
            }
        }
        finally
        {
            Texts.Initialize("en"); CultureInfo.CurrentCulture = regional;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
        Console.WriteLine($"Localization: {english.Count} keys × {Texts.Languages.Count} languages; placeholders, fallback, worker status, invariant data and all recording filters passed.");
    }
    sealed record FormatProbe(int Index) : IFormattable
    {
        public string ToString(string? format, IFormatProvider? provider) => $"value-{Index}";
    }
}

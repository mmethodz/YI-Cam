# Interface languages

OpenYI's native Windows app includes **English** and **Suomi (Finnish)**. English
is the default, regardless of the Windows display language. Choose **Settings →
App language / Kieli → Suomi**, then close and reopen OpenYI when convenient.
In Finnish, the same page is **Asetukset → Sovelluksen kieli / Language**.
Changing the selection saves immediately without interrupting cameras or recordings;
it does not switch an active session's controls halfway through an operation.

The preference is the `Language` field in
`%LOCALAPPDATA%\YI Local\settings.json`. It uses the same atomic save and backup
as other preferences, including when starting from `dist/`. Older settings without
the field, unknown language codes and malformed language fields fall back to
English without discarding storage, capture or alarm settings. A regional code
such as `fi-FI` resolves to the available `fi` translation.

Windows regional preferences continue to determine displayed numbers and dates.
Changing the interface language does not change camera commands, FFmpeg arguments,
JSON property names, catalogue identifiers, filenames or existing camera names.
Existing recordings display translated profile/type descriptions and are filtered
using their original stored identifiers. Device names supplied by Windows and
raw diagnostic output from FFmpeg/the operating system may remain in their source
language. Python is still the English-language protocol/reference implementation.

## Adding or improving a translation

No language-specific C# branches or vendor runtime are needed. The app uses the
standard [.NET resource and satellite-assembly model](https://learn.microsoft.com/en-us/dotnet/core/extensions/localization).

1. Copy `src/YiLocal.Core/Localization/Strings.resx` to
   `Strings.<culture>.resx`, for example `Strings.sv.resx`.
2. Translate each `<value>` while preserving its `<data name="…">` key. The
   `<comment>` gives the owning source file and the meaning of format arguments.
   A resource editor or UTF-8 text editor is sufficient. Keep the original English
   file as the reference; do not use another translation as the source of meaning.
3. Add an entry to `src/YiLocal.Core/Localization/languages.json`, for example
   `{ "code": "sv", "name": "Svenska" }`. Use a .NET culture code and the
   language's own name so users can find it even if they selected the wrong language.
   Keep English first. The selector reads this catalogue automatically.
4. Run the checks below, build, select the language and restart the app. Inspect
   the default 1180 × 740 client area and the bottom of scrollable pages. Keep
   live controls concise. Check the recording browser, dialogs, alarm status and
   tray as well as tab headings. Do not arm/play an alarm just to inspect its text.
5. Submit the resource/catalogue changes and describe which layouts you checked.
   No screenshots containing private camera footage, credentials or QR codes.

```powershell
dotnet run --project tests/YiLocal.Checks -c Release -- --localization
dotnet run --project tests/YiLocal.Windows.Checks -c Release -- --settings
powershell -NoProfile -File scripts/build-windows.ps1
```

The localization check validates every registered language's key coverage,
compiled resources, placeholder/format parity, file-dialog patterns, English and
regional fallback, background-thread messages, invariant persisted data and all
four catalogue type filters. A missing translation falls back to English at
runtime; the checks require complete resources before a translation is registered
as a shipped language. There is no runtime translation service or network lookup.

### Translation rules

- Preserve placeholders such as `{0}`, `{1:0.0}` and `{0:x4}` exactly, including
  their format specifiers and number of occurrences. You may reorder them to suit
  grammar. Translate complete messages where possible; do not split sentences
  into independently translated words.
- Preserve significant spaces/newlines. Prefix resources ending in a space
  precede an error, camera name or filename. Do not translate error output supplied
  by another program.
- For file-dialog filters, translate descriptions only. Keep `|`, `*.png`,
  `*.mp4`, `*.dpapi` and all other wildcard patterns unchanged.
- Keep technical identifiers such as AAC, H.264, CRF, FFmpeg, Wi-Fi, SSID, QR and
  device command numbers intact. Explain experimental/verification limitations
  accurately. “Accepted by the camera” does not establish physical behavior.
- English resource keys are stable identifiers, not text to translate. Avoid
  renaming a key when refining wording. Add new visible text to both resources
  and use `L.Get(key)` or `L.Format(key, arguments)` in C#.
- Never use translated text as a saved enum, database value or filter key.
  `RecordingText` is the display adapter for the existing catalogue format.

Publish/copy the **whole** `dist/YI-Local` directory. Finnish is compiled into
`fi/YiLocal.Core.resources.dll`; other languages get their own culture directories.
Shipping only the main executable omits the translations and other dependencies.

## Verification

English and Finnish resource coverage and regression checks are automated. The
Finnish default-size live controls, scrollable capture/alarm settings and the
recording browser were inspected in the built Windows app. Selecting Suomi,
saving and restarting preserved the existing capture configuration. The Finnish
motion filter found the existing original-stream and encoded motion recordings.
Native protocol/storage checks and synthetic motion/AAC and source/5/1/0.5-fps
player checks passed. These do not qualify additional physical cameras or new
protocol behavior.

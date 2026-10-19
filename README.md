# MusicConverter

WPF (MVVM) application and library that converts music notation to **MusicXML 4.0** (the default; 3.1 remains selectable).

| Format | Extensions | Notes |
|---|---|---|
| LilyPond | `.ly`, `.ily` | `\include` is resolved (the built-in `english.ly` is recognised); multiple voices per staff, slurs, English/Dutch note names and lyrics (`\lyricsto`, `\addlyrics`, hyphenation, melismas) are converted; articulations (`-. -> -- -^`, `\staccato`, `\accent`, `\tenuto`, `\marcato`), ornaments (`\trill`, `\turn`, `\mordent`, `\prall`), `\fermata`, dynamics (`\p`…`\ff`, `\<`, `\>`, `\!`), `\tempo` and grace notes (`\grace`, `\acciaccatura`, `\appoggiatura`, `\afterGrace`) are converted; only the first `\book` is converted; user functions/Scheme are skipped, not evaluated; staff-group brackets are not converted |
| ABC | `.abc` | lyrics (`w:` lines, one verse per line); decorations (`!trill!`, `!fermata!`, `!p!`…`!ff!`, `!crescendo(!`, `.`, `H`, `T`, …), grace notes `{g}` / `{/g}` and `Q:` tempo |
| NWCTXT | `.nwctxt` | second voice per staff via `Dur2`/`Pos2` (or `VoiceNum` in `Opts`); lyrics (`Lyric1`…); `Slur`, `Staccato`, `Accent`, `Tenuto` and `Grace` note flags; `Dynamic`, `DynamicVariance` (hairpins), `Tempo`, `TempoVariance` (fermata / text), `Text`, `PerformanceStyle`, `SustainPedal`; `RestChord` |
| Humdrum | `.krn`, `.hum` | `**kern` spines → parts; spine splits (`*^`) become extra voices of the part; `**text`/`**silbe` spines become lyrics; `**dynam` spines, `*MM` tempo, slurs, articulation/ornament/fermata characters and grace notes (`q`/`Q`) |
| MEI | `.mei` | staves → parts; layers become voices; `<verse>`/`<syl>` lyrics; `<dynam>`, `<hairpin>`, `<fermata>`, `<trill>`, `<mordent>`, `<turn>`, `<slur>`, `<tempo>`, `<dir>` control events (by `@startid` or `@tstamp`), `@artic`, `@grace` and `<graceGrp>` |
| MusiXTeX | `.tex`, `.mtx` | `.tex` accepted only if it contains MusiXTeX markup; pitch letters mapped per the chanson.tex example |
| MUP | `.mup` | multiple voices per staff; `lyrics` statements; accidentals follow the key signature only |

Each converter supports a core subset (notes, chords, rests, ties, tuplets, key/time/clef, multiple staves). Unsupported constructs (repeats and endings, MUP and MusiXTeX ornaments/dynamics/grace notes, …) are skipped with a logged warning.


```
src/MusicConverter.Core   conversion library (net8.0)
src/MusicConverter.App    WPF UI (net8.0-windows)
tests/MusicConverter.Tests  xunit tests
samples/                  one sample per format
```

Pipeline: `IMusicFormatConverter` (text → neutral model → `MusicXmlDocument`) → `MusicXmlSerializer` (versioned writer) → file.
Progress is reported through `IProgress<ConversionProgress>` (Parsing → Converting → Serializing → Completed); every API takes a `CancellationToken`.
Failures are `ConversionException` with a category (`ParseError`, `MappingError`, `SerializationError`, `UnsupportedFormat`), file path and converter name, and are logged with the stack trace.

## Batch (folder) conversion
Tick **Batch (folder) mode** in the app, pick an input folder (optionally with subfolders) and an optional output folder (the folder layout is mirrored; empty = next to each source). Existing outputs are skipped unless **Overwrite** is ticked; one failing file never stops the batch. Programmatically use `BatchConversionService.ConvertDirectoryAsync`.

## Add a format
Create a class deriving from `MusicFormatConverterBase` (or implementing `IMusicFormatConverter`) with a public constructor taking `ILogger` (or none). `ConverterRegistry.DiscoverConverters()` finds it automatically; DLLs in a `plugins` folder are loaded too. Use `[IgnoreConverter]` to opt out.

## Add a MusicXML version
Add a namespace (e.g. `MusicConverter.MusicXml.V4_0`) with a class implementing `IMusicXmlVersionWriter`. The serializer discovers it; converters are untouched. Deprecated features a writer lists are logged as warnings.

## Build / test
```
dotnet build MusicConverter.slnx
dotnet test tests\MusicConverter.Tests
dotnet run --project src\MusicConverter.App
```

# TrackMatch

TrackMatch is a tool for finding acoustically identical or closely related tracks in large FLAC music libraries.

The project starts as a console-based scanner and analysis tool. A GUI can later reuse the same shared libraries and database.

## Requirements

- .NET 10 SDK
- `fpcalc` from Chromaprint for acoustic fingerprint comparison

`fpcalc` can be available on `PATH`, specified with `TRACKMATCH_FPCALC`, or passed to the `compare` command with `--fpcalc`.

## Build

```powershell
dotnet restore TrackMatch.sln
dotnet build TrackMatch.sln --configuration Release
```

## Scan FLAC metadata

```powershell
dotnet run --project src/TrackMatch.Scanner -- scan "D:\Music"
```

The scanner recursively enumerates FLAC files and reads STREAMINFO and Vorbis Comment metadata without reading the audio frames themselves. A malformed or unreadable FLAC file is reported as an error while the remaining files continue to be scanned.

## Compare two tracks with Chromaprint

```powershell
dotnet run --project src/TrackMatch.Scanner -- compare "D:\Music\a.flac" "D:\Music\b.flac"
```

For Probe data collection, CSV output is available:

```powershell
dotnet run --project src/TrackMatch.Scanner -- compare "D:\Music\a.flac" "D:\Music\b.flac" --csv
```

TrackMatch invokes `fpcalc -raw -json -length 0 -algorithm 2` so that the entire track is fingerprinted as raw 32-bit values under fixed algorithm settings. The comparer searches relative offsets and reports Hamming-bit similarity, matched duration, coverage for both files, and the best offset. Classification thresholds are intentionally deferred until representative real-library pairs have been measured.

## Project structure

- `TrackMatch.Core` - domain models, fingerprint comparison, and shared abstractions.
- `TrackMatch.Infrastructure` - filesystem, FLAC metadata, Chromaprint process integration, and future persistence.
- `TrackMatch.Scanner` - console host for scanning and analysis commands.
- `TrackMatch.Core.Tests` - tests for core behavior.
- `TrackMatch.Infrastructure.Tests` - tests for infrastructure behavior.

The GUI project will be added after the scanner and matching logic are validated.

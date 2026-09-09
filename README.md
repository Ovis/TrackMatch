# TrackMatch

TrackMatch is a tool for finding acoustically duplicate or related FLAC tracks in a music library.

The project is being built around acoustic fingerprints so tracks can still be compared when file names, tags, loudness, mastering, or leading/trailing silence differ.

## Requirements

- .NET 10 SDK
- `fpcalc` from Chromaprint for fingerprint comparison commands

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

Use `--csv` for machine-readable output. `fpcalc` can be specified with `--fpcalc`, the `TRACKMATCH_FPCALC` environment variable, or `PATH`.

The Probe uses the complete raw fingerprint (`fpcalc -raw -json -length 0 -algorithm 2`) and performs offset-aware comparison. Classification thresholds are intentionally not fixed yet; they will be calibrated using known real-library pairs.

## Run a batch Probe

Create an input CSV with the following columns:

```csv
Label,ExpectedRelation,FileA,FileB,Notes
same-track,duplicate,D:\Music\album-a\track.flac,D:\Music\album-b\track.flac,same recording on different CDs
full-vs-tv,tv-size,D:\Music\full.flac,D:\Music\tv-size.flac,
```

`ExpectedRelation` is a free-form label intended for calibration data, for example `duplicate`, `remaster`, `tv-size`, `instrumental`, `remix`, `live`, or `unrelated`.

Run all pairs and write a result CSV:

```powershell
dotnet run --project src/TrackMatch.Scanner -- probe .\probe-pairs.csv --output .\probe-results.csv
```

During one Probe run, fingerprints are cached by file path so an audio file shared by several pairs is processed by `fpcalc` only once.

## Analyze Probe results

After collecting known-pair results, summarize the distribution for each `ExpectedRelation`:

```powershell
dotnet run --project src/TrackMatch.Scanner -- analyze-probe .\probe-results.csv
```

The summary reports count and minimum / median / maximum values for similarity, the lower and higher of the two coverage values, and duration ratio. These statistics are intended to calibrate classification thresholds from real audio rather than hard-code thresholds before measurements exist.

## Classify Probe results with a calibrated profile

The relationship classifier has no built-in threshold defaults. Create a JSON profile from the measured distributions with all of the following properties:

- `DuplicateMinimumSimilarity`
- `DuplicateMinimumCoverage`
- `DuplicateMinimumDurationRatio`
- `ShortVersionMinimumSimilarity`
- `ShortVersionMinimumMaximumCoverage`
- `ShortVersionMaximumMinimumCoverage`
- `ShortVersionMaximumDurationRatio`
- `AlternateVersionMinimumSimilarity`

All values are ratios from `0.0` through `1.0`. Apply the profile to the same Probe result CSV:

```powershell
dotnet run --project src/TrackMatch.Scanner -- classify-probe .\probe-results.csv --profile .\thresholds.json
```

Each row is classified as `DuplicateCandidate`, `ShortVersionCandidate`, `AlternateVersionCandidate`, or `NeedsReview`. The output keeps `ExpectedRelation` beside the predicted relation so calibration can be iterated without changing the classifier code. Threshold values should be chosen from real Probe measurements; the repository intentionally does not provide arbitrary default numbers.

## Project structure

- `TrackMatch.Core` - domain models, fingerprint comparison, relationship classification and Probe orchestration.
- `TrackMatch.Infrastructure` - file-system, FLAC metadata and Chromaprint process integration.
- `TrackMatch.Scanner` - command-line host and text/CSV input-output.
- `TrackMatch.Core.Tests` - Core tests.
- `TrackMatch.Infrastructure.Tests` - Infrastructure tests.

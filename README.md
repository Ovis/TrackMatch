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

To persist the library in SQLite and perform an incremental scan including Chromaprint extraction, specify `--db`:

```powershell
dotnet run --project src/TrackMatch.Scanner -- scan "D:\Music" --db ".\trackmatch.db"
```

`fpcalc` can also be specified explicitly:

```powershell
dotnet run --project src/TrackMatch.Scanner -- scan "D:\Music" --db ".\trackmatch.db" --fpcalc "C:\Tools\fpcalc.exe"
```

The first run registers all readable FLAC tracks and stores raw Chromaprint fingerprints. Later runs compare file size and last-write time, update metadata and regenerate fingerprints only for changed tracks, leave unchanged tracks with existing fingerprints untouched, and retry tracks whose fingerprint is still missing. Tracks that disappeared from the scanned root are retained with `IsMissing` set. Metadata or fingerprint failures are recorded as per-file errors without aborting the rest of the scan. Each run is recorded in `ScanSessions` with added, updated, missing, and error counts.

## Generate comparison candidates

After fingerprints are stored, generate a reduced set of track pairs for detailed comparison:

```powershell
dotnet run --project src/TrackMatch.Scanner -- generate-candidates --db ".\trackmatch.db"
```

Candidate generation does not compare every pair in the library. It builds overlapping segment SimHashes from the stored raw Chromaprint fingerprints and uses a multi-index Hamming search to find acoustically promising track pairs. The defaults are 256 fingerprint items per segment, a 128-item stride, and a maximum segment SimHash Hamming distance of 3.

The candidate-generation parameters can be changed for calibration without rebuilding the application:

```powershell
dotnet run --project src/TrackMatch.Scanner -- generate-candidates --db ".\trackmatch.db" --segment-length 256 --stride 128 --max-distance 3
```

`--algorithm` can be used when working with a fingerprint algorithm other than the current default of 2. Candidate generation replaces the current `CandidatePairs` set. Replacing candidate pairs also removes detailed comparison and classification results that belong to the previous candidate set.

## Analyze and classify generated candidates

Run the offset-aware raw-fingerprint comparison only for the generated candidate pairs:

```powershell
dotnet run --project src/TrackMatch.Scanner -- analyze-candidates --db ".\trackmatch.db"
```

The command reuses fingerprints already stored in SQLite; it does not run `fpcalc` again. For every available candidate pair it stores similarity, best offset, matched duration, coverage for both tracks, and duration ratio in `CandidateComparisons`. A stale candidate whose fingerprint is no longer available is skipped instead of comparing invalid data.

After a threshold profile has been calibrated from Probe measurements, pass it to the same command to classify the detailed comparison results and export a human-reviewable report:

```powershell
dotnet run --project src/TrackMatch.Scanner -- analyze-candidates --db ".\trackmatch.db" --profile ".\thresholds.json" --format csv --output ".\candidates.csv"
```

`--format` accepts `csv`, `text`, or `txt`. If `--output` is omitted, the report is written to standard output. File output uses UTF-8 with BOM. The report contains relationship kind, similarity, coverage values, duration ratio, best offset, matched duration, and both tracks' artist/title/album/genre/path metadata.

Classification results are also stored in `CandidateClassifications`. The serialized threshold profile used for each classification is retained so the decision conditions can be traced later. Re-running classification replaces the previous classifications with results from the current profile.

## Review a candidate

A reviewed pair is stored independently from `CandidatePairs`, so regenerating candidates does not lose the decision. Reviewed pairs are excluded from future candidate generation and from classification reports immediately. Track order is normalized, so `123/456` and `456/123` refer to the same pair.

Mark a pair as not duplicate:

```powershell
dotnet run --project src/TrackMatch.Scanner -- review-candidate --db ".\trackmatch.db" --track-a 123 --track-b 456 --note "different arrangement"
```

The default decision remains `NotDuplicate` for compatibility. To confirm that a pair is duplicate and record which copy should be retained, specify `--decision duplicate` and `--keep`:

```powershell
dotnet run --project src/TrackMatch.Scanner -- review-candidate --db ".\trackmatch.db" --track-a 123 --track-b 456 --decision duplicate --keep 123 --note "keep original album copy"
```

`--keep` must be one of the two Track IDs in the reviewed pair. The retained Track is stored separately from the pair decision so the rejected copy can later be moved without asking again.

## Move rejected tracks to Trash

`trash-reviewed` builds a move plan from `ConfirmedDuplicate` reviews. It is a dry-run by default and preserves each rejected track's path relative to the library root under the Trash root:

```powershell
dotnet run --project src/TrackMatch.Scanner -- trash-reviewed --db ".\trackmatch.db" --library-root "D:\Music" --trash-root "D:\MusicTrash"
```

Review the reported `Ready` rows, then add `--execute` to actually move them:

```powershell
dotnet run --project src/TrackMatch.Scanner -- trash-reviewed --db ".\trackmatch.db" --library-root "D:\Music" --trash-root "D:\MusicTrash" --execute
```

The command never overwrites an existing Trash file. It also blocks a track when review data conflicts and the same Track ID is selected as both Keep and Reject across different confirmed duplicate pairs. Tracks outside the specified library root, already-missing tracks, missing source files, and existing destinations are reported instead of moved. The Trash root must be outside the library root so moved FLAC files are not re-discovered by a later scan.

After a successful move, the rejected Track is marked `IsMissing` and its stored fingerprint is removed immediately. The original file is not deleted; it is moved to the corresponding relative path under the Trash root.

The current library pipeline is therefore:

```text
scan -> generate-candidates -> analyze-candidates -> classify/export -> human review -> trash-reviewed
```

The repository intentionally does not provide arbitrary default relationship thresholds. Use real Probe measurements to create `thresholds.json` before classifying the library.

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

- `TrackMatch.Core` - domain models, fingerprint comparison, relationship classification, candidate generation and Probe orchestration.
- `TrackMatch.Infrastructure` - file-system, FLAC metadata, SQLite persistence and Chromaprint process integration.
- `TrackMatch.Scanner` - command-line host and text/CSV input-output.
- `TrackMatch.Core.Tests` - Core tests.
- `TrackMatch.Infrastructure.Tests` - Infrastructure tests.

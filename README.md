# TrackMatch

[日本語](README.ja.md)

TrackMatch is a Windows desktop application for finding duplicate files that appear to contain the same audio, as well as very similar audio tracks, and helping you decide which file to keep.

TrackMatch compares audio using Chromaprint fingerprints, so tracks can still be matched when file names, tags, loudness, mastering, or leading/trailing silence differ. It currently supports FLAC and MP3 files.

## Important notice

TrackMatch can move audio files to a configured Trash folder. Review the detected duplicates and the destination carefully before executing file organization.

**Back up important files before using TrackMatch.**

TrackMatch is provided without warranty. To the extent permitted by applicable law, the author is not responsible for data loss, file loss, or any other damage arising from the use of this software. See [LICENSE](LICENSE) for the license terms and warranty/liability disclaimer.

## Requirements

### Using a release build

- Windows 10 or Windows 11

The Windows release archive includes the required Chromaprint `fpcalc` files and native audio dependencies. No separate .NET SDK installation is required for normal use.

### Development

- .NET 10 SDK

## Getting started

1. Download the Windows x64 ZIP from GitHub Releases and extract it to a folder.
2. Start `TrackMatch.App.exe`.
3. Create a Library and add one or more folders containing FLAC or MP3 files.
4. Run analysis. TrackMatch scans the Library, extracts fingerprints, generates comparison candidates, and evaluates their similarity.
5. Review candidates and choose one of:
   - `重複ではない` — the files are not duplicates.
   - `重複 / Aを残す` — the files are duplicates and A is preferred.
   - `重複 / Bを残す` — the files are duplicates and B is preferred.
6. Check the resulting duplicate groups and the file selected to remain.
7. If necessary, configure the Trash folder and explicitly move files selected for removal.

Marking files as duplicates does not immediately delete or move them. At that point, TrackMatch only records the duplicate decision. The actual files are moved only when you later explicitly run the Move to Trash operation.

## How TrackMatch organizes duplicates

A physical audio file is represented as a Track independently of Library membership. The same Track may therefore belong to multiple Libraries.

Comparison candidates are generated only between tracks that coexist in at least one Library. Review decisions for the same pair of physical Tracks are shared across Libraries.

When a pair is confirmed as duplicate, the review also records which file is preferred. These preference relationships form duplicate groups. TrackMatch derives the file to keep for each Library from the accumulated review decisions rather than storing an independent manual selection.

For groups containing more than two files, TrackMatch may request additional comparisons when they are necessary to determine which file should remain. Comparisons that cannot affect that decision may be skipped.

If review decisions contradict one another, or if a file is being re-evaluated after its contents change, TrackMatch blocks affected file-organization operations until the state is safe to use again.

## Analysis and rescanning

A Library scan recursively reads supported FLAC and MP3 files. TrackMatch stores reusable metadata, fingerprints, comparison results, and review state in its local database.

Later scans reuse analysis data for unchanged files. If a file disappears from a successfully scanned Library root, it is marked as missing instead of immediately deleting its Track record.

When a previously known file changes, TrackMatch distinguishes metadata-only changes from audio-content changes where possible. Existing review decisions are not discarded merely because metadata changed. If the audio content has changed, affected analysis data and directly related review state are re-evaluated.

## Track management

The Track management window can display all Tracks, missing Tracks, and Tracks that no longer belong to any Library. It also provides Force Reanalysis and explicit deletion of TrackMatch-managed data.

Force Reanalysis invalidates analysis data that must be recalculated. Explicit Track deletion removes TrackMatch-managed records but does not delete the original audio file.

## Moving a Library root

Library roots can be remapped when a music directory is moved. Root remapping preserves Track IDs and reusable analysis data where possible, updates membership paths, and marks files as missing when the expected destination file is absent.

Path collisions are checked before the remap is applied. TrackMatch does not automatically add unrelated Library memberships simply because the destination overlaps another Library root.

## Trash and file safety

Trash processing is explicit. Files selected for removal are moved under the configured Trash folder, and TrackMatch does not overwrite an existing file at the destination.

Because one physical Track may be shared by multiple Libraries, moving it affects every Library that references that file. TrackMatch checks these relationships and blocks file organization when the current duplicate/review state is not safe to apply.

If the database update fails after a physical file has been moved, TrackMatch attempts to move the file back to its original location before reporting the failure. This is a safety measure, not a substitute for backups.

A file manually restored from Trash can be detected again on a later scan. TrackMatch reuses the existing Track where possible and recalculates the current organization state rather than blindly reusing a previous removal decision.

## Build

```powershell
dotnet restore TrackMatch.slnx
dotnet build TrackMatch.slnx --configuration Release
```

## Run from source

```powershell
dotnet run --project src/TrackMatch.App
```

## License

TrackMatch is distributed under the MIT License. See [LICENSE](LICENSE).

ZIP archives distributed through GitHub Releases include the third-party components required to run TrackMatch. Each of these components is subject to its own license terms. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for details.

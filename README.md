# TrackMatch

[日本語](README.ja.md)

TrackMatch is a Windows desktop application for finding duplicate files that appear to contain the same audio, as well as very similar audio tracks, and helping you decide which file to keep.

TrackMatch compares the audio itself using [Chromaprint](https://github.com/acoustid/chromaprint) fingerprints, so tracks can still be compared when file names, tags, loudness, mastering, or leading/trailing silence differ.

The officially supported audio formats are currently FLAC and MP3.

## Important notice

TrackMatch can move audio files you no longer want to a configured Trash folder.

Marking files as duplicates does not delete or move them. Before running Move to Trash, carefully check the files to be moved and the destination.

**Back up important files before using TrackMatch.**

TrackMatch is provided without warranty. To the extent permitted by applicable law, the author is not responsible for data loss, file loss, or any other damage arising from the use of this software. See [LICENSE](LICENSE) for the license terms and warranty/liability disclaimer.

## Requirements

### Using a release build

- Windows 10 or Windows 11
- .NET 10 Desktop Runtime (x64)

ZIP archives distributed through GitHub Releases already include Chromaprint `fpcalc` and the native audio libraries required by TrackMatch.

### Development

- .NET 10 SDK

## Getting started

1. Download the ZIP archive from GitHub Releases and extract it to a folder.
2. Start `TrackMatch.App.exe`.
3. Create a Library and add a folder containing FLAC or MP3 files.
4. Run analysis. TrackMatch examines the audio files and finds pairs that may be duplicates.
5. Review each candidate and choose one of:
   - `重複ではない` — the two files are not duplicates.
   - `重複 / Aを残す` — the files are duplicates and A should be kept.
   - `重複 / Bを残す` — the files are duplicates and B should be kept.
6. Check the resulting duplicate groups and the files selected to remain.
7. To organize unwanted files, configure the Trash folder and run Move to Trash.

Marking files as duplicates does not immediately delete or move them. At that point, TrackMatch only records the duplicate decision. The actual files are moved only when you later run Move to Trash.

## Duplicate decisions and choosing files to keep

Candidates found by TrackMatch are not automatically confirmed as duplicates. Review them and choose `重複ではない`, `重複 / Aを残す`, or `重複 / Bを残す`.

When you confirm a duplicate, you also record which file you want to keep. For groups of three or more files containing the same audio, TrackMatch uses the decisions already made to determine which file should remain.

TrackMatch may skip comparisons whose result is already determined by previous decisions. Conversely, when another comparison is needed to decide which file to keep, that pair is shown as a new candidate.

If the same file is registered in multiple Libraries, duplicate decisions for that file are shared between those Libraries.

If decisions conflict or the files otherwise cannot be organized safely, TrackMatch does not move the affected files until the problem is resolved.

## Analysis and rescanning

When you scan a Library, TrackMatch searches its registered folders for FLAC and MP3 files and performs the analysis needed to detect duplicates.

If a previously analyzed file has not changed, later scans reuse its existing analysis results.

If a file disappears from a registered folder, its TrackMatch record is not immediately deleted. It is marked as missing and can be checked from the Track management window.

When a file changes, TrackMatch attempts to distinguish changes to information such as tags from changes to the audio itself.

If only tags or similar information changed, existing duplicate decisions continue to be used. If the audio itself changed, TrackMatch prepares the affected analysis and duplicate decisions to be performed again as needed.

## Track management

The Track management window lists files recognized by TrackMatch.

It also shows files that are missing from their registered folders and files that currently belong to no Library.

You can reanalyze files or delete information held by TrackMatch when necessary.

Deleting information from the Track management window does not delete the original audio file.

## Moving a Library folder

If you move a music folder to another location, you can update the folder registered with TrackMatch.

After the change, scanning checks the files in the new location and reuses previous analysis results where possible. Files not found at the new location are marked as missing.

If the folder configuration would cause a conflict, TrackMatch reports an error without applying the change so that existing Libraries are not unintentionally affected.

## Trash and file safety

Marking files as duplicates does not delete or move them. Files selected for removal are moved to the configured Trash folder only when you run Move to Trash.

TrackMatch does not overwrite a file with the same name at the destination. It also avoids moving files when duplicate decisions conflict or another condition makes organization unsafe.

If TrackMatch fails to update its records after moving a file to Trash, it attempts to return the file to its original location. This cannot completely prevent file loss in every unexpected situation, so back up important files beforehand.

If you manually restore a file from Trash to its original location, TrackMatch can detect it again during the next scan.

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

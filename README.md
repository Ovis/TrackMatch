# TrackMatch

TrackMatch is a Windows desktop application for finding acoustically duplicate or related FLAC tracks in a music library.

The application uses Chromaprint fingerprints so tracks can still be compared when file names, tags, loudness, mastering, or leading/trailing silence differ.

## Requirements

- Windows 10/11
- .NET 10 SDK for development
- Chromaprint `fpcalc` for fingerprint extraction

Release archives include the required `fpcalc` files and native audio dependencies.

## Build

```powershell
dotnet restore TrackMatch.slnx
dotnet build TrackMatch.slnx --configuration Release
```

## Run

```powershell
dotnet run --project src/TrackMatch.App
```

TrackMatch is operated through the WPF application. The former command-line host has been removed so scanning, candidate generation, comparison, review, duplicate-group management, and Trash operations all use the same Library-scoped application workflow.

## Library workflow

Create a Library from the application and register one or more target folders. A physical audio file is stored as a Global Track identified by its normalized absolute path, while Library membership is managed separately.

The normal workflow is:

```text
Library scan
  -> candidate generation
  -> detailed comparison
  -> automatic classification
  -> human review
  -> duplicate-group Keep selection
  -> optional Trash move
```

A scan recursively reads supported audio metadata and extracts Chromaprint fingerprints when required. Later scans reuse unchanged Global Track analysis data and mark files that disappear from a successfully enumerated Root as Missing.

Candidate generation is always scoped to the selected Library. Different Libraries may share the same Global Track, but TrackMatch does not create comparison candidates between tracks that do not coexist in the same Library.

## Human review

The candidate list provides these Global Human Verdict operations:

- `重複ではない` — records `NotDuplicate`
- `重複 / Aを残す` — records `ConfirmedDuplicate` and selects A as the current Library's Keep
- `重複 / Bを残す` — records `ConfirmedDuplicate` and selects B as the current Library's Keep

The duplicate/not-duplicate verdict is Global for the Track pair. Keep selection is Library-specific and is managed as Duplicate Group state. If a Global Verdict created from another Library is changed, the application warns that the change is visible from every Library containing that pair.

ConfirmedDuplicate edges form Global Duplicate Groups. A Library may choose a Keep Track from the Global Group even when that Track is outside the current Library membership; this does not add membership automatically.

## Track management

The Track management window can display all Global Tracks, Missing Tracks, and tracks that are no longer owned by any Library. It also provides Force Reanalysis and explicit deletion of TrackMatch-managed data.

Force Reanalysis invalidates machine analysis data and the current Human Verdict for affected Tracks while retaining review history. Explicit Track deletion removes TrackMatch data but never deletes the original audio file.

## Root relocation

Library Roots can be remapped when a music directory is moved. Root Remap preserves Global Track IDs and reusable analysis data, updates affected child Roots and membership-relative paths, and marks tracks as Missing when the expected destination file is absent.

Path collisions are detected before applying the remap. TrackMatch does not automatically add unrelated Library memberships merely because the destination overlaps a Root belonging to another Library.

## Trash

Trash processing is explicit. Tracks selected for removal from the current Library's duplicate groups are moved under the configured Trash Root without overwriting an existing destination.

Shared Global Tracks are handled conservatively: the application shows the affected Libraries before moving a file. Once a physical file is moved away, that Global Track becomes Missing for every Library that referenced it. If the database update fails after the physical move, TrackMatch attempts to move the file back before reporting the failure.

If a Trash/Missing file is later restored to its original path, TrackMatch reuses the same Global Track ID and retains the historical verdict/disposition records, but the current Library Keep is returned to an unselected state. The restored file must therefore be reviewed again before Trash can be executed from the old disposition.

## Project structure

- `TrackMatch.Core` — domain models, candidate generation, fingerprint comparison, duplicate-group rules, and analysis services.
- `TrackMatch.Infrastructure` — file-system access, audio metadata, SQLite persistence, Chromaprint integration, and Trash I/O.
- `TrackMatch.Application` — Library-scoped application workflows used by the WPF UI.
- `TrackMatch.App` — WPF desktop application.
- `TrackMatch.Core.Tests` — Core unit tests.
- `TrackMatch.Infrastructure.Tests` — SQLite and infrastructure integration tests.
- `TrackMatch.App.Tests` — application/UI-facing regression tests.

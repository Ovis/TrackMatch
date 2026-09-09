# TrackMatch

TrackMatch is a tool for detecting and classifying duplicate or acoustically related FLAC tracks in a local music library.

The project is currently in its initial development stage. The first implementation target is a command-line scanner that can later share its core logic and database with a GUI application.

## Requirements

- .NET 10 SDK

## Build

```powershell
dotnet restore TrackMatch.sln
dotnet build TrackMatch.sln --configuration Release
```

## Project structure

- `TrackMatch.Core` - domain models and audio comparison logic.
- `TrackMatch.Infrastructure` - file system, metadata, persistence, and external integrations.
- `TrackMatch.Scanner` - command-line host for scanning and analysis.
- `TrackMatch.Core.Tests` - tests for core logic.
- `TrackMatch.Infrastructure.Tests` - tests for infrastructure logic.

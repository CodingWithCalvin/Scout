# Scout

A Windows desktop app for identifying TV episodes from video dialogue, reviewing the evidence, and planning episode names and season folders.

## Features

- One Scout per source video folder, with independent runs and saved history.
- Full-dialogue matching with a second verification pass and original evidence.
- Exact recording duplicates grouped together; competing recordings held as unresolved conflicts.
- Local model endpoints or a managed local runtime, with optional cloud providers.
- Settings for model downloads, usage, and API keys, with encrypted Windows credentials and explicit opt-in for cloud dialogue and downloads.
- Rename/move previews with separate handling for duplicates and non-episode features.
- Searchable episode results and a draggable divider to give dialogue evidence more room.
- Run deletion from history with undo; artifacts are retained for recovery.

## Screenshots

### Episode results and dialogue evidence

Review matched episode numbers and titles alongside the dialogue that supports each match.

![Scout episode results and dialogue evidence](docs/screenshots/results.png)

### Scout overview and setup

Keep the source folder, run setup, and activity together in one workspace.

![Scout overview and setup](docs/screenshots/overview.png)

## Current limits

Source media is never renamed, moved, or deleted. Automatic TVDB/TMDB guide fetching and audio transcription are not implemented. A new show needs an imported JSON episode guide under Settings > Usage and tools > Advanced run data. Embedded text subtitles are supported; saved dialogue can be supplied as an advanced override.

FFmpeg/FFprobe and a compatible llama-server are supplied separately. Experimental model downloads require opt-in; no model is universally approved. Cloud requests require separately configured credentials and can incur provider charges. Matcher accuracy still needs an end-to-end model benchmark; fixture tests and imported example results do not establish accuracy.

## Development

Scout is built with C# and WPF on .NET 11, using the prerelease SDK pinned in `global.json`.

### Build and run

On Windows, install the SDK selected by `global.json`, then run:

```powershell
./build.ps1
./artifacts/win-x64/Scout.exe
```

Pass `-DotnetPath C:\path\to\dotnet.exe` to use a particular SDK installation. The build runs the executable test suite and publishes a self-contained x64 app. End users do not need an installed .NET runtime.

### Package

```powershell
./package.ps1
```

Packaging produces a ZIP and SHA-256 file in `artifacts/releases`. Use `-SkipBuild` only after a successful build.

### Repository layout and tests

- `Scout.Core`: media pipeline, matcher, model transport, collection resolution, preview planner, settings and workspace persistence.
- `Scout.Desktop`: WPF application and Windows integration.
- `Scout.Tests`: executable regression suite with synthetic fixtures and mocked HTTP.
- `Scout.Core/Assets`: matcher prompts and JSON schemas.
- `Scout.Desktop/Assets`: SVG source, PNG and multi-resolution ICO.
- `scripts/Generate-Icon.ps1`: regenerates the icon without external graphics tools.

Run tests with:

```powershell
dotnet run --project Scout.Tests -c Release
```

### Local data

Scout stores application data under `%LOCALAPPDATA%/Scout`. Trial preferences and history live under `UI-Trial`. Set `SCOUT_WORKSPACES` to override the history location.

Guides, results, usage, previews and run events are stored in each run directory, outside the source video folder. New runs copy their saved guide. Do not commit media, transcripts, user reports, model weights, keys, or machine-specific settings.

Scout is licensed under the [MIT License](LICENSE).


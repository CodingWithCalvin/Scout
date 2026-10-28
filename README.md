# Scout

Windows desktop app for identifying TV episodes from video dialogue, reviewing evidence, and generating rename/move previews. Built with C# and WPF on .NET 11 (currently a prerelease SDK pinned in global.json).

## Build and run

On Windows, install the SDK selected by global.json, then run:

```powershell
./build.ps1
./artifacts/win-x64/Scout.exe
```

Pass `-DotnetPath C:\path\to\dotnet.exe` to use a particular SDK installation. The build runs the executable test suite and publishes a self-contained x64 app. End users do not need Python or an installed .NET runtime.

```powershell
./package.ps1
```

Packaging produces a ZIP and SHA-256 file in artifacts/releases. Use `-SkipBuild` only after a successful build.

## Features

- One Scout per source video folder, with independent runs and saved history.
- Full-dialogue matching with a second verification pass and original evidence.
- Exact recording duplicates grouped together; competing recordings held as conflicts.
- Local model endpoints or a managed local runtime; optional cloud providers.
- Encrypted Windows credentials and explicit opt-in for cloud dialogue and downloads.
- Rename/move previews, including separate duplicate and feature handling.
- Draggable results/evidence divider, compact UI, native Windows app icon.
- Run deletion from history with undo; artifacts are retained for recovery.

## Current limits

Source media is never renamed, moved, or deleted. Automatic TVDB/TMDB guide fetching and audio transcription are not implemented. A new show needs an imported JSON episode guide under Settings > Usage and tools > Advanced run data. Embedded text subtitles are supported; saved dialogue can be supplied as an advanced override.

FFmpeg/FFprobe and a compatible llama-server are supplied separately. Experimental model downloads require opt-in; no model is universally approved. Cloud requests require separately configured credentials and can incur provider charges. The ported .NET matcher still needs a fresh end-to-end model accuracy benchmark; fixture tests and imported historical results are not that benchmark.

## Repository

- Scout.Core: media pipeline, matcher, model transport, collection resolution, preview planner, settings and workspace persistence.
- Scout.Desktop: WPF application and Windows integration.
- Scout.Tests: executable regression suite with synthetic fixtures and mocked HTTP.
- Scout.Core/Assets: matcher prompts and JSON schemas.
- Scout.Desktop/Assets: SVG source, PNG and multi-resolution ICO.
- scripts/Generate-Icon.ps1: regenerates the icon without external graphics tools.

Tests run with `dotnet run --project Scout.Tests -c Release`.

## Local data

New installations use %LOCALAPPDATA%/Scout; existing installations retain the legacy Episcout directory for compatibility with saved history and protected keys. Trial preferences/history live under UI-Trial. SCOUT_WORKSPACES overrides the history location; the legacy EPISCOUT_WORKSPACES variable remains supported.

Guides, results, usage, previews and run events are stored in each run directory, outside the source video folder. New runs copy their saved guide. Do not commit media, transcripts, user reports, model weights, keys, or machine-specific settings.

No license has been selected for the project yet.

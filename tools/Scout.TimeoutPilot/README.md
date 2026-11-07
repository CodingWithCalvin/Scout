# First-episode CPU timeout check

This isolated runner references the **published** Scout.Core assembly. It uses Scout's managed CPU runtime, normal non-streaming HTTP requests, production media pipeline, dialogue matcher, evidence validation, independent verification, and collection resolver. It does not edit application settings, saved Scouts, or video files.

The input contains the selected recording, its saved provider guide, and non-secret model settings. This test uses the first recording from the interrupted Victorious Scout, not an inferred episode chosen from its filename. A filename is never substituted for the model's decision.

```powershell
dotnet build tools/Scout.TimeoutPilot/Scout.TimeoutPilot.csproj -c Release
dotnet tools/Scout.TimeoutPilot/bin/Release/net11.0-windows/Scout.TimeoutPilot.dll .build/timeout-validation/input.json .build/timeout-validation
```

The input must explicitly select the managed provider, zero GPU layers, and disabled thinking. The runner verifies the pinned installed model and runtime, starts its own server on an ephemeral loopback port, and stops only that server. Downloads are disabled. All parameters match the input settings except the private port and absolute FFmpeg tool paths. The request timeout comes from `AppSettings.EffectiveTimeoutSeconds` and must be 1,800 seconds for this check. A separate 65-minute test watchdog bounds both matching passes and preparation together.

Results are stored in a separate `test.db`, with a `documents(path, kind, content)` table: `manifest.json`, `status.json`, `heartbeat.json`, per-request original request/response/status entries, `report.json`, and `summary.json`. `results.html` renders the automated outcome. Completed results prevent accidental inference reruns. No source-title corrections or reference labels are injected.

The HTTP recorder buffers the normal response with the request's cancellation token; it does not turn on streaming or change payloads. The manifest records the exact published core hash and runtime/settings. A completed request longer than the original 600-second timeout demonstrates that the old boundary was crossed. A shorter successful run demonstrates normal completion but cannot, by itself, demonstrate behavior past ten minutes. Separate automated tests exercise real HttpClient timeout/cancellation and resumable error checkpoints.

Media size, modification time, and sampled digest are checked before and after; pipeline commands only read the source. A structurally valid, dialogue-supported assignment is a model suggestion, not independently confirmed ground truth.

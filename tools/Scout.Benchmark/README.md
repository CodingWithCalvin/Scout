# Automated local model comparison

This console tool evaluates the current dialogue matcher against the existing 13-case Victorious fixture. It does not edit Scouts or media files. Expected answers and filenames are used only for scoring after inference; predictions are never manually corrected.

Build with `dotnet build tools/Scout.Benchmark -c Release`, then run the built DLL with three arguments: the existing fixture base directory, a benchmark output directory, and a writable model download directory. The fixture base must contain `work/stronger-13/{inputs,catalog}.json`, `outputs/victorious-stronger-13/reference-labels.json`, and `outputs/victorious-verified-13/report.json`. These personal fixtures are not distributed with the repository.

The runner verifies pinned model checksums, downloads missing models and the managed Vulkan runtime, and compares Qwen3.6 35B A3B with thinking disabled/enabled, Qwen3.6 27B with thinking, and Gemma 4 31B with thinking. Each uses 8 GPU layers, 8 CPU threads, a 32768-token context, an 8192-token output allowance, and a 2048-token thinking budget where enabled. It uses its own loopback port 18776 and never stops another server. The existing baseline model is reused from the user's Scout model directory when available.

Checkpoints, evidence, model responses, manifest and progress are stored in `benchmark.db` in the output directory. Run the same command to resume. Completed cases are reused, including failed cases; choose a fresh output directory to repeat them. Identical dialogue is reused within a model arm. `results.html` is updated after each completed case. Model files and runtime binaries remain separate from the database.

Reference agreement is not an independent accuracy measurement: the labels are inherited from earlier evaluation. These 13 selected difficult cases cannot establish general performance on other series. The report also records errors, abstentions, unresolved collection conflicts and elapsed inference time. Models run sequentially; dense models partially offloaded to an 8 GB GPU can take substantial time.

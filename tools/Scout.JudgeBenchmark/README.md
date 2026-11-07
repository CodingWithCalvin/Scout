# Dedicated decision-model evaluation

`decision.py` evaluates Apache-2.0 Kev-4B Q8_0 through llama.cpp's `/v1/systemone` endpoint. It never uses chat completions. Run with three positional arguments: the completed Scout model-comparison database, a new output directory, and the comparison model directory containing Kev and the b11908 Vulkan runtime.

Inputs reuse saved Gemma initial suggestions and full saved dialogue. `judge.build_jobs` constructs a deterministic candidate set with two lexical-neighbor negative controls per distinct dialogue. Reference labels only exclude known positive episodes from the negative controls and score results afterward; they are never sent to the model. Twelve unique dialogues represent thirteen files. No predictions are manually changed.

The state contains timestamped dialogue without speaker attribution. Each candidate is an independent choice among supported, contradicted and insufficient_evidence. Raw responses and probability distributions are retained in SQLite. The predeclared 0.8 supported cutoff is exploratory, not independently calibrated. Two consecutive errors stop the experiment. Existing case checkpoints are reused on restart; changed configurations require a new output directory. The runtime uses its own port and only its own process is terminated.

`judge.py` also contains the superseded chat-model experiment, stopped before completing any evaluations after the user clarified the requirement. That experiment is not the decision-model comparison.

Artifacts: `decision.db` (manifest, fixtures, checkpoints, status and summary) and `results.html`. Original Scout data and videos are read-only. This is an offline developer benchmark, not a Python dependency for the distributed .NET app.

## Expanded saved-result evaluation

`prepare_expanded.py` reads the checkpointed application database without writes, excludes original-test dialogue (normalized text and 5-word-shingle overlap), deduplicates recordings, excludes inconsistent supported labels, and freezes new jobs in `.build/decision-expanded/fixture.db`. Saved suggestions are unverified references, not ground truth. The runner accepts an optional fourth positional argument for a threshold fixed before inference; use `0.61` with a fresh output folder. Do not retune based on these results. Three-class verdicts and raw scores remain unchanged.

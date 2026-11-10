# Offline Unity profiler analysis

This reader opens native `.data` captures in an empty, temporary Unity 2021.3.45f2 project. It does not load the game project or modify the running game. The temporary project is retained for diagnostics. The process exits after export.

```powershell
./Tools/ProfilerAnalysis/Run.ps1 -CaptureDirectory 'E:/Git/GAME-KoboldKare-DevBuild-Opt/Profiler'
```

After export, generate the population comparison and Portuguese report (Python 3.11+, standard library only):

```powershell
python ./Tools/ProfilerAnalysis/Analyze.py 'E:/Git/GAME-KoboldKare-DevBuild-Opt/Profiler/Analysis-20261110'
```

The summarizer expects `0k`, `10k`, `20k`, and `40k` captures/exports. It checks exported means, complete-frame counts, native hierarchy validation results and GC accounting. It writes `report.md`, `comparison.csv`, and `provenance.json`, retaining SHA-256 hashes of the source captures. Files with those names are overwritten on rerun. It does not modify the source captures.

Requires the local Unity Editor and a working license. An explicit `-OutputDirectory` reuses/overwrites exports with the same capture names. A fresh timestamped output folder is the default. Reader limits are set only within its process (2,000 history frames and 1.5 GiB profiler memory); the user's Editor preferences are preserved.

Per capture:

- `*.frames.csv`: complete main-thread frames, selected marker totals/self times/calls/inclusive GC bytes, frame time and available GPU time.
- `*.summary.csv`: every main-thread marker, including absent markers as zero on those frames. Mean, median, p95, p99 and maximum are computed across complete frames. Percentiles use linear interpolation.
- `*.threads.csv`: thread inventory and timing metadata, including incomplete frames. GPU time zero means unavailable in these captures, not zero GPU cost.
- `*.edges.csv`: direct parent attribution for instrumented penetration samples and GC allocation samples. Times and calls are averaged across complete frames.
- `*.validation.txt`: raw traversal totals, self times and call counts checked against Unity's native hierarchy for instrumented markers in the first complete frame (0.001 ms tolerance).

Samples in Unity's raw view are traversed in preorder using direct child counts. Self duration subtracts direct children; GC metadata bytes accumulate from descendants. GC bytes are inclusive, so do not sum parent and child allocation counts. Repeated samples with the same name are summed per frame. Inclusive times of different markers can overlap: do not add a parent and its descendants. Native callback waits/worker synchronization remain included in frame time. A zero-length/incomplete terminal frame is logged and excluded from summaries; nonzero spikes are retained.

For the 60 FPS captures, inspect `WaitForTargetFPS` separately. Subtracting this named wait illustrates headroom, but does not produce pure CPU execution time because other waits remain.

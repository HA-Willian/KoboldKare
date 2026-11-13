# Headless performance regression checks

Run from the repository root with Windows PowerShell when local script execution is enabled:

```powershell
./Tools/PerformanceTests/Run.ps1
```

`-UnityEditor` selects another Editor installation. The default is Unity 2021.3.45f2. The runner uses its bundled Mono and Roslyn, compiles project sources in memory and does not open Unity or download dependencies. `-Baseline` selects another Git revision; the default pins the commit before these changes.

If PowerShell disables script files, the same checks can be invoked directly without changing execution policy:

```powershell
$editorData = 'C:/Program Files/Unity/Hub/Editor/2021.3.45f2/Editor/Data'
$roslyn = "$editorData/MonoBleedingEdge/lib/mono/msbuild/Current/bin/Roslyn"
& "$editorData/MonoBleedingEdge/bin/mono.exe" "$roslyn/csi.exe" "/lib:$editorData/MonoBleedingEdge/lib/mono/4.5" "/r:$roslyn/Microsoft.CodeAnalysis.dll" "/r:$roslyn/Microsoft.CodeAnalysis.CSharp.dll" "/r:$roslyn/System.Collections.Immutable.dll" Tools/PerformanceTests/Regression.csx
```

The differential suite compares the actual reagent, recipe and database implementations and actual extracted character metabolism methods with the baseline. It covers numerical results, JSON persistence, callback order, zero additions, recipe chains, mod replacement/unload, shifted reagent IDs, passive energy, partial consumption, capacity changes, ownership and reentrant callbacks. Additional checks cover buffer lifetime/reuse, all gene snapshot fields, scheduler fairness/budgets, delegate removal, destroyed targets and exceptions. The allocation check measures the isolated buffer operation after warmup.

The adapters stand in for Unity/Photon services and rendering. These checks do **not** validate native renderer state, actual network authority transfer, Unity coroutine lifecycle, build compatibility with every external package or frame-time improvements in the game. Perform the Development Build and Play Mode checks described in the [plan](../../OPTIMIZATION_PLAN.md) before claiming a measured game performance gain.

# OpenNest constructive solver

An independent C++17 solver alongside the existing OpenNest engines. The immediate acceptance target is all 58 boat parts on five sheets, with the original 10 mm clearance and 15 mm sheet margin independently verified. **That target has not yet been achieved by this engine.** Grasshopper integration is deferred until it is.

Two initial 300-second runs, before the newer prefix/pattern search changes, produced independently valid partial layouts:

| Rotation search | Placed on five sheets | Minimum source clearance | Minimum source margin |
| --- | ---: | ---: | ---: |
| Four cardinal angles | 55/58 | 10.7669 mm | 15.0091 mm |
| Eight regular angles plus hull-edge angles | 53/58 | 10.4742 mm | 15.0086 mm |

These are individual bounded runs, not evidence that additional angles are generally worse. Inputs, output CSVs, independent checks, and binary hashes are recorded under `tools/bench3dm/out/constructive_native/baseline_runs_manifest.json`.

The DLL and command-line benchmark perform geometry, search, parallel strategy selection, cached no-fit calculations, and sheet-tail rebuilding in C++. They do not invoke the existing nesting or physics engines. Python utilities only export benchmark geometry and independently validate results.

## Build and test

From the repository root, with Visual Studio 2022 C++ tools and CMake:

```powershell
cmake -S src/nest_constructive_cpp -B src/nest_constructive_cpp/build -A x64
cmake --build src/nest_constructive_cpp/build --config Release --parallel 8
ctest --test-dir src/nest_constructive_cpp/build -C Release --output-on-failure
```

The outputs are `build/Release/nest_constructive.dll` and `build/Release/constructive_bench.exe`. The public ABI is in `include/nest_constructive.h`. Geometry passed to the ABI is already offset for the required spacing; the DLL does not silently add another part offset.

## Benchmark

Export an instance with `tools/bench3dm/export_constructive.py`. Its `--smart` option adds native edge-aligned orientations only where an explicit angle list is absent. `--conservative-proxies` enables containment-verified outer contour approximations for faster no-fit construction. These approximations can miss feasible placements; accepted placements still undergo full material intersection checks. Holes have a separate, bounded contact search.

```powershell
$env:NC_TRACE='1'
src/nest_constructive_cpp/build/Release/constructive_bench.exe input.ncb placements.csv 300 5
python -I tools/bench3dm/validate_constructive_csv.py --input input.ncb --csv placements.csv --out validation
```

Return status zero from the DLL means every requested copy was placed within its input domain. Status one means a partial result. The independent validator must also pass on the original source geometry before claiming a successful benchmark: integer quantization and precomputed offsets can differ from the source outlines.

## Reference provenance

OpenNest Constructive is independently maintained C++ code. Historical analysis, attribution, and documented differences are preserved in the [reference archive](../../research/reference/constructive/README.md). This implementation does not claim original-source or full behavioral equivalence.

Current limitations include bounded cavity placement, heuristic orientation sampling, uniform rectangular stock, approximate bulk contours when enabled, and incomplete benchmark optimization. The historical analysis does not establish equivalent numeric weights or runtime defaults.

Completed-sheet pattern recombination is an independent extension: it combines quantity-compatible sheets from different constructions with a bounded native beam, and offers shorter combinations as prefixes for further construction. Post-compaction refill attempts to use newly opened space on the last mutable sheet. Neither feature is claimed to be recovered original source.

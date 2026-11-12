# Game pack API 1

Controller → Game packs lists bundled and imported packs. Review the description,
license, resource list and integrity hash, set tuning, then **Create setup from pack**.
This creates a new controller setup and pauses sequences; existing setups remain
available. Enable sequences explicitly when ready. Tuning an existing linked setup
also pauses sequences, disarms vision and cancels voice commands. It changes only
the imported scripts' numeric tuning, preserves user mappings, and refuses to overwrite
edited Lua sources. Exported profiles include scripts/tuning and need no installed
pack to replay; re-tuning requires the exact installed manifest hash/version.

Bundled examples:

- [`games/nba2k27/pack.json`](../games/nba2k27/pack.json): optional rhythm Lua/tuning,
  plus the compiled NBA adapter source and offline tools in that folder. No trained
  model is bundled. Existing shot-profile JSON/fingerprints remain compatible.
- [`games/switch-demo/pack.json`](../games/switch-demo/pack.json): game-neutral bumper
  to A-button pulse, with a choice of press durations. It has no vision dependency.

Import selects a file named `pack.json` alongside its declared resources. The host
copies only those resources into local storage, verifies hashes before/after copying,
rejects symbolic links/reparse points and traversal paths, and atomically commits a
new version directory. Existing versions cannot be overwritten with a different
manifest hash. Stop cancels an in-progress import. Importing executes no script and
does not start controller output, capture, inference or recognition.

The exact manifest fields are illustrated by the examples. Schema/API versions must
both equal 1; unknown fields and unsupported versions are rejected. IDs/versions and
resource path segments use lowercase ASCII letters, numbers, dots, `_` and `-`;
they start with a letter or number. Segments cannot be `.`/`..`, Windows reserved
device names, or end in a dot. Asset paths are relative forward-slash paths under
`scripts/` or `models/`. Each asset declares kind, byte count and SHA-256.

| Resource/contract | Limit |
| --- | --- |
| `pack.json` | 128 KiB; JSON nesting 16 |
| Resources | 40; 256 MiB total; no DLL/native/plugin asset kind |
| Lua | 32 scripts; each 128 KiB UTF-8 and 32,768 decoded characters |
| ONNX | 4 models; 128 MiB each; every model needs a declared vision manifest |
| Vision manifests | 4 JSON files, 256 KiB each; model filename/hash must bind a declared ONNX asset |
| Tuning | 32 unique numeric controls; finite values within ±10,000 |
| Presets | 16 named sets; unknown keys and out-of-range/off-step values rejected |
| Library scan | 128 immediate pack directories; hidden import staging directories skipped |

Range controls declare `minimum`, `maximum`, positive `step`, `default`, and empty
`choices`. Choice controls additionally declare unique `{label,value}` choices.
Bindings map a physical `control` to a declared `script`, with optional `consume`.
The UI renders these controls and presets directly from the manifest. Lua reads the
immutable numeric values through `tuning.get("control-id")` and uses the existing
[controller API](lua-scripting.md). It cannot access CLR objects, filesystem,
process/network APIs or controller drivers. Existing instruction/run/state budgets,
physical-input precedence, cancellation and final host output ownership apply.
MoonSharp's instruction budget is not process-level memory or native-code isolation.

`visionAdapter` is `none` or `temporal-release-v1`. The latter is a specific typed
five-value temporal-release observation, not a universal model contract. Optional
vision manifests declare `outputContract: "temporal-release-v1"`; legacy manifests
without the field keep that contract. Unsupported contracts fail closed. Additional
vision tasks need a new reviewed host adapter/API version, rather than arbitrary
float dictionaries interpreted as release commands. Scheduling, freshness checks,
generation/shot tokens and the final arbiter remain in the host; Lua receives no
unrestricted deadline or driver-write interface.

Model resources are optional. **Import model … into Remote Play** uses the existing
model library, compatible profile/camera selection and validation/calibration gates.
It never arms assistance or supplies accuracy evidence. GPU models can consume
substantial resources: import code and weights only from sources you trust. Hashes
establish byte integrity, not publisher authenticity, model correctness, licensing
rights or protection from resource exhaustion. No marketplace or third-party model
distribution is provided by this migration.

Profile format 5 records pack identity/hash/tuning and Lua numeric tuning. Legacy
formats 1–4 still load with their defaults and keep their existing version on save
when no pack/tuning feature requires format 5. Earlier app versions cannot read
format 5; keep an exported original setup if using an older app.

Authors can validate a local folder without loading the desktop app:

```sh
dotnet run --project tools/packs/PackTool.csproj -- games/switch-demo/pack.json
```

Validation parses Lua without executing it and checks declared resource integrity.
Controller verification additionally exercises real Lua output/timing, cancellation,
profile persistence, malformed paths/versions/limits, hashes and link rejection.
Windows CI validates host compilation/runtime with simulated devices. Real Windows
pack import, WebView interaction and target hardware acceptance remain separate.

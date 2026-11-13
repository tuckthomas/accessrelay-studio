# MCP assistant workflows

AccessRelay includes an opt-in MCP server bridge for assistants. Studio provides
tools, context and coaching prompts; it does not include an AI model or hosted
chat service. The initial release supports diagnostics, reviewed controller drafts,
actual-engine Lua rehearsal, dataset audits and bounded CPU experiment jobs.

## Connect

For local Codex and ChatGPT desktop, run `scripts/Install-CodexPlugin.ps1` from
the source checkout after publishing. It installs the AccessRelay Studio plugin
and wires it to the actual installed bridge. The generated marketplace stays
under the project's ignored `artifacts/CodexPluginMarketplace/`; no app binaries
or model weights are duplicated into the plugin cache. If the desktop app was
already open, restart it to load the new plugin. See the
[plugin package](../plugins/accessrelay-studio/README.md) for relocation and scope.

The following client-neutral stdio setup also remains supported:

1. Publish the complete Windows app using the normal `dotnet publish` command.
   Its publish target builds a self-contained console bridge in `Mcp/`.
2. Open Studio → Settings → Assistant access. Select draft/offline permissions as
   needed and enable a 15/30/60 minute access window. Access starts off at launch.
3. Expand **Connect your assistant client** and copy its stdio server configuration
   into an MCP-compatible client. The command names the actual installation's
   `Mcp/AccessRelayStudio.Mcp.exe`; no arguments are required for ordinary use.
4. Ask the assistant to read Studio status or use the `diagnose_studio` prompt.

The bridge uses official C# SDK 2.4.0. Verification covers the SDK's default protocol
and a client explicitly selecting `2025-11-25`. Other clients must support stdio and
the relevant tools/resources/prompts; client support for these features can vary.
The bridge remains usable for discovery/documentation while Studio is unavailable;
live calls return an actionable unavailable/disabled error.

No HTTP listener is opened. The bridge connects to one app endpoint derived from
the Windows account. Windows enforces an owner-only pipe ACL and rejects remote
pipe clients; Unix verification uses current-user-only local pipes. A second app
instance cannot take an endpoint that is already owned. All local clients under
your account share the explicitly enabled authorization window. This is not
isolation from malicious software already running as your Windows user.

Returned configuration, diagnostics and registered-data summaries may be sent to
the external client's AI provider. A local MCP bridge does not imply local AI.
Raw microphone audio, screenshots, gameplay frames and local paths are not exposed
by the tool catalog. Registered input paths and output folders are shown only in
Studio's local Settings panel. Worker logs remain in the chosen output workspace.

## Tools and resources

| Tool | Behavior |
| --- | --- |
| `read_studio_status` | Redacted effective configuration, connection/processing flags and evidence-based blocker codes. Counters describe the current context, not a proven cause of one particular action. |
| `draft_controls` | Propose sticky controls, per-stick elapsed-time smoothing, and optional Lua source/mapping. Returns a pending draft; never applies or activates it. |
| `simulate_lua` | Run the actual Lua engine in an isolated worker with deterministic synthetic physical events. Requires draft permission. |
| `list_training_inputs` | Return opaque IDs for inputs explicitly registered in Settings. Requires offline permission. |
| `audit_dataset` | Start a local audit of registered train/validation sessions using the actual provenance/label validators. |
| `run_vision_experiment` | Start the real CPU experiment matrix. `planOnly=true` is the default; choose 1–10 epochs and a nonnegative seed. |
| `read_training_job` | Read bounded state/aggregate metrics from this access window's job. |
| `cancel_training_job` | Cancel the job and terminate its owned processes. Existing artifacts remain for review. |

Resources are `accessrelay://studio/status`, `accessrelay://studio/capabilities` and
`accessrelay://docs/lua-contract`. The last serves the actual shipped Lua contract,
including `input`, `output`, `wait`, state and instruction limits. Prompts cover
accommodation planning, diagnosis, dataset coverage, personal-sound trials and
experiment review. They do not add capabilities beyond the tools above.

## Draft review and Lua rehearsal

Drafts bind to the exact app instance, controller setup/revision, profile digest
and input generation. At most 16 pending drafts are retained, with a monotonic
15-minute expiry. Revocation/renewal invalidates pending drafts.

In Settings choose **Review draft**. The dialog shows the changes and exact affected
original/proposed values, including Lua source where relevant; unrelated recordings,
timings and scripts are preserved. Apply requires gameplay output/sequences,
capture/voice/recording and startup transitions stopped, with other UI edits saved
or discarded. Controller navigation may stay enabled. Applying resets its input
history, so release the switch before selecting again.

Apply never enables output or sequences. **Undo last assistant change** restores
the original with processing off, provided its setup/input context has not changed.
MCP has no apply, undo, device-start, arbitrary driver-write, capture-start, speech
broadcast, label-edit or model-promotion tool. Studio's existing native activation,
arbitration, freshness, Stop and evidence gates remain authoritative.

Rehearsal uses MacroEngine and its actual Lua implementation, not a parallel set
of invented APIs. Events between 4 ms sample boundaries apply on the next sample.
The report tests specified inputs, manual behavior after Stop and neutral output
after disconnect. A hold at the window boundary is allowed. Trace output is capped
at 512 changes and explicitly marked truncated when needed. `safetyCertified` stays
false: rehearsal is not exhaustive program verification or hardware testing.

The worker has a 10-second wall deadline, 5-second sampled CPU budget, bounded IPC
and 384 MiB working-set monitoring. Windows additionally enforces a process memory
quota and kill-on-close job object. Portable verification uses process monitoring
and cancellation without claiming an OS hard memory quota. Lua's production
instruction/run/state budgets also apply. Worker stdout carries framed results;
SDK logging goes to stderr in the separate console bridge.

## Register and run offline work

Choose dataset directories containing reviewed `session.json` and `labels.json`,
the CPU-target threshold JSON, a trusted existing Python executable and a local
output workspace. Registrations last until Studio closes. Dataset metadata/label
hashes and threshold hashes bind registered inputs; changed inputs require a fresh
registration session. MCP cannot specify arbitrary executables, shell commands,
paths, camera relabeling, output directories or weakened validation thresholds.

The bundled Python sources are the same scripts used by the repository. The app
does not install Python or download dependencies. Audit uses `-S` and the standard
library. Training needs the optional dependencies from the existing
[experiment workflow](vision-model-experiments.md). Inputs must be usable by the
existing [reviewed evidence workflow](vision-model-workflow.md); raw enriched clips
are rejected until reviewed conversion. Held-out data is excluded from auditing/
tuning through these tools.

Audit accepts 1–8 sessions and at most 1 GiB of canonical player-frame data. It
checks the real human-review, timestamp/crop/provenance and exact footage-reuse
guards, and reports descriptive coverage plus corpus identity. It does not certify
the truth of labels or independence after transforms. No labels are written.

Experiment jobs use one seed, two heads × three uncertainty objectives, one CPU
thread, and CPU deployment context. Their fresh output directory is under the
chosen workspace. Plan-only writes real validated commands/corpus identity without
importing ML packages. Execution creates observation-only models and validation
reports. Independent held-out evaluation and actual Windows CPU/CUDA hardware
acceptance remain separate. Reports never promote/arm a model or infer those checks.

At most one job runs at once and 16 jobs are retained per app session. Limits are
10 minutes wall time, 5 minutes sampled CPU, 1.5 GiB sampled memory, 256 MiB sampled
output storage and 4,096 files. Logs retain at most 512 Ki characters per stream.
Windows additionally limits the process family to four processes, 1.5 GiB job memory
and five minutes job CPU, and kills descendants on job closure. Portable tests check
process-tree termination; killed Linux orphans may remain as non-running zombies
until the container init reaps them. Software cannot promise a hard kernel/driver
termination bound. Run offline work while foreground processing is stopped.

Jobs outlive a single tool request and have explicit IDs; inspect status before
retrying an interrupted start. Revoke, expiry, Stop everything or app close cancels
jobs. Foreground controller/capture/voice mutations also cancel offline work, while
ordinary state reads and navigation between pages do not. Completed/cancelled
status is published after owned-process/log cleanup. A foreground action between
the idle check and job admission rejects that pending request, even if there was
no running job to cancel yet. Files are preserved, not erased.

## Architecture and validation

The portable Application library references Engine. React and the SDK bridge call
the same application use cases; the desktop adapter marshals runtime facts and
reviewed commits onto the window's owner thread. Runtime has no Application, MCP or
presentation dependency. Native input/deadline/audio workers never wait for an LLM.
IPC schema 1 uses a bounded length prefix, cancellation on disconnect and typed
version checks. See [desktop architecture](desktop-architecture.md).

Verification exercises actual isolated Lua workers, current/legacy SDK clients,
local IPC, permissions/revocation, stale drafts, apply/undo, real synthetic dataset
audits and experiment plans, source identity, and cancelled process families.
Browser workflows exercise permissions and keyboard-contained draft review.
These are software checks. A real app/client session should still verify private
pipe access, expiry/revoke/Stop, native UI review/undo and selected local Python
operation. Existing screen-reader/device/microphone/gameplay validation remains
in the active accessibility roadmap.

## Expansion directions

Physical calibration needs normalized selected-device samples and comfort/latency
trials. Replay capture needs a separate opt-in bounded recorder. HUD reading needs
explicit image sharing and OCR/multimodal support. Party callouts need local TTS,
audio-owner integration and user-controlled broadcast. Cross-game accommodation
transfer needs semantic action metadata in packs. Local-assistant training needs
opt-in reviewed tool-use demonstrations and an independent evaluation corpus.
None of these is silently implied by the initial MCP tool catalog.

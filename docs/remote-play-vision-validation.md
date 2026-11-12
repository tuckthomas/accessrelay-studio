# Remote Play vision implementation verification

Date: 2026-11-11. Branch: `codex/remote-play-vision`. Application version: `1.9.0-experimental.1`.

## Implemented software

The experimental application includes HWND-based Windows Graphics Capture, bounded reusable latest-frame buffers, a preview, local settings, CUDA ONNX inference, temporal player-region processing, confidence/freshness checks, a single controller-output arbiter, deadline scheduling, manual cancellation, release latching, diagnostics and reviewed calibration import. Capture and inference run outside the controller-output lock. Stop, disconnect, source changes and profile changes invalidate pending work. Configuration import never starts capture or arms output.

The NBA 2K27 plugin supports saved profile creation, editing, reverting, duplication, selection and deletion. Stored fields include height, three-point rating, base, upper release 1, upper release 2, meter mode, meter-specific release type, four speed levels and complementary upper-release percentages. Gameplay configuration changes invalidate the model/calibration association.

Training controls record bounded local cropped takes with timestamps and full shot-profile provenance. Recording inhibits automatic release at the output arbiter. The review dialog supports frame scrubbing, reviewed target/negative intervals and session-level train/validation/held-out splits. Offline Python scripts provide training, ONNX export and independent evaluation; no Python worker is used by the running application.

## Completed checks

| Check | Result and scope |
| --- | --- |
| Portable controller/profile verification | 25 groups passed, including deadline math, ownership, manual cancellation, generations, freshness, training inhibit, profile CRUD/hash changes and existing Lua/macro/recording behavior. |
| Windows capture and dataset verification | Passed real synthetic-window capture/readback, QPC timestamps, resize, minimize/restore and closure; reusable-buffer ownership, row pitch, latest-frame replacement; dataset pixels, offsets, full profile provenance, incomplete takes, metadata/write failures and independent timeout. |
| Runtime training transitions | Actual runtime/arbiter with isolated test devices passed manual passthrough, script/profile/recording guards, permitted stop/disable paths, no automatic resumption, and 20 competing-entry races each for script enabling and macro recording. |
| Portable model verification | Seven checks passed for preprocessing, manifest/evidence integrity, output validation, temporal tokens, calibration, bounded diagnostics and frozen-player-region cancellation despite changing surrounding pixels. |
| C#/offline preprocessing parity | Irregular gradient, noninteger crop and 37×43 output matched with maximum absolute difference zero in the exercised fixture. |
| Actual RTX 2070 CUDA compatibility | Strict CUDA-only diagnostic model load, warm-up and 17 observations passed; missing held-out/hardware evidence continued to prevent arming. Tiny random diagnostic weights are not a gameplay speed or accuracy benchmark. |
| Offline workflow smoke check | Synthetic one-epoch training, ONNX export/structural checking and independent CPU evaluation passed. The report remained hardware-unvalidated. |
| Offline label integrity | Six checks passed for valid disjoint intervals, overlapping/nested intervals, shared endpoints, incompatible timing bases and unreviewed labels. |
| Application build and UI | Release build/publish passed with zero warnings/errors. The running WinUI app showed the new shot attributes; 35% release 1 updated release 2 to 65%, meter-on enabled release type, and reverting restored the draft. Capture-only preview displayed frames from a selected local app, remained disarmed, and stopped successfully. No Xbox session or virtual-controller output was exercised by this UI check. |

Build outputs, synthetic fixtures, NVIDIA runtime redistributables and Python dependencies were placed outside the repository on C:. Raw frames, trained weights and dependency binaries are excluded from Git.

## Remaining acceptance gates

No real gameplay training footage or trained no-meter NBA 2K27 model was supplied. Actual gameplay collection, justified target-phase annotation, model training, disjoint held-out evaluation, live calibration and Xbox Remote Play virtual-controller/release trials remain pending. The starter model architecture and manually selected player crop do not establish full-court player tracking or reliable shot timing.

Local timestamps measure PC capture, processing and controller handoff. They do not directly measure the Xbox render timestamp or when Xbox applies an input. GC collection counts are diagnostic correlations, not measured GC pause lengths. Windows scheduling and Remote Play transport remain variable; deadline misses are skipped rather than represented as timely releases. A fresh-looking changing frame is not proof of low upstream streaming delay.

The application deliberately remains observation-only for models without matching reviewed held-out and hardware validation evidence. Hashes ensure file/configuration consistency; they cannot certify the truth of manually authored reports. See [the model and dataset workflow](vision-model-workflow.md) and [the implementation plan](remote-play-vision-implementation-plan.md).

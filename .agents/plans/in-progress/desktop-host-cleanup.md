# Desktop host and engine separation

Approved: 2026-11-12. Keep React as the application UI and WinUI as the small native
window/WebView2 host. Remove the inactive native application screens and enforce
engine/presentation separation through real project references.

## 1. Plan organization

- [x] Add `completed` and `in-progress` directories under `.agents/plans`.
- [x] Move the existing accessibility roadmap into `in-progress`, preserving history.
- [x] Record the cleanup checklist before implementation and push it to `main`.

Plan organization and initial checklist: `74cda5a6fc56f3c404610648d75985c297a19038`.

## 2. Remove the inactive native screens

- [x] Confirm the active launch path and references before deleting legacy UI.
- [x] Remove the unused MainWindow, native panels/editors and their UI-only helpers.
- [x] Remove unused native resource definitions; retain the current shell, loading,
      recovery and emergency-stop controls, React assets and 3D controller viewer.
- [x] Record the implementation commit and validation evidence.

Implementation: `2fae5d685d939ee1cfba27c22933a6d153c14004`. The app launch path
constructs only DesktopWindow; references to the removed screen/helper types stayed
inside the legacy MainWindow tree. Removed 24 inactive UI files and their unused
brush resources (9,152 lines). App.xaml and DesktopWindow.xaml are the only remaining
XAML sources. React, the 3D viewer, native loading/retry/emergency-stop and shutdown
logic remain in the active host. Windows publishing and the full existing CI passed:
https://github.com/tuckthomas/accessrelay-studio/actions/runs/43303780187.

## 3. Enforce project boundaries

- [x] Put portable Core logic in an engine library with no Windows or UI dependencies.
- [x] Put Windows controller/audio/vision workers and built-in NBA vision adapter in a
      runtime library referencing the engine, without a WinUI/presentation dependency.
- [x] Make the WinUI app explicitly compile only its shell/presentation sources and
      reference the runtime; preserve deployment of native ONNX and helper assets.
- [x] Migrate applicable verification/tools to real library references and actual
      runtime contracts; preserve portable CPU-only vision checks.
- [x] Document dependency direction and validate it in CI.
- [x] Record implementation commits and validation evidence.

Implementation: `5b699a2788a9734cd5416e2a6dc6eaea65346365`. Added Engine (plain
net10.0 Core) and Runtime (Windows workers, no WinUI) libraries. The app compiles
only shell/presentation sources. Named friend assemblies retain internal contracts
without widening the public API. Worker asset paths/copy rules remain stable.
Verification and author tools reference the engine; Windows verification references
the actual runtime and implements real contracts with FakeControllerInput and
FakeControllerOutput. The input probe also references the runtime. Added compiled
metadata boundary inspection and published-asset checks to Windows CI.
See `docs/desktop-architecture.md` for the dependency graph and linked-source layout.

## 4. Verify and finish

- [x] Run portable engine, vision and desktop-evidence verification, actual runtime
      checks with simulated devices, input-probe compilation and frontend checks.

Local validation: portable engine, 10 managed vision/model/performance suites,
desktop evidence checks, pack author CLI, actual compiled runtime with simulated
devices, and input-probe compilation passed. The metadata checker passed for the
compiled Engine/Runtime assemblies. 57 frontend unit tests, 20 Chromium browser
workflows, TypeScript and Vite production build passed. Published host metadata and
Windows-native deployment checks are pending the new CI run; no physical-device
or screen-reader listening result is inferred from these checks.
- [ ] Pass Windows publishing and CI against the separated projects.
- [ ] Record full implementation commit IDs, update the active accessibility roadmap
      where needed, and move this cleanup plan to `completed`.

Physical-device, microphone, user and gameplay acceptance remains tracked separately
in `accessibility-platform-roadmap.md`; this cleanup does not manufacture that evidence.

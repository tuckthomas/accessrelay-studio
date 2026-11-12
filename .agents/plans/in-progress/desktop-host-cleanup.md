# Desktop host and engine separation

Approved: 2026-11-12. Keep React as the application UI and WinUI as the small native
window/WebView2 host. Remove the inactive native application screens and enforce
engine/presentation separation through real project references.

## 1. Plan organization

- [x] Add `completed` and `in-progress` directories under `.agents/plans`.
- [x] Move the existing accessibility roadmap into `in-progress`, preserving history.
- [x] Record the cleanup checklist before implementation and push it to `main`.

## 2. Remove the inactive native screens

- [ ] Confirm the active launch path and references before deleting legacy UI.
- [ ] Remove the unused MainWindow, native panels/editors and their UI-only helpers.
- [ ] Remove unused native resource definitions; retain the current shell, loading,
      recovery and emergency-stop controls, React assets and 3D controller viewer.
- [ ] Record the implementation commit and validation evidence.

## 3. Enforce project boundaries

- [ ] Put portable Core logic in an engine library with no Windows or UI dependencies.
- [ ] Put Windows controller/audio/vision workers and built-in NBA vision adapter in a
      runtime library referencing the engine, without a WinUI/presentation dependency.
- [ ] Make the WinUI app explicitly compile only its shell/presentation sources and
      reference the runtime; preserve deployment of native ONNX and helper assets.
- [ ] Migrate applicable verification/tools to real library references and actual
      runtime contracts; preserve portable CPU-only vision checks.
- [ ] Document dependency direction and validate it in CI.
- [ ] Record implementation commits and validation evidence.

## 4. Verify and finish

- [ ] Run portable engine, vision and desktop-evidence verification, actual runtime
      checks with simulated devices, input-probe compilation and frontend checks.
- [ ] Pass Windows publishing and CI against the separated projects.
- [ ] Record full implementation commit IDs, update the active accessibility roadmap
      where needed, and move this cleanup plan to `completed`.

Physical-device, microphone, user and gameplay acceptance remains tracked separately
in `accessibility-platform-roadmap.md`; this cleanup does not manufacture that evidence.

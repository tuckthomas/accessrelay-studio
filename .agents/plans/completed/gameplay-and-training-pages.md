# Gameplay and training pages

Delivered in 1.9.0-beta.13 (2026-11-12).

- Live Gameplay: window selection, live preview, model selection, assistance and timing diagnostics.
- Model Training: training-clip recording, capture areas, clip review and offline dataset/job management.
- NBA-specific profiles, HUD regions and release controls remain in the bundled NBA plugin adapter. The platform live page can capture other games without that plugin.
- Detach opens one independent native window, movable between monitors. Bring back or closing that window returns the preview to the main app without stopping the session.
- Both windows share the existing capture/inference service and WebView2 environment. The inline image is hidden while detached; no second capture or model worker starts.
- Preview presentation stays active while the detached window is visible, including while the main app is on another page or minimized. Minimizing the viewer pauses preview presentation without stopping the live session.
- Explicit app closure now follows the same cleanup path as the native close gesture and closes the detached window before disposing its session.
- Shared controls, light/dark themes, unsaved-work guards and explicit processing activation remain in use.

Validation:

- UI production build and Windows Release build passed.
- 38 focused checks passed for page separation, plugin controls, recording/review, unsaved edits and shared controls.
- `tools/desktop/Verify-GameplayWindow.cjs` passed against the native app with isolated data and an animated synthetic capture source: one detached viewer, restricted bridge, theme synchronization, real-frame decoding away from Live Gameplay, reattachment without stopping capture, explicit stop and shutdown cleanup.
- No trained-model accuracy, gameplay timing or physical controller acceptance is claimed by these UI checks.

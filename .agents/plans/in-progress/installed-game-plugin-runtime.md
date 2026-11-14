# Installed game implementation ownership

Requested 2026-11-14. The previous Marketplace training package was an enabling
manifest for NBA code/UI bundled in Studio. That does not meet the requested
separation. This work corrects implementation ownership, not merely folder names.

- [ ] Keep Studio's engine, capture, inference, recording/review transport, device
      ownership, plugin contracts/loader and shared UI components game-agnostic.
- [ ] Move NBA profiles, tracking/court/HUD rules, controller feedback, interface,
      training tools, capture-model declarations and game-specific checks to the
      separate Marketplace repository as an actual versioned plugin implementation.
- [ ] Load the installed plugin's checked runtime and UI resources through a
      generic package contract. No hardcoded NBA factory or UI import remains.
      UI plugins use the host's React instance and shared controls/theme tokens.
- [ ] Keep existing controller/manual actions and processing activation explicit.
      A native extension is trusted local code, not a Lua sandbox. Preserve scoped
      paths, resource hashes and compatibility checks; describe its runtime in
      package inspection before installation.
- [ ] Preserve existing saved profiles, capture configuration, recordings and
      dataset provenance. Keep schema-1 script-only packs readable.
- [x] Make the review timeline render during dragging, coalescing to the latest
      requested frame without a release-only debounce or an image-request backlog.
      Label controls remain bound to the loaded frame.
- [ ] Verify the affected scrubber, package boundaries, actual plugin build/load
      and source/resource hashes. Build/install the update, push both main branches,
      and report concrete remote commits and any remaining validation limits.

The existing app commit `ed62822` was confirmed on GitHub main; the seven-hour
timestamp in the supplied image is the file's own last modification, not a push
time. The Marketplace implementation gap was real despite both trees being synced.
No trained release model or new physical gameplay acceptance is claimed here.

Focused scrubber verification: 3 tests passed (continuous dragging, one image in
flight with latest-position coalescing/error recovery, and loaded-frame labeling).


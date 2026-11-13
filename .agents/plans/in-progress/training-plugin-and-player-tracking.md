# Installable training plugins and NBA 2K27 tracking

Requested 2026-11-13. This supplements the platform roadmap; it does not mark the
NBA model or physical validation complete.

## Plugin boundary

- [x] Separate Marketplace types: game scripts, controller profiles, game plugins,
      model training plugins. Filter by type and game.
- [x] Publish a distinct NBA 2K27 Model Training package in the Marketplace repo.
      Installing its controller script does not install training.
- [x] Only installed training packages enable their reviewed adapter in Model
      Training. Keep NBA profiles, meter/cue/camera rules and evidence stream
      selection in `games/nba2k27` and its UI plugin.
- [x] Keep the host responsible for capture, storage, scheduling, device ownership,
      model execution and generic plugin selection. Downloaded manifests cannot
      execute arbitrary DLLs, Python or browser code.
- [ ] Validate installation and selection in the installed desktop app.

## Acquisition correction

- [x] New NBA capture layouts retain the full gameplay scene and fixed shot feedback
      instead of marking stationary player/meter crops. Keep court context AND the
      image above the visible floor so raised hands and the ball remain present.
- [x] Keep synchronized raw controller trajectory and capture timestamps. Record
      scene dimensions and mark the scene explicitly as untracked acquisition data.
      Bound scene dimensions to 1280 per axis and visual storage to 4 GiB/10 seconds.
- [x] Keep old fixed-region recordings readable. Reject scene overviews as player
      crops in release-model conversion and live analysis.
- [ ] Record and review actual new scene-first footage, including meter/feedback
      legibility and disk/recording overhead. No such recording has been made yet.

## Dynamic player/meter pipeline (not implemented or validated)

- [ ] Annotate court floor, intended player identity, body/keypoints, ground
      projection and meter across representative gameplay. Include negatives with
      spectators, teammates, opponents, occlusions, camera cuts and missing meters.
- [ ] Train/validate player detection and identity association using scene context.
      Gamertag/indicator cues may help acquisition; neither repeated full-frame OCR
      nor a fixed tag offset is the only identity source.
- [ ] Use the court plane/polygon as a ground-position prior. Associate a player's
      ground projection with the court, preserving it during jumps. Do not require
      every body/hand pixel to lie inside the floor polygon, and do not black out
      everything above the sideline. Camera motion changes the screen-space court
      projection; re-estimate it rather than treating an initial outline as static.
- [ ] Generate each frame's player crop from the tracked body, hands and ball,
      with scale/motion padding in normalized source coordinates. No hardcoded
      160-pixel box; actual capture resolution and on-screen player scale matter.
- [ ] Associate/detect the meter near that player and follow its actual bounds.
      Keep shot feedback in its fixed HUD region, independent of player motion.
- [ ] Save per-frame identity, player/meter bounds, court estimate, confidence,
      source sequence, timestamps and failure reason for review and conversion.
- [ ] Invalidate tracking on ambiguous identity, cuts, stale/missing detections,
      source changes or loss of confidence. Reacquire before assistance; never
      silently select a fan or another player.
- [ ] Validate moving and stationary attempts, camera height/zoom, 1080p/1440p,
      occlusions and raised hands. Measure total processing p95/p99 with Remote
      Play, recording and preview. A stationary crop test is not this acceptance.

The installed training plugin initially collects the data needed for this tracker.
It does not contain a trained tracker or release model. Scene-first acquisition
must not be described as working automatic tracking or successful shot timing.

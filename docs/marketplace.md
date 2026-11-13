# Marketplace

The separate [accessrelay-marketplace repository](https://github.com/tuckthomas/accessrelay-marketplace)
owns listings, versioned packages, source, contribution review and licenses.
Marketplace filters distinguish game scripts, controller profiles, game plugins and
model training plugins. Listings also identify the game when applicable.

**NBA 2K27 Model Training** is separate from **NBA 2K27 Rhythm Script**. Install the
training package, then select NBA 2K27 under **Model Training → Training plugin**, or
choose **Open training** on the installed listing. A controller package cannot
activate a training adapter. Installation never starts capture or controller output.

A training manifest declares `gameId` and `trainingAdapter`. Installed manifests
enable reviewed adapters supported by this Studio build; the host does not execute
downloaded DLLs, React or Python. Unknown adapters cannot be installed through the
training Marketplace workflow. Adding a new native adapter requires a host release.
NBA profile fields, cue/meter rules, camera options, UI and evidence-stream selection
belong to the NBA plugin. Generic capture, storage, models, timing, scheduling and
device ownership belong to the host.

Packages install under `InstalledPacks` beside the app, on the installation drive.
The consumer checks catalog metadata, manifest identity/SHA-256, declared resource
sizes/hashes, bounded HTTPS downloads, sandbox compilation and model declarations.
Controller content appears under **Controller → Plugins**; training-only packages
do not appear there. Published versioned package bytes remain immutable.

The NBA training plugin contains no trained tracker or release model. New acquisition
retains the full gameplay scene and fixed feedback panel with synchronized controller
input. Scene overviews cannot be passed off as player crops. See
[the tracking plan](../.agents/plans/in-progress/training-plugin-and-player-tracking.md)
for court/identity association and moving player/meter requirements.

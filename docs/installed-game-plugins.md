# Installed game implementations

Studio beta.28 loads game implementations from installed Marketplace packages.
Studio owns the shared C# API, loader, capture/recording/review transport, inference
services, controller ownership and shared UI. A game owns its profiles, camera,
HUD/court/identity rules, tuning interface and offline training tools. No game
factory, UI import or game-source project reference is bundled into Studio.

Schema/API 1 Lua/controller packages remain supported. Schema/API 2 adds a
`runtime` declaration: assembly, `IGamePluginFactory` type, UI module, training
entry, optional audit/experiment entries and capture-model directory. Every DLL,
JavaScript module, Python file, model and notice has a checked path, size and SHA-256.
Unknown versions, path escapes, links, undeclared entry points and changed resources
are rejected. Changed packages require a new version.

Implementation plugins are **trusted installed code with the user's permissions**.
The .NET loader and React host API are not a sandbox. Marketplace identifies these
packages with `nativeCode: true` and explains the runtime in package details.
Hashes establish integrity, not publisher authenticity. Schema-1 Lua restrictions
do not apply to schema-2 .NET/UI/Python code.

The custom assembly load context shares Studio's contract assembly. UI modules
receive Studio's React singleton, shared controls, theme and gameplay preview;
they bundle no duplicate React runtime or controls. Components receive command/state
props; capture and output start through the existing explicit application actions.

Checked UI resources use
`https://plugins.accessrelay.test/plugins/<package>/<manifest-hash>/...` with CORS
limited to `https://studio.local`. This is intercepted local content, not a public
server. The origin is unmapped because WebView2 does not raise `WebResourceRequested`
for virtual-host folder mappings.
See [Microsoft's request-interception documentation](https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/webresourcerequested).

Model Training owns the gameplay-window selector and data/output folders. An
installed plugin supplies the game's workspace. Training scripts resolve from its
package and run offline; live capture remains native C#. Installation starts no
recording, inference or controller output.

The NBA implementation is maintained in
[accessrelay-marketplace](https://github.com/tuckthomas/accessrelay-marketplace/tree/main/plugins/nba2k27).
Migration preserves the existing per-user NBA profile store, IDs, configuration
fingerprints, capture settings, original clips and labels. Earlier enabling-only
manifests no longer expose a built-in implementation.

Clip scrubbing updates while dragging, keeps one image request in flight and
coalesces to the latest thumb position. Label buttons require the selected image to
finish loading. This changes review behavior, not recorded evidence or capture FPS.

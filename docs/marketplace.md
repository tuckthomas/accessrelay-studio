# Marketplace

The app's Marketplace page reads the catalog in the separate public
[accessrelay-marketplace repository](https://github.com/tuckthomas/accessrelay-marketplace).
That repository owns package source, listings, contribution review and licenses.
No community package is bundled into the app repository. Its initial two listings
are clearly attributed AccessRelay examples, not a claim of an existing community.

Marketplace provides search, script/plugin filters, source links, explicit install,
refresh and installed status. Opening the page reads the catalog; it does not
activate scripts or controller output. Installed content appears under Controller
→ Plugins, where the existing pack workflow creates a separate controller profile.

The consumer accepts catalog schema 1 and the existing game-pack API 1. It checks
catalog paths/metadata, manifest identity/SHA-256, declared asset sizes/hashes,
resource types, Lua sandbox compilation and typed vision manifests. Downloads are
bounded, use the repository's fixed HTTPS content origin without redirects, and
stage under the installed app's MarketplaceCache before validated library import.
The package API supports Lua, declarative tuning and supported vision resources;
it does not load arbitrary native plugin DLLs or downloaded React UI.

Game Vision is the renamed Remote Play workspace: it captures an existing game
window for training evidence and live AI assistance, rather than hosting Xbox
Remote Play. Capture/training, clip review and live assistance have distinct tabs
inside the selected game plugin.

The UI follows the reusable primitive/composition pattern in the user's CRM
component library. Every app/game-plugin screen uses shared controls from `ui.tsx`,
with form primitives in `components/ui/`. Dropdowns share rounded theme tokens,
keyboard/typeahead selection, disabled items, adaptive placement/scrolling and
dialog-aware portals. ProfilePicker keeps selectors and create buttons aligned.
The UI AGENTS.md and a source guard preserve this app-wide requirement.

Focused checks cover catalog downloads, real example-package imports into an
isolated library, inactive profiles, altered manifest/path/size rejection, affected
UI workflows, Marketplace filtering/install requests, shared-control usage, profile
alignment, upward dropdown positioning, keyboard focus return and dark-mode
dropdowns inside native dialogs.

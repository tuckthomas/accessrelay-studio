# AccessRelay Studio desktop plugin

This plugin connects local Codex and ChatGPT desktop chats to the MCP server
shipped with AccessRelay Studio. It uses stdio and the app's current-user Windows
pipe. It does not need an OpenAI API key or an internet-facing app server.

From the repository, register the installed app and install the plugin:

```powershell
./scripts/Install-CodexPlugin.ps1
```

Use `-InstallDirectory` for a different Studio installation. The script materializes
the portable and compatibility manifests with the actual MCP executable path under
the project's ignored `artifacts/CodexPluginMarketplace/` folder, then registers and
installs `accessrelay-studio@accessrelay-local` through the Codex CLI. App binaries
and voice/model assets stay in their existing installation; they are not copied
into the plugin cache. Rerun the installer if the app moves. Existing unrelated
plugins, MCP servers and personal marketplace entries are preserved.

Restart the ChatGPT desktop app if it has not loaded the new local plugin. In
Studio, enable Settings → Assistant access for the requested access window. That
app-owned session setting starts off at launch. Tool discovery and the Lua/capability
resources work while access is off; live status, drafts and jobs require access.
The plugin cannot grant itself permissions, activate hardware, apply drafts or
promote models. Enable draft/offline permissions only for the workflows requested.

Ask Codex to read Studio status. Additional tools support reviewed controller drafts,
production-engine Lua rehearsal, registered dataset audits and bounded offline jobs.
See [the assistant guide](https://github.com/tuckthomas/accessrelay-studio/blob/main/docs/mcp-assistant.md) for the actual tool contract.

This is a local desktop installation, not publication to OpenAI's public directory.
ChatGPT web requires a separately reachable remote MCP connection; this local plugin
does not expose the Windows app to the internet.

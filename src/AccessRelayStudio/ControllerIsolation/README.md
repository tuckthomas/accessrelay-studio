# Controller isolation

HidHide is intended to keep a physical controller visible to Studio while blocking
the physical controller's direct path to Remote Play. This requires verification
in the destination application; installation and configuration alone are insufficient.
The signed upstream installer and license are bundled in `Package/`.

Install the client tools into this project's `ControllerIsolation/HidHide/`
directory. This is a project-local client installation, not a portable driver:
Windows still registers a system filter driver. Installation requires Windows
administrator approval and acceptance of the installer's license terms.

Build and publish copy this directory and `Set-ControllerIsolation.ps1` into
the runnable application's `ControllerIsolation/` directory. The installer is
explicitly allowed by `.gitignore`, and binary attributes preserve its signature.

Use a separately prepared machine-specific plan with exact controller instance
paths, its verified container ID, the Studio executable and `installDirectory`.
Do not commit a user's plan or treat one machine's device paths as universal.
The script defaults to read-only `Inspect`. `Apply` adds only the planned entries
and writes `isolation-before.json` next to the plan. `Restore` removes only entries
it added. Existing unrelated HidHide settings are preserved.

After applying, restart the controller device stack and reopen the controller
readers. Verify Studio receives fresh physical movement and a non-allowlisted
reader sees only the virtual controller. Then stop Studio's virtual output and
press a physical button in the destination application. It must not respond.
An XInput-only check does not establish isolation for another input API or an
existing service connection. On the development machine, Xbox still accepted a
physical A press after XInput hiding worked; that configuration was not accepted
as isolated. Recheck the destination after reconnecting its input readers.
If Studio loses physical input, restore before proceeding.

Sources: https://docs.nefarius.at/projects/HidHide/CLI-Usage/
and https://github.com/nefarius/HidHide/releases/tag/v1.5.230.0

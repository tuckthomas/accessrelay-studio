# Windows physical and data acceptance

The roadmap's browser, portable and simulated-device checks do not demonstrate
physical actuation, microphone recognition, user comfort or gameplay model quality.
Generate a local journal against the published Windows build:

```powershell
./scripts/Start-AccessibilityAcceptance.ps1 `
  -AppPath C:/AccessRelayStudio/AccessRelayStudio.exe `
  -OutDirectory C:/Acceptance/accessrelay-first-run `
  -Tester 'Your name'
```

Optionally pass `-InputProbePath` with the built `EliteInputProbe.exe` to save
read-only physical discovery in `physical-input.json` and `input-probe.log`.
The JSON exports hashed device identifiers; names, transport information and raw
device paths in the diagnostic log can still identify local devices. The probe
never requests rumble or virtual controller output.
Its existing conversion/filter checks use simulated input events and cannot prove
physical controls or motors work. A failed probe is recorded as a collection issue.

The journal records the app hash/version and available Windows graphics, audio and
speech inventories. These inventories describe discovery, not capability proof.
The executable hash/version identifies the supplied file; it does not authenticate
a publisher or verify which executable is currently running. `identityValidated`
remains false.
The generator does not launch the app, actuate devices, record audio or upload files.
It refuses an existing output directory. `-TemplateOnly` also works outside Windows
but collects no device inventories and does not validate an app identity.

All 20 cases in `cases.csv` begin **Pending**. They cover modal/screen-reader use,
sticky controls, smoothing, switch navigation, physical cues, co-pilot, speech and
personalized vocalizations, pack integration, reviewed gameplay data and runtime
profiling. Before a case, specify the intended user/device/task and measurable pass
criteria. Record the active app commit, drivers, source/settings and model hashes
with evidence. Mark each case Passed, Failed or Blocked and attach measured results
or recordings; an API's presence or a passing CI job cannot fill these results.

Use [controller accessibility](controller-accessibility.md),
[voice commands](voice-commands.md), [game packs](game-packs.md),
[reviewed evidence conversion](vision-model-workflow.md),
[model experiments](vision-model-experiments.md) and
[runtime profiling](runtime-profiling.md) for the corresponding procedures and limits.
Real comparisons require reviewed, disjoint gameplay sessions; synthetic fixtures
exercise the pipeline but cannot establish accuracy or choose a model.

Keep the journal and sensitive audio/gameplay evidence local unless you choose to
share them. Review evidence explicitly before marking roadmap checks complete.
The generator leaves `hardwareAcceptanceComplete` false and never edits the roadmap.

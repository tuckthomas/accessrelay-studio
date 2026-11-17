# Native Xbox stream telemetry: investigation and discovery probe

## Findings

The absence of a public cross-process API does not establish that OCR is necessary.
There are concrete internal SDK and ETW paths to investigate first.

1. [OpenXbox's recovered native SDK declarations](https://github.com/OpenXbox/xcloud-python/issues/7)
   expose `StreamSession.StatisticsChanged`,
   `StreamSessionStatisticsChangedEventArgs.GetStatisticsJson()` and
   `StreamSession.ToggleDisplayPerformanceOverlay()` on the session interface.
   The declarations were published in November 2020. They establish an internal
   structured statistics callback, not a public way to obtain another process's
   active session object or the contents/units of its JSON.
2. [An independent Xbox package analysis](https://github.com/slaveitgr/xboks/blob/1bfb8fb27889e069b7db1c25bb99f6f5741a8507/docs/analysis/reference/native-contracts.md)
   reports session statistics/quality events in `gamestreaming_sdk.winmd` from
   Xbox PC package **2610.1001.20.0**. This corroborates the native SDK boundary in
   a more recent package, but was static analysis, not an executed Windows test.
3. [OpenXbox's Windows client tracing instructions](https://github.com/OpenXbox/xcloud-tools/blob/9c0c5749263e2140af03d50622dcc811932da087/README.md)
   identify native streaming ETW providers, with code to decode their events.
   Those instructions target the older Content Test App. Their continued use by
   the current Xbox PC app, emitted fields and units require a live check.
4. [A native Xbox PC Remote Play file-activity report](https://github.com/kaczorws/xboxappmod/issues/16#issuecomment-978019619)
   observed `Microsoft.GamingApp_8wekyb3d8bbwe/LocalState/gamestreaming_log_0.txt`
   changing during Series console streaming in November 2021. It does not show
   the log's contents or establish that all overlay values are written there.

Windows [ETW consumers](https://learn.microsoft.com/en-us/windows/win32/etw/about-event-tracing)
can receive application events in a separate process in real time. This is the
first candidate for observing an existing native Xbox session. It requires no
new media connection. Enabling a provider successfully does not prove that it
is present, emits events in the selected process, or exports the required metrics.
TraceLogging providers can be enabled by GUID; absence from a registered-provider
listing alone is not a conclusive test.

The SDK callback is another candidate if ETW lacks the relevant values. Merely
loading `gamestreaming_sdk.dll` or creating a new StreamClient in Studio would
not supply the Xbox app's existing StreamSession. A version-specific bridge to
that callback would need separate investigation. No such bridge is implemented.

Some statistics originate in local receive/decoder processing. Inspecting network
packets alone would not establish the Xbox app's actual decode duration. Provider
or callback statistics are the appropriate measurement boundary for that value.

## Standalone discovery probe

`tools/vision/StreamTelemetryProbe` is an experimental Windows ETW consumer for
the following historical provider identifiers:

| Provider | GUID |
| --- | --- |
| Microsoft.Streaming.Nano.Network | 3f3c85cb-a328-4467-96df-438d76b4ac59 |
| Microsoft.Streaming.Nano.Network.urcp | e027bcec-71f3-4b85-ae28-90ff7446b675 |
| Microsoft.Streaming.Nano.Media | cb8b7862-eae7-4f30-9574-9453076ebaab |
| Microsoft.Streaming.Nano | d20f61e9-8929-4282-9b24-b07db5a27a76 |
| Microsoft.Streaming.Basix | afc8a033-e28a-415c-acff-030e4b113a35 |

It selects running XboxPcApp/ContentTestApp processes, or explicitly supplied PIDs.
It records relevant loaded module names/versions, streaming log file sizes and
modification times, event schemas, and sampled numeric fields with metric-like
names. Log contents and arbitrary event strings are not retained. Account/session identifiers,
credentials and addresses are filtered. JSON-shaped fields are inspected for
numeric counters only; malformed input and nonfinite numbers are discarded.

Capture lasts 30 seconds by default (maximum 120), stops on Ctrl+C, and disposes
only its own uniquely named ETW session. Event-schema and sampling limits bound
output; the default file has a unique name and existing files are never replaced.
The summary records missing events and omitted output. Use a writable directory.

With native Remote Play and its statistics overlay running, in an elevated
Windows terminal with .NET 10:

```powershell
dotnet run --project tools/vision/StreamTelemetryProbe -c Release -- --seconds 30 --verbose
# A rendering/helper process can be selected explicitly if needed:
dotnet run --project tools/vision/StreamTelemetryProbe -c Release -- --pid 1234 --seconds 30 --verbose
```

The tool creates a JSONL report next to the command's working directory. It does
not obtain credentials, start or replace a game stream, modify the Xbox package,
or inject code. The provider session requires elevation; the probe does not
request it automatically or change system logging configuration persistently.

Its portable filtering check is:

```sh
dotnet run --project tools/vision/StreamTelemetryProbe -c Release -- --self-test
```

## Integration gate

This probe is **not** a native telemetry backend for Studio. Nothing it observes
changes output, calibration or timing adaptation. The existing OCR reader remains
the implemented fallback while this route is tested.

Before introducing a native backend, verify the installed app/module versions,
active streaming PID(s), event cadence, field names and units against the overlay.
Establish whether counters are cumulative, instantaneous or interval averages;
reject stale samples, account for resets and trace loss, and correlate source
timestamps with Studio's shot clock. Missing fields remain unavailable. Only then
can a provider map into `StreamTelemetryReading` and take precedence over OCR.

The current environment can compile this tool and run its filtering checks. It
cannot verify native Xbox ETW publication or the live JSON statistics schema.

#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [Parameter(Mandatory = $true)][string]$OutDirectory,
    [string]$Tester = '',
    [string]$InputProbePath = '',
    [switch]$TemplateOnly
)

$ErrorActionPreference = 'Stop'
$acceptanceWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
if (-not $acceptanceWindows -and -not $TemplateOnly) {
    throw 'Physical acceptance requires Windows. Use -TemplateOnly to inspect the pending journal elsewhere.'
}
$acceptanceApp = Get-Item -LiteralPath $AppPath
if ($acceptanceApp.PSIsContainer) { throw 'AppPath must name the published executable.' }
if (-not $TemplateOnly -and $acceptanceApp.Name -ne 'AccessRelayStudio.exe') {
    throw 'AppPath must name the published AccessRelayStudio.exe.'
}
$acceptanceOut = [IO.Path]::GetFullPath($OutDirectory)
if (Test-Path -LiteralPath $acceptanceOut) { throw 'Choose a new output directory; acceptance journals are never overwritten.' }
[IO.Directory]::CreateDirectory($acceptanceOut) | Out-Null
$acceptanceIssues = [Collections.Generic.List[string]]::new()
$acceptanceEnvironment = [ordered]@{
    schemaVersion = 1
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    tester = $Tester
    templateOnly = [bool]$TemplateOnly
    hardwareAcceptanceComplete = $false
    os = [Environment]::OSVersion.VersionString
    is64BitOperatingSystem = [Environment]::Is64BitOperatingSystem
    app = [ordered]@{
        path = $acceptanceApp.FullName
        sha256 = (Get-FileHash -LiteralPath $acceptanceApp.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        productVersion = $acceptanceApp.VersionInfo.ProductVersion
        fileVersion = $acceptanceApp.VersionInfo.FileVersion
        expectedExecutableName = $acceptanceApp.Name -eq 'AccessRelayStudio.exe'
        identityValidated = $false
    }
    graphics = @()
    audioEndpoints = @()
    speechEngines = @()
    inputProbe = [ordered]@{ requested = $false; inventoryWritten = $false; exitCode = $null; actuationVerified = $false }
}
if ($acceptanceWindows -and -not $TemplateOnly) {
    try {
        $acceptanceEnvironment.graphics = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, AdapterRAM)
    } catch { $acceptanceIssues.Add('Graphics inventory: ' + $_.Exception.Message) }
    try {
        $acceptanceEnvironment.audioEndpoints = @(Get-PnpDevice -Class AudioEndpoint | Select-Object FriendlyName, Status)
    } catch { $acceptanceIssues.Add('Audio endpoint inventory: ' + $_.Exception.Message) }
    try {
        Add-Type -AssemblyName System.Speech
        $acceptanceEnvironment.speechEngines = @([System.Speech.Recognition.SpeechRecognitionEngine]::InstalledRecognizers() |
            Select-Object Id, Name, @{ Name = 'Culture'; Expression = { $_.Culture.Name } })
    } catch { $acceptanceIssues.Add('Speech inventory unavailable in this PowerShell host; inspect the app: ' + $_.Exception.Message) }
    if ($InputProbePath) {
        $acceptanceEnvironment.inputProbe.requested = $true
        try {
            $acceptanceProbe = Get-Item -LiteralPath $InputProbePath
            if ($acceptanceProbe.PSIsContainer -or $acceptanceProbe.Name -ne 'EliteInputProbe.exe') {
                throw 'InputProbePath must name the built EliteInputProbe.exe.'
            }
            $acceptanceInventory = Join-Path $acceptanceOut 'physical-input.json'
            & $acceptanceProbe.FullName --inventory-json $acceptanceInventory *> (Join-Path $acceptanceOut 'input-probe.log')
            $acceptanceEnvironment.inputProbe.exitCode = $LASTEXITCODE
            $acceptanceEnvironment.inputProbe.inventoryWritten = Test-Path -LiteralPath $acceptanceInventory
            if ($LASTEXITCODE -ne 0) { $acceptanceIssues.Add('Input probe failed; inspect input-probe.log.') }
        } catch { $acceptanceIssues.Add('Input probe: ' + $_.Exception.Message) }
    }
}
$acceptanceEnvironment.collectionIssues = @($acceptanceIssues.ToArray())
$acceptanceEnvironment | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $acceptanceOut 'environment.json') -Encoding UTF8

# Every result starts Pending. Discovery, compilation and simulated tests cannot pass these cases.
$acceptanceCases = @(
    @('A01', '1-2', 'Use Narrator, Tab/Shift+Tab and action menus through nested dialogs.', 'Focus stays contained and returns to the opener; names, descriptions and menus are usable.'),
    @('A02', '2', 'Listen while disconnecting hardware, changing voice state and completing calibration.', 'Meaningful transitions are announced once, including inside dialogs, without telemetry chatter.'),
    @('A03', '3', 'Latch physical buttons/triggers; Stop, disconnect and change profiles/devices.', 'Fresh presses latch/release; every reset clears stale intent and output.'),
    @('A04', '4', 'Measure physical stick input/output and try chosen smoothing strengths.', 'Record latency and user comfort against criteria chosen before testing; neutralization is prompt.'),
    @('A05', '5', 'Navigate profiles, sliders, menus and dialogs with one and two switches.', 'Scanning, selection, timing, debounce and exit work throughout the real WebView2 app.'),
    @('A06', '5', 'Use the selected physical/adaptive controller for native UI navigation.', 'Navigation consumes gameplay input, handles reconnects and exits without unintended game actions.'),
    @('A07', '6', 'Listen to profile and vision earcons; change or remove the audio endpoint.', 'Cues are distinct, comfortable and timely; unavailable audio does not block processing.'),
    @('A08', '6', 'Test the selected physical motors, Stop, unsupported hardware and device changes.', 'Actual rumble occurs only on the intended device and ends on cancellation; API presence alone is insufficient.'),
    @('A09', '7', 'Select two verified physical devices; exercise ownership, ties and virtual-device exclusion.', 'Only intended physical contributors reach the single virtual output under the configured conflict rules.'),
    @('A10', '7', 'Hold controls on both devices, disconnect each, record merged input and press Stop.', 'Surviving held controls persist, recording matches merged raw input and Stop neutralizes output.'),
    @('A11', '8', 'Use actual installed speech engines with intended phrases, game speech and background noise.', 'Measure recognition and false activation against predeclared criteria; record engine, language and microphone.'),
    @('A12', '8', 'Record and try personalized clicks, hums and intended dysarthric vocalizations.', 'Measure misses, confusion, false activation and fatigue with the intended user; do not substitute synthetic audio.'),
    @('A13', '8', 'Combine commands with conversion; Stop, close, switch profiles and disconnect the microphone.', 'Microphone activity remains visible; obsolete work cannot issue new commands after cancellation.'),
    @('A14', '8', 'Measure acoustic onset/end, host recognition and actual controller delivery with external timing evidence.', 'Record onset/end-to-delivery latency and resource use against criteria chosen before testing.'),
    @('A15', '9', 'Use native pickers to import both packs, tune, restart and import a compatible model.', 'Hashes, conflicts, persistence and model checks work; imported scripts require explicit activation.'),
    @('A16', '10', 'Review actual teacher/student footage, labels, overlays and session provenance before conversion.', 'Converted evidence retains verified source hashes and valid disjoint crop/overlay boundaries; real review is attached.'),
    @('A17', '11', 'Compare variants on reviewed disjoint train/validation data; reserve held-out evaluation for locked choices.', 'Record calibration, timing error, false releases and resources; no synthetic or validation-only winner is promoted.'),
    @('A18', '12', 'Collect representative Windows CPU/CUDA performance runs with at least 100 decisions each.', 'Save source/settings/build identities, readback/processing/inference/lock percentiles and end-to-end evidence before optimization.'),
    @('A19', '9,12', 'Stop/close capture during resize, source/model changes and repeated start/stop cycles.', 'Leases, generations and resources are released; stale observations cannot affect a later run.'),
    @('A20', '3-9', 'Exercise startup, disconnect, Stop all and the native emergency stop when the web interface fails.', 'Controller, voice and vision work stop and output neutralizes without depending on a working React screen.')
)
$acceptanceCases | ForEach-Object {
    [pscustomobject][ordered]@{
        id = $_[0]; roadmapSection = $_[1]; procedure = $_[2]; expected = $_[3]
        status = 'Pending'; measurement = ''; evidence = ''; notes = ''
    }
} | Export-Csv -LiteralPath (Join-Path $acceptanceOut 'cases.csv') -NoTypeInformation -Encoding UTF8
@'
This journal starts with 20 Pending physical/data acceptance cases.

Inventory, an available API, a successful build, a probe exit code, or a simulated
test is not physical acceptance. The generator never starts the app, drives a
controller, rumbles motors, records a microphone or uploads evidence. TemplateOnly
does not validate an application identity or collect device inventories.
The executable hash/version identifies the file supplied by the tester; it does
not authenticate a publisher or prove that the same file is currently running.

Before each measurement, write the intended device/user/task and pass criteria in
notes. Run the procedure on the recorded Windows app build. Change status to
Passed, Failed or Blocked and attach measured results and concrete evidence paths.
Record the active app version/commit, drivers, model hashes, sources, settings and
criteria with the corresponding evidence. Do not infer a pass from a missing API.

environment.json is the original collection record and remains marked incomplete;
do not use its hardwareAcceptanceComplete=false as a computed aggregate. Review
completed CSV cases and their evidence explicitly before updating roadmap checks.
Keep identifiable device/audio/gameplay information local unless you choose to
share it. No automatic publication or roadmap completion is performed.
'@ | Set-Content -LiteralPath (Join-Path $acceptanceOut 'README.txt') -Encoding UTF8
Write-Output ('Created pending acceptance journal: ' + $acceptanceOut)

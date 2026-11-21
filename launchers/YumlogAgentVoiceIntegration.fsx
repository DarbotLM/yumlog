#r @"__DLL__"
#r @"__DRAWING__"

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions
open Yumlog.Native

let input = Environment.GetEnvironmentVariable("YUMLOG_TEST_VOICE_SOURCE")
let root = Environment.GetEnvironmentVariable("YUMLOG_TEST_VOICE_OUTPUT")
let ffmpeg = Environment.GetEnvironmentVariable("YUMLOG_TEST_VOICE_FFMPEG")
let existing = Environment.GetEnvironmentVariable("YUMLOG_TEST_VOICE_WORKSPACE")
let minimumSpeakers = Int32.Parse(Environment.GetEnvironmentVariable("YUMLOG_TEST_VOICE_MINIMUM"))
let existingBundle = Environment.GetEnvironmentVariable("YUMLOG_TEST_VOICE_BUNDLE")
let persistedOnly = not (String.IsNullOrEmpty(existing))
let workspacePath = if persistedOnly then existing else Path.Combine(root, "workspace")
let checks = ResizeArray<obj>()
let pass name detail =
    checks.Add(box {| name = name; passed = true; detail = detail |})
    printfn "PASS %s: %s" name detail
let require condition message = if not condition then failwith message
let json value = JsonSerializer.SerializeToElement(value, NativeJson.options)
let payload (pairs: (string * string) list) = json (dict pairs)
let prop (key: string) (value: JsonElement) = value.GetProperty(key)
let stringProp key value = (prop key value).GetString() |> Option.ofObj |> Option.defaultValue ""
let number key value = (prop key value).GetDouble()
let elements key value = (prop key value).EnumerateArray() |> Seq.toArray
let digest (stream: Stream) = Convert.ToHexStringLower(SHA256.HashData(stream))
let hash file = use stream = File.OpenRead(file) in digest stream
let equalText (left: string) (right: string) = String.Equals(left, right, StringComparison.OrdinalIgnoreCase)
let report passed error =
    NativeJson.write (Path.Combine(root, "results.json"))
        {| passed = passed; error = error; sourcePath = input; workspacePath = workspacePath; checks = checks.ToArray() |}

let runFfmpeg arguments =
    let info = ProcessStartInfo(ffmpeg, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true)
    arguments |> List.iter info.ArgumentList.Add
    use child = new Process(StartInfo = info)
    require (child.Start()) "FFmpeg failed to start."
    let errors = child.StandardError.ReadToEndAsync()
    if not (child.WaitForExit(180000)) then
        child.Kill(true)
        failwith "FFmpeg timed out."
    require (child.ExitCode = 0) (errors.GetAwaiter().GetResult())

let run (workspace: DesktopLibrary.Workspace) command fields paths = workspace.Run(command, payload fields, paths)
let manifest workspace =
    (run workspace "agentVoices" [] [||]).data |> Option.map json |> Option.defaultWith (fun () -> failwith "Agent Voices returned no manifest.")

let sampleRate = 16000.
let bytesPerSample = 2L
let sample seconds = int64 (Math.Round(seconds * sampleRate, MidpointRounding.AwayFromZero))
let verifyTurnAudio (source: FileStream) (montage: FileStream) alias (start, finish) =
    source.Position <- start * bytesPerSample
    let sourceBytes = Array.zeroCreate<byte> 65536
    let montageBytes = Array.zeroCreate<byte> sourceBytes.Length
    let mutable remaining = (finish - start) * bytesPerSample
    while remaining > 0L do
        let count = int (min remaining (int64 sourceBytes.Length))
        source.ReadExactly(sourceBytes, 0, count)
        montage.ReadExactly(montageBytes, 0, count)
        require (sourceBytes.AsSpan(0, count).SequenceEqual(montageBytes.AsSpan(0, count)))
            $"Montage for {alias} differs from source audio at sample {start}."
        remaining <- remaining - int64 count

try
    let initialHash = hash input
    Directory.CreateDirectory(workspacePath) |> ignore
    let workspace = DesktopLibrary.Workspace(workspacePath)
    if not persistedOnly then
        run workspace "saveSettings" [ "outputDirectory", Path.Combine(root, "exports"); "ffmpegPath", ffmpeg ] [||] |> ignore
        run workspace "import" [] [| input |] |> ignore
        require (elements "voices" (manifest workspace) |> Array.isEmpty) "A newly imported recording already has voice cards."
        run workspace "detectAgentVoices" [] [||] |> ignore
    let snapshot = workspace.Snapshot()
    require (workspace.TryGetMedia(snapshot.selectedId) |> Option.exists (fun path -> equalText (Path.GetFullPath(path)) (Path.GetFullPath(input))))
        "The selected recording does not match the test source."
    let detected = manifest workspace
    let voices = elements "voices" detected
    require (not (String.IsNullOrWhiteSpace(stringProp "schema" detected))) "Speaker manifest schema is empty."
    require (not (String.IsNullOrWhiteSpace(stringProp "method" detected))) "Speaker manifest has no analysis method."
    require (equalText (stringProp "sourceSha256" detected) initialHash) "Speaker manifest source hash does not match the input."
    let duration = number "durationSeconds" detected
    require (Double.IsFinite(duration) && duration > 0.) "Source duration must be positive and finite."
    require (voices.Length >= minimumSpeakers) $"Only {voices.Length} voices found; expected at least {minimumSpeakers} for the test input."
    for key in [ "id"; "alias"; "artifactId" ] do
        let values = voices |> Array.map (stringProp key)
        require (values |> Array.forall (String.IsNullOrWhiteSpace >> not)) $"Speaker {key} must be nonempty."
        require ((values |> Array.map _.ToLowerInvariant() |> Set.ofArray).Count = values.Length) $"Speaker {key} must be unique."
    pass "voices.detection" $"{voices.Length} unique source-linked speaker clusters."

    let inputPcm = Path.Combine(root, "source.pcm")
    let montagePcm = Path.Combine(root, "montage.pcm")
    let decode file filters output =
        runFfmpeg ([ "-hide_banner"; "-v"; "error"; "-nostdin"; "-y"; "-i"; file; "-map"; "0:a:0"; "-vn" ] @ filters @
            [ "-ar"; "16000"; "-ac"; "1"; "-c:a"; "pcm_s16le"; "-f"; "s16le"; output ])
    decode input [ "-af"; "aresample=async=1:first_pts=0" ] inputPcm
    use source = File.OpenRead(inputPcm)
    let artifacts = snapshot.artifacts |> Array.filter (fun artifact -> artifact.kind = "agentVoice")
    require (artifacts.Length = voices.Length) "Expected one current WAV artifact per speaker."
    let collected = ResizeArray<string * string>()
    let mutable turnCount = 0
    for voice in voices do
        let alias = stringProp "alias" voice
        require (Regex.IsMatch(alias, "^[a-z][a-z0-9]*(?:_[a-z0-9]+)+$")) $"Alias has unexpected format: {alias}."
        let artifactId = stringProp "artifactId" voice
        let artifact = artifacts |> Array.tryFind (fun item -> item.id = artifactId) |> Option.defaultWith (fun () -> failwith $"WAV missing for {alias}.")
        require (artifact.exists && artifact.name = alias + ".wav") $"WAV artifact is not named for {alias}."
        let wav = workspace.TryGetMedia(artifactId) |> Option.defaultWith (fun () -> failwith $"The native media route did not register {alias}.")
        let wavHash = hash wav
        require (equalText (stringProp "sha256" voice) wavHash) $"WAV hash does not match manifest for {alias}."
        let turns = elements "turns" voice
        require (turns.Length > 0) $"A detected speaker has no turns: {alias}."
        decode wav [] montagePcm
        use montage = File.OpenRead(montagePcm)
        let mutable lastEnd = 0L
        for turn in turns do
            let startTime, endTime = number "startSeconds" turn, number "endSeconds" turn
            require (Double.IsFinite(startTime) && Double.IsFinite(endTime) && startTime >= 0. && endTime <= duration && startTime < endTime)
                $"Turn is outside source bounds for {alias}."
            let start, finish = sample startTime, sample endTime
            require (start >= lastEnd && finish > start && finish * bytesPerSample <= source.Length) $"Turn sample positions are unordered or invalid for {alias}."
            verifyTurnAudio source montage alias (start, finish)
            lastEnd <- finish
        require (montage.Position = montage.Length) $"Unexpected samples after the last reported turn for {alias}."
        require (sample (number "durationSeconds" voice) * bytesPerSample = montage.Length) $"Incorrect montage duration for {alias}."
        turnCount <- turnCount + turns.Length
        collected.Add(wav, wavHash)
    pass "voices.wav-source-evidence" $"{turnCount} turns exactly matched their source PCM across {voices.Length} alias WAVs."

    let reloaded = DesktopLibrary.Workspace(workspacePath)
    require ((manifest reloaded).GetRawText() = detected.GetRawText()) "Agent Voices did not survive reopening the workspace."
    pass "voices.persistence" "Reopened the workspace and recovered the same manifest."

    if not persistedOnly then
        let blankVideo = Path.Combine(root, "silent.mp4")
        runFfmpeg [ "-hide_banner"; "-v"; "error"; "-nostdin"; "-y"; "-f"; "lavfi"; "-i"; "color=s=64x64:d=1"; "-c:v"; "libx264"; blankVideo ]
        run reloaded "import" [] [| blankVideo |] |> ignore
        require (elements "voices" (manifest reloaded) |> Array.isEmpty) "Speaker manifest leaked onto another recording."
        run reloaded "select" [ ("videoId", snapshot.selectedId) ] [||] |> ignore
        require ((manifest reloaded).GetRawText() = detected.GetRawText()) "Speaker manifest did not restore when reselecting the source."
        pass "voices.recording-isolation" "Switching between recordings retains a separate speaker catalog for each."

    let bundle =
        if persistedOnly then existingBundle
        else
            let path = Path.Combine(root, "agent-voices.paperboy.zip")
            run reloaded "bundle" [] [| path |] |> ignore
            path
    if not (String.IsNullOrEmpty(bundle)) then
        let voiceManifest = snapshot.artifacts |> Array.find (fun artifact -> artifact.kind = "agentVoices")
        let manifestPath = workspace.TryGetMedia(voiceManifest.id) |> Option.defaultWith (fun () -> failwith "Persisted speaker manifest file is missing.")
        collected.Add(manifestPath, hash manifestPath)
        use archive = ZipFile.OpenRead(bundle)
        use index = archive.GetEntry("paperboy-manifest.json").Open()
        use document = JsonDocument.Parse(index)
        let bundleEntries = elements "entries" document.RootElement
        for path, expectedHash in collected do
            let entry = bundleEntries |> Array.tryFind (fun item -> equalText (stringProp "sourcePath" item) path)
                        |> Option.defaultWith (fun () -> failwith $"Harvest omitted {Path.GetFileName(path)}.")
            use content = archive.GetEntry(stringProp "bundlePath" entry).Open()
            require (equalText (stringProp "sha256" entry) expectedHash && digest content = expectedHash) $"Harvest changed {Path.GetFileName(path)}."
        pass "voices.harvest" $"Harvest included the speaker manifest and {voices.Length} WAVs with matching SHA-256 hashes."

    require (hash input = initialHash) "The source recording was modified."
    pass "source.unchanged" initialHash
    report true ""
with error ->
    report false error.Message
    eprintfn "FAIL: %s" error.Message
    exit 1

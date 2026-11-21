#r @"__DLL__"
#r @"__SPEECH__"
#r @"__DRAWING__"
#r @"__WINDOWS__"
#r @"__WINRT__"
open System
open System.IO
open System.IO.Compression
open System.Diagnostics
open System.Text.Json
open System.Security.Cryptography
open Yumlog.Native
let root, repo, video = @"__OUTPUT__", @"__REPO__", @"__VIDEO__"
let skipDecimation = __SKIP__
let checks = ResizeArray<obj>()
let pass name detail = checks.Add(box {| name=name; passed=true; detail=detail |}); printfn "PASS %s: %s" name detail
let require condition detail = if not condition then failwith detail
let json value = JsonSerializer.SerializeToElement(value, NativeJson.options)
let payload (pairs: (string*string) list) = json (dict pairs)
let prop name (e:JsonElement) = e.GetProperty(name:string)
let text name e = (prop name e).GetString() |> Option.ofObj |> Option.defaultValue ""
let hash path = use f=File.OpenRead(path) in Convert.ToHexStringLower(SHA256.HashData(f))
let ffmpeg=Path.Combine(repo,".tools","ffmpeg","bin","ffmpeg.exe")
let ffprobe=Path.Combine(repo,".tools","ffmpeg","bin","ffprobe.exe")
let whisper=Path.Combine(repo,".tools","whisper","runtime","Release","whisper-cli.exe")
let model=Path.Combine(repo,".tools","whisper","ggml-base.en.bin")
let runProcess executable args =
    let info=ProcessStartInfo(executable,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true)
    args |> List.iter info.ArgumentList.Add
    use child=new Process(StartInfo=info)
    require (child.Start()) "Process did not start"
    let stdout=child.StandardOutput.ReadToEndAsync()
    let stderr=child.StandardError.ReadToEndAsync()
    require (child.WaitForExit(120000)) "Process timed out"
    let output=stdout.GetAwaiter().GetResult()
    let errors=stderr.GetAwaiter().GetResult()
    require (child.ExitCode=0) errors
    output
let saved=hash video
let wr=Directory.CreateDirectory(Path.Combine(root,"workspace")).FullName
let workspace=DesktopLibrary.Workspace(wr)
let run command pairs paths=workspace.Run(command,payload pairs,paths)
let state ()=NativeJson.read<DesktopLibrary.Library>(Path.Combine(wr,"studio.json"))
let artifacts kind=(state()).Videos |> Array.find(fun v->v.Id=(state()).SelectedId) |> _.Artifacts |> Array.filter(fun a->a.Kind=kind)
let first kind=artifacts kind |> Array.tryHead |> Option.defaultWith(fun()->failwith $"Missing {kind} artifact")
let reject name action=
    let rejected=try action(); false with _->true
    require rejected (name+" should reject invalid input")
    pass name "Invalid input rejected"
let report passed error=NativeJson.write (Path.Combine(root,"results.json")) {|passed=passed;error=error;input=video;checks=checks.ToArray()|}
try
    reject "settings.relative-output" (fun()->run "saveSettings" ["outputDirectory","relative"] [||] |> ignore)
    reject "settings.missing-tool" (fun()->run "saveSettings" ["outputDirectory",Path.Combine(root,"exports");"ffmpegPath",Path.Combine(root,"missing.exe")] [||] |> ignore)
    run "saveSettings" ["outputDirectory",Path.Combine(root,"exports");"ffmpegPath",ffmpeg;"whisperPath",whisper;"whisperModel",model] [||] |> ignore
    run "setSize" ["cardSize","wide"] [||] |> ignore
    let reloaded=DesktopLibrary.Workspace(wr)
    require (reloaded.Snapshot().settings.CardSize="wide" && reloaded.Snapshot().settings.WhisperModel=model) "Settings did not survive reload"
    pass "settings.persistence" "Tool paths, model and card size persisted"
    run "import" [] [|video|] |> ignore
    run "import" [] [|video|] |> ignore
    require (workspace.Snapshot().videos.Length=1) "Duplicate import created duplicate recording"
    let priorManifest = Path.Combine(repo,"dist","ignite-2026-semantic","source-sha256.json")
    if File.Exists(priorManifest) && not (video.EndsWith("Meeting Recording.mp4")) then
        reject "insights.import-wrong-source" (fun () -> run "importAnalysis" [] [| priorManifest |] |> ignore)
    run "probe" [] [||] |> ignore
    let media=(state()).Videos[0].Media |> Option.defaultWith(fun()->failwith "No media info")
    require (media.Duration>0. && media.Width>0 && media.AudioCodec<>"") "Tracks not probed"
    pass "video.import-and-probe" $"One recording; {media.Duration:F3}s {media.Width}x{media.Height}"
    let catalog=(run "catalog" [] [||]).data |> Option.map json |> Option.defaultWith(fun()->failwith "No catalog")
    require (catalog.ValueKind=JsonValueKind.Array && catalog.GetArrayLength()>0) "Empty capability catalog"
    pass "agents.catalog" $"{catalog.GetArrayLength()} declared capabilities"
    pass "agents.ocr-readiness" ((run "ocrStatus" [] [||]).message+"; provisioning not tested")
    let voices=(run "voices" [] [||]).data |> Option.map json |> Option.defaultWith(fun()->failwith "No voice list")
    require (voices.GetArrayLength()>0) "No installed speech voices"
    let voice=text "name" (voices.EnumerateArray() |> Seq.head)
    run "speak" ["voice",voice;"script","Yumlog Studio integration validation. The original recording remains unchanged."] [||] |> ignore
    let narration=first "voice"
    runProcess ffmpeg ["-v";"error";"-i";narration.Path;"-f";"null";"-"] |> ignore
    pass "voices.synthesis" ("Narration rendered and fully decoded: "+voice)
    run "transcribe" [] [||] |> ignore
    let transcript=first "transcript"
    require ((File.ReadAllText(transcript.Path)).Contains("-->")) "No timestamped ASR cues"
    let preview=(run "readArtifact" ["artifactId",transcript.Id] [||]).data |> Option.map json |> Option.defaultWith(fun()->failwith "No transcript preview")
    require (preview.GetString().Length>20) "Empty transcript preview"
    pass "transcript.local-asr" "Actual audio transcribed locally and previewed through Workspace"
    run "attachTranscript" [] [|transcript.Path|] |> ignore
    require ((artifacts "transcript").Length=2) "Transcript attachment not persisted"
    pass "transcript.attach" "Transcript copied to a persisted artifact"
    if not skipDecimation then
        let schema=Path.Combine(root,"example-customer.schema.json")
        File.WriteAllText(schema,"""{"title":"Customer","type":"object","properties":{"customerName":{"type":"string","title":"Customer name"},"accountNumber":{"type":"string"}}}""")
        run "attachDocument" [] [|schema|] |> ignore
        pass "document.attach" "JSON customer schema attached for distillation"
        let malformed = Path.Combine(root,"misaligned.srt")
        File.WriteAllText(malformed,"1\n99:00:00,000 --> 99:00:02,000\nSpeech beyond source.\n")
        reject "transcript.misalignment" (fun () -> StudioSemantic.readTranscript malformed media.Duration |> ignore)
        run "decimate" [] [||] |> ignore
        let readLibrary () = (run "library" [] [||]).data |> Option.map json |> Option.defaultWith(fun()->failwith "No atomic library")
        let firstCards = readLibrary() |> prop "cards"
        let attachedFingerprint (items: JsonElement) =
            items.EnumerateArray()
            |> Seq.filter(fun card -> text "kind" card = "attached-document-schema")
            |> Seq.map(fun card -> text "title" card, card |> prop "evidence" |> text "sha256", (prop "attributes" card).GetRawText())
            |> Seq.sort |> Seq.toArray
        let reportDocumentCount () =
            let reportArtifact = artifacts "semanticReport" |> Array.find(fun artifact -> Path.GetFileName(artifact.Path) = "report.json")
            use doc = JsonDocument.Parse(File.ReadAllText(reportArtifact.Path))
            (prop "attachedDocuments" doc.RootElement).GetInt32()
        require (reportDocumentCount()=1) "Initial run failed to distill the attached document exactly once"
        let beforeDocuments = attachedFingerprint firstCards
        run "decimate" [] [||] |> ignore
        let library=first "atomicLibrary"
        let data=readLibrary()
        let cards=prop "cards" data
        require (cards.GetArrayLength()=firstCards.GetArrayLength()) "Repeated decimation multiplied atomic cards"
        require (reportDocumentCount()=1 && beforeDocuments.Length=1 && attachedFingerprint cards=beforeDocuments) "Repeated decimation multiplied or renamed attached document schemas"
        pass "insights.repeat-decimation" $"{cards.GetArrayLength()} cards and one attached document remain stable across two runs"
        require (cards.GetArrayLength()>0) "Atomic library empty"
        let ids=cards.EnumerateArray() |> Seq.map(text "id") |> Seq.toArray
        require ((Array.distinct ids).Length=ids.Length && Array.forall(fun id->id<>"") ids) "Invalid card IDs"
        for card in cards.EnumerateArray() do
            require ((prop "source" card).ValueKind=JsonValueKind.Object && (prop "evidence" card).ValueKind=JsonValueKind.Object) "Missing source/evidence"
        let modalities = cards.EnumerateArray() |> Seq.map (text "modality") |> Set.ofSeq
        require (Set.isSubset (set ["video";"audio";"document"]) modalities) "Missing video/audio/document atomic layer"
        let bitdot = first "bitdot"
        use layersDoc = JsonDocument.Parse(File.ReadAllText(bitdot.Path))
        let runs = prop "runs" layersDoc.RootElement |> fun x -> x.EnumerateArray() |> Seq.toArray
        let mutable bin = 0
        for r in runs do
            require ((prop "startBin" r).GetInt32()=bin) "Bitdot run gap/overlap"
            let count = (prop "length" r).GetInt32()
            require (count>0) "Empty bitdot run"
            bin <- bin + count
        require (bin=int(ceil media.Duration) && bin=(prop "binCount" layersDoc.RootElement).GetInt32()) "Bitdot does not span full source"
        pass "insights.bitdot-coverage" $"{bin} one-second bins, no gaps/overlaps"
        pass "library.atomic-cards" $"{cards.GetArrayLength()} uniquely identified source-linked cards"
        let mdkg=first "timedMetadata"
        use reader=Mdkg.openReader mdkg.Path
        let book=MdkgIntel.buildRunbook reader
        let verification=MdkgIntel.verifyRunbook reader book
        require verification.Verified "MDKG structural citations failed"
        require (book.DurationSeconds>0. && book.DurationSeconds<=media.Duration+0.1) "MDKG timeline outside source-video range"
        pass "insights.mdkg" $"{verification.ResolvedProofs} structural proofs resolved"
        let tick seconds = int64 (Math.Round(seconds * float Mdkg.MovieTimescale))
        let sourceEnd = tick media.Duration
        let intervals = cards.EnumerateArray() |> Seq.map(fun card -> text "id" card, tick ((prop "startSeconds" card).GetDouble()), tick ((prop "endSeconds" card).GetDouble())) |> Seq.toArray
        let boundaries = [| yield 0L; yield sourceEnd; for _,start,finish in intervals do yield start; yield finish |] |> Array.distinct |> Array.sort
        let checkpoints = [| yield! boundaries; for left,right in Array.pairwise boundaries do yield left + (right-left)/2L |] |> Array.distinct |> Array.filter(fun at -> at>=0L && at<sourceEnd)
        for at in checkpoints do
            let expected = intervals |> Array.choose(fun(id,start,finish)->if start<=at && at<finish then Some id else None) |> Set.ofArray
            let actual = Mdkg.SemanticGraph.reconstructAt reader at |> Option.map(fun graph -> graph.Nodes |> Map.keys |> Set.ofSeq) |> Option.defaultValue Set.empty
            require (actual=expected) $"MDKG active nodes do not match atomic intervals at tick {at}"
        pass "insights.mdkg-intervals" $"Exact active card sets at {checkpoints.Length} boundaries and interval midpoints"
        // An explicit fixture isolates expiry during silence; the real clip's ASR groups can be adjacent.
        let expiryTranscript = Path.Combine(root,"expiry-regression.srt")
        let expirySeconds = min 0.123 (media.Duration / 4.)
        let endpoint = TimeSpan.FromSeconds(expirySeconds).ToString(@"hh\:mm\:ss\,fff")
        File.WriteAllText(expiryTranscript,$"1\n00:00:00,000 --> {endpoint}\nExplicit transcript fixture for semantic node expiry.\n")
        let expiryCues = StudioSemantic.readTranscript expiryTranscript media.Duration
        let finish = expiryCues[0].End
        let expiryResult = StudioSemantic.run ffmpeg whisper model video expiryTranscript (Path.Combine(root,"mdkg-expiry-regression"))
        let expiryMetadata = expiryResult.Artifacts |> Array.pick(fun(kind,path,_,_)->if kind="timedMetadata" then Some path else None)
        use expiryReader = Mdkg.openReader expiryMetadata
        let speechAt seconds =
            Mdkg.SemanticGraph.reconstructAt expiryReader (tick seconds)
            |> Option.map(fun graph -> graph.Nodes.Values |> Seq.exists(fun node -> node.Kind="speech-segment"))
            |> Option.defaultValue false
        require (speechAt (finish/2.)) "Regression fixture has no active speech before expiry"
        require (not (speechAt finish) && not (speechAt (finish+min 0.1 ((media.Duration-finish)/2.)))) "Expired speech node persisted into a silent gap"
        pass "insights.mdkg-expiry" "Speech is present before its endpoint and absent at the endpoint and inside the following gap"
        run "runbook" [] [||] |> ignore
        require (File.Exists((first "runbook").Path)) "Runbook export absent"
        let exported=Path.Combine(root,"atomic-library-export.json")
        run "exportArtifact" ["artifactId",library.Id] [|exported|] |> ignore
        require (hash library.Path=hash exported) "Library export differs"
        pass "library.export" "Export preserves exact library bytes"
    let cardPath=Path.Combine(root,"video-card.json")
    run "exportCard" [] [|cardPath|] |> ignore
    use card=JsonDocument.Parse(File.ReadAllText(cardPath))
    require (text "id" card.RootElement<>"") "Missing Paperboy card ID"
    pass "harvest.card" "Paperboy card exported"
    let bundle=Path.Combine(root,"harvest.paperboy.zip")
    run "bundle" [] [|bundle|] |> ignore
    use zip=ZipFile.OpenRead(bundle)
    let manifest=zip.GetEntry("paperboy-manifest.json")
    require (not(isNull manifest)) "Missing bundle manifest"
    use stream=manifest.Open()
    use document=JsonDocument.Parse(stream)
    let entries=prop "entries" document.RootElement
    for item in entries.EnumerateArray() do
        let entry=zip.GetEntry(text "bundlePath" item)
        require (not(isNull entry)) "Manifest names absent ZIP entry"
        use content=entry.Open()
        let digest=Convert.ToHexStringLower(SHA256.HashData(content))
        require (digest.Equals(text "sha256" item,StringComparison.OrdinalIgnoreCase)) "Harvest SHA-256 mismatch"
        require (entry.Length=(prop "length" item).GetInt64()) "Harvest length mismatch"
    if not skipDecimation then require (entries.EnumerateArray() |> Seq.exists(fun e->(text "sourcePath" e).EndsWith("atomic-library.json"))) "Harvest omitted atomic library"
    if not skipDecimation then
        let library = first "atomicLibrary"
        use atomic = JsonDocument.Parse(File.ReadAllText(library.Path))
        let manifestRows = entries.EnumerateArray() |> Seq.toArray
        let mutable references = 0
        for card in (prop "cards" atomic.RootElement).EnumerateArray() do
            for kind in ["source"; "evidence"] do
                let reference = prop kind card
                let sourcePath = text "path" reference
                let sourceHash = text "sha256" reference
                let item = manifestRows |> Array.tryFind(fun row -> (text "sourcePath" row).Equals(sourcePath,StringComparison.OrdinalIgnoreCase))
                require item.IsSome $"Harvest omitted atomic {kind} reference: {sourcePath}"
                let item = Option.get item
                require ((text "sha256" item).Equals(sourceHash,StringComparison.OrdinalIgnoreCase)) "Atomic reference hash mismatches bundle"
                references <- references + 1
        pass "harvest.atomic-evidence" $"{references} source/evidence references resolve through portable bundle manifest"
    pass "harvest.bundle" $"Verified SHA-256 and length of {entries.GetArrayLength()} entries"
    require (hash video=saved) "Input recording changed"
    pass "source.unchanged" "Source SHA-256 unchanged"
    report true ""
with error->
    report false error.Message
    eprintfn "FAIL: %s" error.Message
    exit 1

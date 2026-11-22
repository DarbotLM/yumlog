// Studio workspace checks for the gen-audio boundary, driven by launchers/Test-YumlogGenAudio.ps1.
#r @"__DLL__"
#r @"__SPEECH__"
#r @"__DRAWING__"
#r @"__WINDOWS__"
#r @"__WINRT__"
open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Yumlog.Native
let root, video = @"__OUTPUT__", @"__VIDEO__"
let require condition detail = if not condition then failwith detail
let payload (pairs: (string * string) list) = JsonSerializer.SerializeToElement(dict pairs, NativeJson.options)
let hash (path: string) = use file = File.OpenRead(path) in Convert.ToHexStringLower(SHA256.HashData(file))
let wr = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName
let workspace = DesktopLibrary.Workspace(wr)
let run command pairs paths = workspace.Run(command, payload pairs, paths)
let state () = NativeJson.read<DesktopLibrary.Library>(Path.Combine(wr, "studio.json"))
let artifacts kind = (state()).Videos |> Array.find (fun v -> v.Id = (state()).SelectedId) |> _.Artifacts |> Array.filter (fun a -> a.Kind = kind)
let settings =
    [ ("outputDirectory", Path.Combine(root, "exports")); ("ffmpegPath", @"__FFMPEG__"); ("genAudioPython", @"__PYTHON__")
      ("genAudioRoot", @"__STUB__"); ("genAudioEngine", "kokoro_onnx"); ("genAudioVoice", "af_heart"); ("vibeVoiceModel", @"__MODEL__") ]
try
    let rejected = try run "saveSettings" [ ("outputDirectory", Path.Combine(root, "exports")); ("vibeVoiceModel", Path.Combine(root, "missing")) ] [||] |> ignore; false with _ -> true
    require rejected "A missing VibeVoice checkpoint folder was accepted."
    run "saveSettings" settings [||] |> ignore
    let reloaded = DesktopLibrary.Workspace(wr)
    reloaded.Run("snapshot", payload [], [||]) |> ignore
    let saved = (state()).Settings
    require (saved.GenAudioVoice = "af_heart" && saved.GenAudioEngine = "kokoro_onnx" && saved.VibeVoiceModel = @"__MODEL__") "gen-audio settings did not persist."
    run "import" [] [| video |] |> ignore
    run "genAudioSpeak" [ ("script", "Studio narration through gen-audio.") ] [||] |> ignore
    let voice = artifacts "voice" |> Array.exactlyOne
    require (voice.Path.EndsWith("narration-gen-audio.wav", StringComparison.Ordinal) && File.Exists(voice.Path)) "gen-audio narration artifact is missing."
    require ((File.ReadAllText(voice.Path + ".script.txt")) = "Studio narration through gen-audio.") "Studio script did not reach gen-audio."
    run "genAudioTranscribe" [] [||] |> ignore
    let transcript = artifacts "transcript" |> Array.exactlyOne
    let cues = File.ReadAllText(transcript.Path)
    require (cues.Contains("-->") && cues.Contains("Speaker 0: Open the review workspace.")) $"gen-audio transcript cues are wrong: {cues}"
    require (not (File.Exists(Path.ChangeExtension(transcript.Path, ".wav")))) "The temporary 16 kHz WAV was left behind."
    let failed = try run "genAudioSpeak" [ ("script", "") ] [||] |> ignore; false with _ -> true
    require failed "An empty gen-audio script was accepted."
    require (ChildProcesses.count () = 0) "A gen-audio child process is still registered."
    printfn "Studio gen-audio workspace checks passed (settings, narration %s, VibeVoice transcript)." (hash voice.Path)
with error ->
    eprintfn "FAIL: %s" error.Message
    exit 1

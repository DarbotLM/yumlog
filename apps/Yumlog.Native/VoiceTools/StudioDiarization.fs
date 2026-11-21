namespace Yumlog.Native

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text.RegularExpressions

module StudioDiarization =
    type Turn =
        { Speaker: string
          StartSeconds: float
          EndSeconds: float }

    let methodName = "sherpa-onnx 1.13.8 / pyannote segmentation 3.0 / 3D-Speaker CAMPPlus"

    let private installRoot () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "DarbotLabs", "Yumlog", "VoiceTools", "sherpa-onnx-v1.13.8")

    let private files () =
        let root = installRoot ()
        let runtime = Path.Combine(root, "sherpa-onnx-offline-speaker-diarization.exe")
        let segmentation = Path.Combine(root, "pyannote-segmentation-3.0.onnx")
        let embedding = Path.Combine(root, "3dspeaker-campplus.onnx")
        runtime, segmentation, embedding, Path.Combine(root, "onnxruntime.dll")

    let available () =
        let runtime, segmentation, embedding, library = files ()
        [ runtime; segmentation; embedding; library ] |> List.forall File.Exists

    let status () =
        if available () then $"Ready locally. {methodName}."
        else "Speaker detection is unavailable. Run launchers/Install-YumlogVoiceTools.ps1 to install the local runtime and models."

    let private turnLine =
        Regex(@"^(?<start>\d+(?:\.\d+)?)\s+--\s+(?<end>\d+(?:\.\d+)?)\s+speaker_(?<id>\d+)\s*$", RegexOptions.CultureInvariant)

    let private parseTurns (output: string) =
        let culture = CultureInfo.InvariantCulture
        let speakers = Dictionary<int, string>()
        output.Split('\n', StringSplitOptions.TrimEntries ||| StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            let matched = turnLine.Match(line)
            if matched.Success then
                let start = Double.Parse(matched.Groups["start"].Value, culture)
                let finish = Double.Parse(matched.Groups["end"].Value, culture)
                let speaker = Int32.Parse(matched.Groups["id"].Value, culture)
                if not (Double.IsFinite(start) && Double.IsFinite(finish)) || finish < start then
                    invalidOp "Speaker detection returned an invalid time range."
                if finish = start then None else Some(start, finish, speaker)
            elif line.Contains("speaker_", StringComparison.Ordinal) then
                invalidOp "Speaker detection returned an unrecognized time range. Check the installed Voice Tools version."
            else None)
        |> Array.sortBy (fun (start, finish, speaker) -> start, finish, speaker)
        |> Array.map (fun (start, finish, speaker) ->
            let id =
                match speakers.TryGetValue(speaker) with
                | true, existing -> existing
                | false, _ ->
                    let next = sprintf "speaker-%02d" (speakers.Count + 1)
                    speakers.Add(speaker, next)
                    next
            { Speaker = id; StartSeconds = start; EndSeconds = finish })

    /// The input is mono 16 kHz PCM16 WAV aligned to source time zero.
    let run (wavPath: string) =
        if not (available ()) then invalidOp (status ())
        if not (File.Exists(wavPath)) then invalidArg "wavPath" "Speaker detection input audio is missing."
        let runtime, segmentation, embedding, _ = files ()
        let threads = min 4 Environment.ProcessorCount
        let args =
            [ $"--segmentation.num-threads={threads}"
              $"--embedding.num-threads={threads}"
              $"--segmentation.pyannote-model={segmentation}"
              $"--embedding.model={embedding}"
              "--clustering.cluster-threshold=0.65"
              Path.GetFullPath(wavPath) ]
        let stdout, _ = DesktopMedia.run 1800000 runtime args
        parseTurns stdout

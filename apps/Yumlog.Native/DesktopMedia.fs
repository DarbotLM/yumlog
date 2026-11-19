namespace Yumlog.Native

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Speech.Synthesis
open System.Text.Json
open System.Text.RegularExpressions

module DesktopMedia =
    type MediaInfo =
        { Duration: float
          Width: int
          Height: int
          VideoCodec: string
          AudioCodec: string
          SubtitleStreams: int
          Bytes: int64 }

    let videoExtensions = set [ ".mp4"; ".mov"; ".m4v"; ".mkv"; ".webm"; ".avi"; ".wmv"; ".mpg"; ".mpeg"; ".mts"; ".m2ts" ]
    let transcriptExtensions = set [ ".srt"; ".vtt"; ".txt" ]
    let extension (path: string) = (Path.GetExtension(path) |> orEmptyIfNull).ToLowerInvariant()
    let number (value: float) = value.ToString("0.######", CultureInfo.InvariantCulture)

    let private run (timeout: int) (executable: string) (arguments: string list) =
        let info = ProcessStartInfo(executable, UseShellExecute = false, CreateNoWindow = true,
                                    RedirectStandardOutput = true, RedirectStandardError = true)
        for argument in arguments do
            info.ArgumentList.Add(argument)
        use child = new Process(StartInfo = info)
        if not (child.Start()) then invalidOp $"Could not start {executable}."
        let output = child.StandardOutput.ReadToEndAsync()
        let errors = child.StandardError.ReadToEndAsync()
        if not (child.WaitForExit(timeout)) then
            child.Kill(true)
            child.WaitForExit()
            invalidOp $"{Path.GetFileName(executable)} exceeded the time limit."
        let stdout = output.GetAwaiter().GetResult()
        let stderr = errors.GetAwaiter().GetResult()
        if child.ExitCode <> 0 then
            let lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            let detail = lines |> Array.rev |> Array.truncate 5 |> Array.rev |> String.concat " "
            invalidOp $"{Path.GetFileName(executable)} exited with code {child.ExitCode}. {detail}"
        stdout, stderr

    let private ffmpeg executable logLevel arguments =
        run 600000 executable ([ "-hide_banner"; "-nostdin"; "-loglevel"; logLevel; "-n"
                                 "-protocol_whitelist"; "file,crypto,data" ] @ arguments)

    let private probePath (ffmpeg: string) =
        if Path.IsPathRooted(ffmpeg) then
            let sibling = Path.Combine(Path.GetDirectoryName(ffmpeg) |> nonNull, "ffprobe.exe")
            if File.Exists(sibling) then sibling else "ffprobe"
        else "ffprobe"

    let private property name (element: JsonElement) =
        if element.ValueKind = JsonValueKind.Object then
            match element.TryGetProperty(name: string) with
            | true, value -> value
            | _ -> Unchecked.defaultof<JsonElement>
        else Unchecked.defaultof<JsonElement>

    let private text name element =
        let value = property name element
        if value.ValueKind = JsonValueKind.String then value.GetString() |> orEmptyIfNull else ""

    let private integer name (element: JsonElement option) =
        element
        |> Option.map (fun stream ->
            let value = property name stream
            match value.ValueKind with
            | JsonValueKind.Number -> value.GetInt32()
            | _ -> 0)
        |> Option.defaultValue 0

    let probe executable (input: string) =
        let output, _ =
            run 30000 (probePath executable)
                [ "-v"; "error"; "-protocol_whitelist"; "file,crypto,data"; "-show_format"; "-show_streams"; "-of"; "json"; input ]
        use document = JsonDocument.Parse(output)
        let streams = property "streams" document.RootElement
        let streams = if streams.ValueKind = JsonValueKind.Array then streams.EnumerateArray() |> Seq.toArray else [||]
        let matches kind = Array.filter (fun stream -> text "codec_type" stream = kind) streams
        let video =
            matches "video" |> Array.tryFind (fun stream ->
                let disposition = property "disposition" stream
                disposition.ValueKind <> JsonValueKind.Object || integer "attached_pic" (Some disposition) <> 1)
        if video.IsNone then invalidOp "The file does not contain a video track."
        let codec = Option.map (text "codec_name") >> Option.defaultValue ""
        let duration =
            match Double.TryParse(text "duration" (property "format" document.RootElement), NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value when Double.IsFinite(value) && value > 0.0 -> value
            | _ -> 0.0
        { Duration = duration
          Width = integer "width" video
          Height = integer "height" video
          VideoCodec = codec video
          AudioCodec = codec (matches "audio" |> Array.tryHead)
          SubtitleStreams = (matches "subtitle").Length
          Bytes = FileInfo(input).Length }

    let still executable input seconds output =
        ffmpeg executable "error" [ "-ss"; number seconds; "-i"; input; "-map"; "0:V:0"; "-frames:v"; "1"; output ] |> ignore
        if not (File.Exists(output)) then invalidOp "No frame was available at that timestamp."

    let poster executable input output =
        ffmpeg executable "error"
            [ "-i"; input; "-map"; "0:V:0"; "-frames:v"; "1"; "-vf"; "scale=480:-2:force_original_aspect_ratio=decrease"; "-q:v"; "3"; output ]
        |> ignore

    let clip executable input start duration output =
        ffmpeg executable "error"
            [ "-ss"; number start; "-i"; input; "-t"; number duration; "-map"; "0:V:0"; "-map"; "0:a:0?"
              "-c:v"; "libx264"; "-pix_fmt"; "yuv420p"; "-vf"; "pad=ceil(iw/2)*2:ceil(ih/2)*2"
              "-preset"; "fast"; "-c:a"; "aac"; "-movflags"; "+faststart"; output ]
        |> ignore

    let scenes executable input threshold limit directory =
        let pattern = Path.Combine(directory, "frame-%04d.png")
        let filter = $"fps=2,select='eq(n,0)+gt(scene,{number threshold})',showinfo"
        let _, log = ffmpeg executable "info"
                         [ "-i"; input; "-map"; "0:V:0"; "-vf"; filter; "-fps_mode"; "vfr"; "-frames:v"; string limit; pattern ]
        let timestamps =
            Regex.Matches(log, @"showinfo[^\r\n]*?\bn:\s*\d+\s+pts:\s*-?\d+\s+pts_time:\s*([\d.eE+-]+)")
            |> Seq.map (fun item -> Double.Parse(item.Groups[1].Value, CultureInfo.InvariantCulture))
            |> Seq.toArray
        let files = Directory.GetFiles(directory, "frame-*.png") |> Array.sort
        if files.Length = 0 || files.Length > timestamps.Length then invalidOp "Could not match the extracted frames to video timestamps."
        (files, Array.take files.Length timestamps)
        ||> Array.map2 (fun path seconds ->
            let name = TimeSpan.FromSeconds(seconds).ToString(@"hh\-mm\-ss\.fff", CultureInfo.InvariantCulture)
            let destination = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(path)}_{name}.png")
            File.Move(path, destination)
            destination, seconds)

    let wave executable input output =
        ffmpeg executable "error" [ "-i"; input; "-map"; "0:a:0"; "-c:a"; "pcm_s16le"; output ] |> ignore

    let spectrogram executable input output =
        ffmpeg executable "error"
            [ "-i"; input; "-filter_complex"; "[0:a:0]showspectrumpic=s=1200x420:legend=1:color=viridis[s]"
              "-map"; "[s]"; "-frames:v"; "1"; output ] |> ignore

    let subtitles executable input output =
        ffmpeg executable "error" [ "-i"; input; "-map"; "0:s:0"; "-c:s"; "srt"; output ] |> ignore

    let transcribe executable (whisper: string) (model: string) input (output: string) =
        if String.IsNullOrWhiteSpace(whisper) || not (File.Exists(model)) then
            invalidOp "Set the whisper.cpp executable and a local model in Settings."
        let pcm = Path.ChangeExtension(output, ".wav") |> orEmptyIfNull
        let transcriptBase = Path.ChangeExtension(output, null) |> orEmptyIfNull
        try
            ffmpeg executable "error" [ "-i"; input; "-map"; "0:a:0"; "-ar"; "16000"; "-ac"; "1"; "-c:a"; "pcm_s16le"; pcm ] |> ignore
            run 1800000 whisper [ "-m"; model; "-f"; pcm; "-osrt"; "-of"; transcriptBase ] |> ignore
            if not (File.Exists(output)) then invalidOp "Whisper did not produce a transcript."
        finally
            File.Delete(pcm)

    let voices () =
        use engine = new SpeechSynthesizer()
        engine.GetInstalledVoices()
        |> Seq.filter (fun voice -> voice.Enabled)
        |> Seq.map (fun voice ->
            let info = voice.VoiceInfo
            {| name = info.Name; locale = info.Culture.DisplayName; description = info.Description |})
        |> Seq.toArray

    let speak voice (script: string) (output: string) =
        use engine = new SpeechSynthesizer()
        engine.SelectVoice(voice)
        engine.SetOutputToWaveFile(output)
        engine.Speak(script)

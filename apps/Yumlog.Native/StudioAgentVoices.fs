namespace Yumlog.Native

open System
open System.IO
open System.Text
open Yumlog.Native.Mdkg

module StudioAgentVoices =
    type TimeSlice =
        { StartSeconds: float
          EndSeconds: float }

    type Voice =
        { Id: string
          Alias: string
          Path: string
          Sha256: string
          DurationSeconds: float
          Turns: TimeSlice array }

    let private sampleRate = 16000
    let private sampleBytes = 2L
    let private seconds sample = float sample / float sampleRate

    // These aliases are fictional labels assigned by order of appearance.
    let private aliases =
        [| "enthusiastic_tech_lead"; "curious_builder"; "thoughtful_guide"
           "practical_explorer"; "creative_planner"; "friendly_maker"
           "focused_navigator"; "bright_organizer"; "patient_mentor"
           "inventive_designer"; "steady_facilitator"; "observant_reviewer" |]

    let private alias index =
        if index < aliases.Length then aliases[index]
        else sprintf "curious_collaborator_%02d" (index + 1)

    let private readPcm (stream: FileStream) =
        use reader = new BinaryReader(stream, Encoding.ASCII, true)
        let fourCc () = Encoding.ASCII.GetString(reader.ReadBytes(4))
        if fourCc () <> "RIFF" then invalidOp "Speaker analysis expects a standard PCM WAV file."
        reader.ReadUInt32() |> ignore
        if fourCc () <> "WAVE" then invalidOp "Speaker analysis WAV header is invalid."
        let mutable formatOk = false
        let mutable data = None
        while data.IsNone && stream.Position <= stream.Length - 8L do
            let name = fourCc ()
            let length = int64 (reader.ReadUInt32())
            let position = stream.Position
            if length > stream.Length - position then invalidOp "Speaker analysis WAV contains an incomplete chunk."
            match name with
            | "fmt " when length >= 16L ->
                let format = reader.ReadUInt16()
                let channels = reader.ReadUInt16()
                let rate = reader.ReadUInt32()
                reader.ReadUInt32() |> ignore
                let alignment = reader.ReadUInt16()
                let bits = reader.ReadUInt16()
                formatOk <- format = 1us && channels = 1us && rate = uint32 sampleRate && alignment = 2us && bits = 16us
            | "data" -> data <- Some(position, length)
            | _ -> ()
            stream.Position <- position + length + length % 2L
        match data with
        | Some(offset, length) when formatOk && length > 0L && length % sampleBytes = 0L ->
            offset, length / sampleBytes
        | _ -> invalidOp "Speaker analysis requires mono 16 kHz PCM16 audio."

    let private mergeTurns (intervals: (int64 * int64) array) =
        let merged = ResizeArray<int64 * int64>()
        for start, finish in intervals |> Array.sort do
            if merged.Count = 0 then merged.Add(start, finish)
            else
                let last = merged.Count - 1
                let previousStart, previousEnd = merged[last]
                if start <= previousEnd then merged[last] <- previousStart, max finish previousEnd
                else merged.Add(start, finish)
        merged.ToArray()

    let private writeMontage (source: FileStream) audioOffset path (intervals: (int64 * int64) array) =
        let size = intervals |> Array.sumBy (fun (start, finish) -> (finish - start) * sampleBytes)
        if size > int64 UInt32.MaxValue - 36L then invalidOp "The speaker's WAV exceeds the RIFF size limit."
        use target = new FileStream(path, FileMode.CreateNew, FileAccess.Write)
        use writer = new BinaryWriter(target)
        let fourCc (name: string) = writer.Write(Encoding.ASCII.GetBytes(name))
        fourCc "RIFF"
        writer.Write(uint32 (36L + size))
        fourCc "WAVE"
        fourCc "fmt "
        writer.Write(16u)
        writer.Write(1us)
        writer.Write(1us)
        writer.Write(uint32 sampleRate)
        writer.Write(uint32 (int64 sampleRate * sampleBytes))
        writer.Write(uint16 sampleBytes)
        writer.Write(16us)
        fourCc "data"
        writer.Write(uint32 size)

        let buffer = Array.zeroCreate<byte> 65536
        for start, finish in intervals do
            source.Position <- audioOffset + start * sampleBytes
            let mutable remaining = (finish - start) * sampleBytes
            while remaining > 0L do
                let bytes = int (min remaining (int64 buffer.Length))
                source.ReadExactly(buffer, 0, bytes)
                target.Write(buffer, 0, bytes)
                remaining <- remaining - int64 bytes
        seconds (size / sampleBytes)

    let run ffmpeg videoPath (duration: float) directory =
        let pcm = Path.Combine(directory, "analysis-pcm.wav")
        try
            DesktopMedia.voiceAnalysisWave ffmpeg videoPath pcm
            use audio = File.OpenRead(pcm)
            let audioOffset, audioSamples = readPcm audio
            let sourceSamples = min audioSamples (int64 (Math.Floor(duration * float sampleRate)))
            let sample timestamp =
                let bounded = Math.Clamp(timestamp, 0., seconds sourceSamples)
                int64 (Math.Round(bounded * float sampleRate, MidpointRounding.AwayFromZero))

            StudioDiarization.run pcm
            |> Array.choose (fun turn ->
                if not (Double.IsFinite(turn.StartSeconds) && Double.IsFinite(turn.EndSeconds)) ||
                   turn.StartSeconds < 0. || turn.EndSeconds <= turn.StartSeconds ||
                   String.IsNullOrWhiteSpace(turn.Speaker) then
                    invalidOp "The local speaker detector returned an invalid time slice."
                let start, finish = sample turn.StartSeconds, sample turn.EndSeconds
                if finish > start then Some(turn.Speaker, (start, finish)) else None)
            |> Array.groupBy fst
            |> Array.map (fun (speaker, turns) -> speaker, turns |> Array.map snd |> mergeTurns)
            |> Array.sortBy (fun (speaker, turns) -> fst turns[0], speaker)
            |> Array.mapi (fun index (_, intervals) ->
                let name = alias index
                let path = Path.Combine(directory, name + ".wav")
                let sampleDuration = writeMontage audio audioOffset path intervals
                { Id = sprintf "speaker-%02d" (index + 1); Alias = name; Path = path
                  Sha256 = FileHash.sha256 path; DurationSeconds = sampleDuration
                  Turns = intervals |> Array.map (fun (start, finish) -> { StartSeconds = seconds start; EndSeconds = seconds finish }) })
        finally
            File.Delete(pcm)

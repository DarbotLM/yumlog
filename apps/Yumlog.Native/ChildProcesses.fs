namespace Yumlog.Native

open System
open System.Collections.Generic
open System.Diagnostics

/// Tracks helper processes (ffmpeg, ffprobe, orchestration steps) that Yumlog
/// starts, so that closing Studio or the CLI never leaves headless children
/// running. Every spawn site registers the child right after Start() and the
/// app shell calls killAll() when it exits.
module ChildProcesses =
    let private gate = obj ()
    let private live = HashSet<Process>()

    let private untrack (child: Process) =
        lock gate (fun () -> live.Remove(child) |> ignore)

    /// Registers a started child. Dispose the result once the child has exited.
    let track (child: Process) : IDisposable =
        lock gate (fun () -> live.Add(child) |> ignore)
        { new IDisposable with
            member _.Dispose() = untrack child }

    /// Number of children still registered (used by self-tests).
    let count () = lock gate (fun () -> live.Count)

    /// Kills every registered child and its descendants. Safe to call repeatedly.
    let killAll () =
        let snapshot =
            lock gate (fun () ->
                let items = Seq.toArray live
                live.Clear()
                items)
        for child in snapshot do
            try
                if not child.HasExited then
                    child.Kill(true)
                    child.WaitForExit(5000) |> ignore
            with _ -> ()

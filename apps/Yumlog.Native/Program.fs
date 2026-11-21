namespace Yumlog.Native

open System
open System.Threading
open System.Windows.Forms

module Program =
    [<STAThread; EntryPoint>]
    let main _ =
        let mutable createdNew = false
        use instanceMutex = new Mutex(true, @"Local\DarbotLabs.Yumlog.Native", &createdNew)
        if not createdNew then
            MessageBox.Show("Yumlog Native is already running.", "Yumlog Native", MessageBoxButtons.OK, MessageBoxIcon.Information)
            |> ignore
            0
        else
            AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> ChildProcesses.killAll())
            try
                WinAppRuntime.tryInitialize() |> ignore
                NativeUi.run()
                ChildProcesses.killAll()
                WinAppRuntime.shutdown()
                0
            with ex ->
                ChildProcesses.killAll()
                WinAppRuntime.shutdown()
                MessageBox.Show(ex.Message, "Yumlog Native could not start", MessageBoxButtons.OK, MessageBoxIcon.Error)
                |> ignore
                1

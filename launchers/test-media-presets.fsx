#r "../apps/Yumlog.Native.Cli/bin/Release/net10.0-windows10.0.26100.0/win-x64/Yumlog.Native.Cli.dll"
open Yumlog.Native
let durations = [("5s",5);("15s",15);("30s",30);("1m",60);("2m",120);("3m",180);("5m",300);("10m",600);("15m",900);("30m",1800);("45m",2700);("60m",3600);("90m",5400);("120m",7200)]
for id, seconds in durations do
    match Cli.parse [|"record";"--duration-preset";id;"--frame-rate-preset";"60"|] with
    | Record(config, _) when config.DurationSec = seconds && config.Fps = 60 -> ()
    | _ -> failwith "Preset mismatch"
for args in [[|"record";"--duration-preset";"7m"|];[|"record";"--frame-rate-preset";"29.97"|];[|"record";"--duration-preset";"5s";"--duration";"10"|]] do
    let rejected = try Cli.parse args |> ignore; false with :? System.ArgumentException -> true
    if not rejected then failwith "Invalid/unsupported preset was accepted"
printfn "14 native durations and 3 negative controls passed without recording."

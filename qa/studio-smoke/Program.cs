using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Yumlog.Native.dll"));
        var native = assembly.GetType("Yumlog.Native.NativeUi", true)!;
        var root = Path.GetFullPath(args[1]);
        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        if (args[0] == "ui")
        {
            NativeCall(() => native.GetMethod("runWorkspace", flags)!.Invoke(null, [root]));
            return;
        }
        Directory.CreateDirectory(root);
        var fixture = Path.GetFullPath(args[2]);
        var workspaceType = assembly.GetType("Yumlog.Native.DesktopLibrary+Workspace", true)!;
        var workspace = Activator.CreateInstance(workspaceType, flags, null, [root], null)!;
        var snapshot = workspaceType.GetMethod("Snapshot", flags)!;
        var run = workspaceType.GetMethod("Run", flags)!;
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        JsonElement Serialized(object item) => JsonSerializer.SerializeToElement(item, options);
        JsonElement Call(string command, object? arguments = null, params string[] files)
        {
            var result = NativeCall(() => run.Invoke(workspace, [command, Serialized(arguments ?? new {}), files]))!;
            var json = Serialized(result);
            Console.WriteLine(command + ": " + json.GetProperty("message").GetString());
            return json.GetProperty("data");
        }
        JsonElement State() => Serialized(NativeCall(() => snapshot.Invoke(workspace, []))!);
        void Require(bool passed, string detail)
        {
            if (!passed) throw new Exception(detail);
        }
        Require(State().GetProperty("videos").GetArrayLength() == 0, "A new library should be empty");
        Call("import", null, fixture);
        Call("import", null, fixture);
        var state = State();
        Require(state.GetProperty("videos").GetArrayLength() == 1, "Expected idempotent import");
        var first = state.GetProperty("videos")[0];
        var id = first.GetProperty("id").GetString();
        var media = first.GetProperty("media");
        Require(media.GetProperty("width").GetInt32() == 960, "FFprobe width was missing");
        Require(first.GetProperty("hasPoster").GetBoolean(), "Missing poster");
        Call("clip", new { start = "1.25", end = "3.75" });
        Call("frame", new { timestamp = "2.5" });
        Call("scenes", new { threshold = "0.04", limit = "3" });
        Call("wave");
        Call("spectrogram");
        Call("subtitles");
        var transcript = State().GetProperty("artifacts").EnumerateArray().First(x => x.GetProperty("kind").GetString() == "transcript");
        var captions = Call("readArtifact", new { artifactId = transcript.GetProperty("id").GetString() }).GetString();
        Require(captions!.Contains("Welcome to the capture review"), "Missing timed caption");
        Require(Call("catalog").GetArrayLength() > 1, "No native capabilities");
        var voices = Call("voices");
        if (voices.GetArrayLength() > 0)
            Call("speak", new { voice = voices[0].GetProperty("name").GetString(), script = "Welcome to the Yumlog test recording." });
        var frame = State().GetProperty("artifacts").EnumerateArray().First(x => x.GetProperty("kind").GetString() == "frame");
        Call("ocr", new { artifactId = frame.GetProperty("id").GetString() });
        if (State().GetProperty("hasTimedMetadata").GetBoolean()) Call("runbook");
        Call("setSize", new { cardSize = "wide" });
        Call("exportCard", null, Path.Combine(root, "sample.paperboy-card.json"));
        var bundle = Path.Combine(root, "sample.paperboy.zip");
        Call("bundle", null, bundle);
        using var archive = ZipFile.OpenRead(bundle);
        Require(archive.GetEntry("paperboy-manifest.json") != null, "Missing bundle manifest");
        Require(archive.Entries.Any(x => x.Name == "paperboy-video-card.json"), "Missing embedded video card");
        Require(archive.Entries.Any(x => x.Name == Path.GetFileName(fixture)), "Missing source video payload");
        Require(State().GetProperty("artifacts").EnumerateArray().Where(x => x.GetProperty("kind").GetString() == "frame").Count() == 4, "Expected one still plus three scene exports");
        var reopened = Activator.CreateInstance(workspaceType, flags, null, [root], null)!;
        var reopenedState = Serialized(NativeCall(() => snapshot.Invoke(reopened, []))!);
        Require(reopenedState.GetProperty("selectedId").GetString() == id, "Recording selection was not restored");
        Require(reopenedState.GetProperty("settings").GetProperty("cardSize").GetString() == "wide", "Size was not restored");
        Console.WriteLine("Native fixture passed.");
    }

    private static object? NativeCall(Func<object?> action)
    {
        try { return action(); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
}

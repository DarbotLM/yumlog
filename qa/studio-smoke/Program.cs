using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        Require(State().GetProperty("settings").GetProperty("screenshotDirectory").GetString() == @"C:\Users\darbot\Pictures\Screenshots",
            "The requested default screenshot directory changed");
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
        var statePath = Path.Combine(root, "studio.json");
        var saved = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        saved["settings"]!.AsObject().Remove("screenshotDirectory");
        File.WriteAllText(statePath, saved.ToJsonString());
        var migrated = Activator.CreateInstance(workspaceType, flags, null, [root], null)!;
        var migratedState = Serialized(NativeCall(() => snapshot.Invoke(migrated, []))!);
        Require(migratedState.GetProperty("settings").GetProperty("screenshotDirectory").GetString() == @"C:\Users\darbot\Pictures\Screenshots",
            "Existing libraries did not acquire the new screenshot default");
        Require(migratedState.GetProperty("videos").GetArrayLength() == 1, "Gallery migration changed the video library");
        var relativeRejected = false;
        try { Call("setScreenshotDirectory", new { screenshotDirectory = "relative-photos" }); }
        catch (ArgumentException) { relativeRejected = true; }
        Require(relativeRejected, "Relative screenshot directories must be rejected");
        var missing = Path.Combine(root, "missing-photos");
        Call("setScreenshotDirectory", new { screenshotDirectory = missing });
        Require(State().GetProperty("screenshotGallery").GetProperty("status").GetString() == "missing" && !Directory.Exists(missing),
            "A missing screenshot directory must be reported without creating it");
        var photosRoot = Path.Combine(root, "photos");
        Directory.CreateDirectory(photosRoot);
        Call("setScreenshotDirectory", new { screenshotDirectory = photosRoot });
        Require(State().GetProperty("screenshotGallery").GetProperty("status").GetString() == "empty", "Empty folder status missing");
        var framePath = State().GetProperty("artifactPaths").GetProperty(frame.GetProperty("id").GetString()!).GetString()!;
        for (var index = 0; index < 50; index++)
            File.Copy(framePath, Path.Combine(photosRoot, $"photo-{index:D2}.png"), true);
        File.WriteAllText(Path.Combine(photosRoot, "ignored.html"), "<h1>Not a gallery photo</h1>");
        Directory.CreateDirectory(Path.Combine(photosRoot, "nested"));
        File.Copy(framePath, Path.Combine(photosRoot, "nested", "not-enumerated.png"), true);
        File.Copy(framePath, Path.Combine(photosRoot, "photo & quoted.PNG"), true);
        var expectedPhotoCount = 51;
        Call("refreshScreenshots");
        var gallery = State().GetProperty("screenshotGallery");
        Require(gallery.GetProperty("photos").GetArrayLength() == expectedPhotoCount, "Gallery format filtering/top-level enumeration failed");
        var photoId = gallery.GetProperty("photos")[0].GetProperty("id").GetString()!;
        var mediaMethod = workspaceType.GetMethod("TryGetMedia", flags)!;
        Require(NativeCall(() => mediaMethod.Invoke(workspace, [photoId])) != null, "Registered photo was not accessible through the media bridge");
        Require(NativeCall(() => mediaMethod.Invoke(workspace, ["photo-../../ignored.html"])) == null, "Unregistered media path was accepted");
        Call("setScreenshotDirectory", new { screenshotDirectory = missing });
        Require(NativeCall(() => mediaMethod.Invoke(workspace, [photoId])) == null, "Changing the screenshot folder did not revoke old media IDs");
        Call("setScreenshotDirectory", new { screenshotDirectory = photosRoot });
        for (var index = 51; index <= 2000; index++)
            File.Copy(framePath, Path.Combine(photosRoot, $"limit-{index:D4}.png"), true);
        Call("refreshScreenshots");
        gallery = State().GetProperty("screenshotGallery");
        Require(gallery.GetProperty("photos").GetArrayLength() == 2000 && gallery.GetProperty("truncated").GetBoolean(),
            "Gallery payload must be bounded and disclose truncation");
        foreach (var extra in Directory.EnumerateFiles(photosRoot, "limit-*.png")) File.Delete(extra);
        Call("refreshScreenshots");
        var restored = Activator.CreateInstance(workspaceType, flags, null, [root], null)!;
        var restoredState = Serialized(NativeCall(() => snapshot.Invoke(restored, []))!);
        Require(restoredState.GetProperty("screenshotGallery").GetProperty("photos").GetArrayLength() == expectedPhotoCount,
            "Screenshot folder selection was not persisted");
        Require(restoredState.GetProperty("videos").GetArrayLength() == 1 && restoredState.GetProperty("selectedId").GetString() == id,
            "Gallery actions changed video selection");
        Console.WriteLine("Screenshot gallery passed (default, migration, filtering, missing/empty folders, media IDs, bounds, persistence).");
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

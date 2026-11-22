using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;

/// <summary>
/// <c>nodes/PeopleDetector.cs</c>, saved as library <c>people_detector</c> beside library <c>vision</c> and derived by a C#
/// device node in an entity definition run by a whole manager (<see cref="EntityTestHome"/>), with the entity
/// descriptions, so that <c>CameraEntity</c> exists. Its camera is <c>camera.porch</c>, whose live feed is the fake ffmpeg
/// of hass-sharp's <see cref="CameraFeedTests"/>: grey frames, or the group photo while the still file exists. The tests
/// that run the detector need a local <c>hs_vision</c> build (<see cref="VisionTests.LocalLibrary"/>), and are skipped
/// without one unless <c>HS_VISION_REQUIRE_LIBRARY=1</c>.
/// </summary>
static class PeopleDetectorTests
{
    const string ProbeKey = "HassSharp.PeopleDetectorTests.Probe";
    static readonly string[] Keys = ["camera", "face_seen", "person_seen", "faces", "people", "last_face_seen", "last_person_seen"];

    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static string Rid => RuntimeInformation.RuntimeIdentifier;

    static string directory = "", ffmpeg = "", still = "";
    // What the fake Home Assistant names as each camera's stream source; any other camera has none ("Camera not found").
    static readonly Lock gate = new();
    static readonly Dictionary<string, string> sources = [];

    /// <summary><c>nodes/PeopleDetector.cs</c> as committed.</summary>
    static string Committed() => File.ReadAllText(Path.Combine(VisionTests.Root, "nodes", "PeopleDetector.cs"));

    /// <summary>The fake stream source of <c>camera.porch</c>: 640 × 426 (group.jpg's size) at 20 frames a second, the still while it exists.</summary>
    static string Porch(string more = "") => $"fake://w=640,h=426,fps=20,{more}still={still}";

    static void Source(string entityId, string? url)
    {
        lock (gate)
        {
            if (url == null) sources.Remove(entityId);
            else sources[entityId] = url;
        }
    }

    [RuntimeSuite("--people-detector", Order = 42)]
    public static async Task Run()
    {
        Check(TurboJpeg.Available, TurboJpeg.Missing + " (brew install jpeg-turbo on macOS, the distribution's libturbojpeg package on Linux)");
        var saved = (CameraFeeds.IdleAfter, CameraFeeds.FirstFrameLimit, CameraFeeds.StallLimit, CameraFeeds.RestartDelays, CameraFeeds.Tries, CameraFeeds.RestAfter);
        var asks = PyInterop.BeginCameraSource;
        directory = Path.Combine(Path.GetTempPath(), "people-detector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ffmpeg = Path.Combine(directory, "ffmpeg");
        still = Path.Combine(directory, "still");
        File.WriteAllText(ffmpeg, CameraFeedTests.FakeFfmpeg);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var tests = new List<(string, Func<Task>)>
        {
            (nameof(TheDeviceHasItsEntities), TheDeviceHasItsEntities),
            (nameof(UnsupportedPlatformFailsTheStart), UnsupportedPlatformFailsTheStart),
        };
        var failures = new List<string>();
        if (VisionTests.LocalLibrary != null)
            tests.AddRange([
                (nameof(SeesPeopleAndHoldsSeen), SeesPeopleAndHoldsSeen),
                (nameof(SeenDoesNotFlicker), SeenDoesNotFlicker),
                (nameof(LastSeenIsNotWrittenOnEveryCheck), LastSeenIsNotWrittenOnEveryCheck),
                (nameof(AStalledCameraFailsTheCheckAndExpiresSeen), AStalledCameraFailsTheCheckAndExpiresSeen),
                (nameof(AMissingCameraIsLoggedOnce), AMissingCameraIsLoggedOnce),
                (nameof(StopClosesTheDetector), StopClosesTheDetector),
            ]);
        else if (Environment.GetEnvironmentVariable("HS_VISION_REQUIRE_LIBRARY") == "1")
            failures.Add($"an hs_vision library is required (HS_VISION_REQUIRE_LIBRARY=1) and there is none for {Rid}");
        else
            Console.WriteLine($"SKIP people detector: no hs_vision library for {Rid}");
        try
        {
            PyInterop.BeginCameraSource = (entityId, ok, fail) =>
            {
                string? url;
                lock (gate) sources.TryGetValue(entityId, out url);
                // Answered on another thread, as Python answers on its loop.
                _ = Task.Run(() => { if (url != null) ok(url, ffmpeg); else fail("Camera not found"); });
            };
            foreach (var (name, test) in tests)
            {
                // Each test starts with no still (grey frames), camera.porch on the fake, and fresh feeds.
                (CameraFeeds.IdleAfter, CameraFeeds.FirstFrameLimit, CameraFeeds.StallLimit) = (TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                (CameraFeeds.RestartDelays, CameraFeeds.Tries, CameraFeeds.RestAfter) = ([TimeSpan.FromMilliseconds(50)], 2, TimeSpan.FromSeconds(30));
                lock (gate) sources.Clear();
                Source("camera.porch", Porch());
                Hide();
                CameraFeeds.Open();
                try { await test(); }
                catch (Exception e) { failures.Add($"{name}: {e}"); }
                finally { await CameraFeeds.CloseAllAsync(); }
            }
        }
        finally
        {
            CameraFeeds.Open();
            (CameraFeeds.IdleAfter, CameraFeeds.FirstFrameLimit, CameraFeeds.StallLimit, CameraFeeds.RestartDelays, CameraFeeds.Tries, CameraFeeds.RestAfter) = saved;
            PyInterop.BeginCameraSource = asks;
            try { Directory.Delete(directory, recursive: true); } catch (Exception) { }
        }
        if (failures.Count > 0) throw new Exception($"People detector: {failures.Count} failing:\n" + string.Join("\n", failures));
        Console.WriteLine(VisionTests.LocalLibrary != null
            ? $"PASS people detector: PeopleDetector.cs reports the faces and people a camera shows, holds seen, writes last seen sparingly, and fails a check without a frame ({VisionTests.LocalLibrary})"
            : "PASS people detector: PeopleDetector.cs has its entities and settings, and an unsupported platform fails its start");
    }

    // ---- The fake camera's still ----

    static readonly Lazy<byte[]> group = new(() => SharpImage.Load(File.ReadAllBytes(VisionTests.Fixture("group.jpg"))).ToPixels(SharpPixelFormat.Yuv420));

    /// <summary>group.jpg as one YUV 4:2:0 frame of the fake: what it shows while the still exists.</summary>
    static byte[] Group => group.Value;

    /// <summary>Shows the group photo: written beside the still and moved over it, so the fake never reads half a frame.</summary>
    static void Show()
    {
        var written = still + ".tmp";
        File.WriteAllBytes(written, Group);
        File.Move(written, still, overwrite: true);
    }

    /// <summary>Shows nobody: grey frames.</summary>
    static void Hide() => File.Delete(still);

    // ---- A whole manager with both libraries ----

    /// <summary>One snapshot pushed, when it was pushed (on the rig's clock, and on the wall clock).</summary>
    sealed record Pushed(TimeSpan At, DateTimeOffset When, JsonObject Snapshot)
    {
        public string UniqueId => Snapshot["uniqueId"]!.GetValue<string>();
        public bool Available => Snapshot["available"]!.GetValue<bool>();
        public JsonNode? Value => Snapshot["values"]!.AsObject().TryGetPropertyValue("is_on", out var on) ? on : Snapshot["values"]!["value"];
        public bool? IsOn => Snapshot["values"]!["is_on"]?.GetValue<bool>();
        public double? Number => Snapshot["values"]!["value"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<double>() : null;
        public DateTimeOffset? Time => Snapshot["values"]!["value"] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture) : null;
        public override string ToString() => $"{At.TotalSeconds:0.000} s {Snapshot.ToJsonString()}";
    }

    /// <summary>
    /// A manager over an <see cref="EntityTestHome"/> with the entity descriptions and libraries <c>vision</c> and
    /// <c>people_detector</c> saved, a loopback server for the download, and every snapshot pushed recorded with its time.
    /// </summary>
    sealed class Rig : IAsyncDisposable
    {
        // Guards pushed, which the push writes from the pump's thread.
        readonly Lock recording = new();
        readonly List<Pushed> pushed = [];
        readonly Stopwatch clock = Stopwatch.StartNew();
        string vision = "", people = "";

        Rig(string name)
        {
            Server = LoopbackServer.Start();
            Home = new EntityTestHome("people-detector-" + name);
            Api = new HassSharpManager(Home.Root);
            // The home's push records the batch; this records each snapshot with its time. The home puts back what it replaced.
            var push = PyInterop.PushEntityStates;
            PyInterop.PushEntityStates = batch =>
            {
                var (at, when) = (clock.Elapsed, DateTimeOffset.UtcNow);
                var snapshots = JsonNode.Parse(batch)!.AsArray().Select(node => new Pushed(at, when, node!.AsObject())).ToArray();
                lock (recording) pushed.AddRange(snapshots);
                push(batch);
            };
        }

        public LoopbackServer Server { get; }
        public EntityTestHome Home { get; }
        public HassSharpManager Api { get; }
        public TimeSpan Now => clock.Elapsed;
        /// <summary>How many snapshots were pushed so far: where to read on from.</summary>
        public int Mark { get { lock (recording) return pushed.Count; } }

        /// <summary>The rig, with library <c>vision</c> of <paramref name="vision"/> (else <see cref="VisionTests.LocalSource"/>) and <c>people_detector</c> of <paramref name="people"/> (else as committed).</summary>
        public static async Task<Rig> Start(string name, Func<LoopbackServer, string>? vision = null, string? people = null)
        {
            var rig = new Rig(name);
            try
            {
                await rig.Api.Init();
                rig.Api.SetEntityDescriptions(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ha-entity-components.json")), started: true);
                rig.vision = (vision ?? VisionTests.LocalSource)(rig.Server);
                rig.people = people ?? Committed();
                foreach (var (id, file, source) in new[] { ("vision", "Vision.cs", rig.vision), ("people_detector", "PeopleDetector.cs", rig.people) })
                {
                    var saved = await rig.Ok(rig.Api.NodeLibraryRequest, new { operation = "save", id, name = file, expectedRevision = 0, source });
                    Check(saved["saved"]!.GetValue<bool>(), $"library {id} saves: " + saved.ToJsonString());
                }
                return rig;
            }
            catch
            {
                await rig.DisposeAsync();
                throw;
            }
        }

        public static async Task<JsonObject> Answer(Func<string, Task<string>> request, object body) => JsonNode.Parse(await request(JsonSerializer.Serialize(body)))!.AsObject();

        public async Task<JsonObject> Ok(Func<string, Task<string>> request, object body)
        {
            var answer = await Answer(request, body);
            Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString());
            return answer["result"]!.AsObject();
        }

        /// <summary>Both libraries as saved, previewed as the manager compiles them, with the entity descriptions.</summary>
        public NodeLibraryPreview Preview(params CustomNodeSource[] nodes) =>
            new NodeLibraryCompiler(NodeLibraryFixture.EntityProvider).Preview([new("vision", vision, 1), new("people_detector", people, 1)], nodes);

        /// <summary>The C# device node <c>door</c> of <paramref name="code"/>, named <paramref name="name"/>, with the signature its code derives.</summary>
        public JsonObject Device(string name, string code, JsonObject arguments)
        {
            var descriptor = Preview(new CustomNodeSource("c", code, "csharp_device")).Snapshot.Custom["c"];
            Check(descriptor.Valid, "the device's code compiles: " + string.Join(" | ", descriptor.Diagnostics.Select(d => d.Message)));
            return EntityNodeTests.CSharpDevice("door", "door", name, new JsonObject { ["source"] = code, ["signature"] = descriptor.Signature!.DeepClone(), ["arguments"] = arguments });
        }

        /// <summary>A definition of one C# device node <c>door</c>, of the class <paramref name="type"/> deriving <c>PeopleDetector</c>.</summary>
        public JsonObject Definition(string name, string type, JsonObject arguments, string? code = null) => new()
        {
            ["name"] = name, ["settings"] = new JsonArray(),
            ["logic"] = new JsonObject
            {
                ["kind"] = "graph",
                ["graph"] = new JsonObject { ["version"] = 5, ["name"] = name, ["nodes"] = new JsonArray(Device(type, code ?? $"public class {type} : PeopleDetector {{ }}", arguments)), ["edges"] = new JsonArray() },
            },
        };

        /// <summary>Saves the definition at revision 0 and answers the whole answer.</summary>
        public Task<JsonObject> Save(string id, JsonObject definition) =>
            Answer(Api.EntityRequest, new { operation = "save", id, expectedRevision = 0, definition });

        /// <summary>Saves the definition at revision 0; it must save.</summary>
        public Task<JsonObject> Saved(string id, JsonObject definition) =>
            Ok(Api.EntityRequest, new { operation = "save", id, expectedRevision = 0, definition });

        /// <summary>The snapshots of <c>{definition}:door:{key}</c> pushed from <paramref name="from"/> on.</summary>
        public Pushed[] Of(string key, int from = 0, string definition = "porch")
        {
            var id = $"{definition}:door:{key}";
            lock (recording) return [.. pushed.Skip(from).Where(p => p.UniqueId == id)];
        }

        /// <summary>The last snapshot of <c>{definition}:door:{key}</c>; null before any.</summary>
        public Pushed? Latest(string key, string definition = "porch") => Of(key, 0, definition).LastOrDefault();

        public string Shown(string key, int from = 0, string definition = "porch") => string.Join(" | ", Of(key, from, definition).Select(p => p.ToString()));

        public string Lines() => string.Join(" | ", Home.Logs());

        public async ValueTask DisposeAsync()
        {
            try { await Api.UnloadAsync(); }
            finally
            {
                await Home.DisposeAsync();
                await Server.DisposeAsync();
            }
        }
    }

    static JsonObject Arguments(string camera = "camera.porch") => new()
    {
        ["Camera"] = VisualExecutionTests.Lit("entity_id:camera", camera),
        ["CheckEvery"] = VisualExecutionTests.Lit("number", 0.1),
        ["StayOnFor"] = VisualExecutionTests.Lit("number", 1),
    };

    /// <summary>Waits up to <paramref name="limit"/> (5 s unless given) for the condition, then fails naming what it waited for.</summary>
    static async Task Until(Func<bool> condition, string what, double limit = 5)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(limit)) throw new Exception("timed out waiting: " + what);
            await Task.Delay(5);
        }
    }

    /// <summary>The library vision for a platform it has no build for: nothing is downloaded.</summary>
    static string Elsewhere(LoopbackServer server) => VisionTests.Rewritten(server.Url("/"), ("linux-riscv64", new string('a', 64)));

    // ---- Always: no native library needed ----

    static async Task TheDeviceHasItsEntities()
    {
        // The two libraries preview valid against the entity descriptions, without a diagnostic.
        var preview = new NodeLibraryCompiler(NodeLibraryFixture.EntityProvider).Preview([new("vision", VisionTests.Committed(), 1), new("people_detector", Committed(), 1)]);
        Check(preview.Valid && preview.Diagnostics.IsEmpty, "Vision.cs and PeopleDetector.cs preview valid: " + string.Join(" | ", preview.Diagnostics.Select(d => $"{d.SourceId} {d.StartLine}:{d.StartColumn} {d.Message}")));

        await using var rig = await Rig.Start("entities", Elsewhere);
        // The C# device's inputs: the five settings, with their bounds; the camera alone is required.
        var signature = rig.Preview(new CustomNodeSource("c", "public class Porch : PeopleDetector { }", "csharp_device")).Snapshot.Custom["c"].Signature!;
        var settings = signature["settings"]!.AsArray().Select(s => s!.AsObject()).ToArray();
        string Shape(JsonObject s) => string.Join(",", new[] { "key", "label", "type", "domain", "minimum", "maximum", "required" }.Select(k => s[k]?.ToJsonString() ?? "-"));
        string[] expected =
        [
            "\"Camera\",\"Camera\",\"entity\",\"camera\",-,-,true",
            "\"CheckEvery\",\"Check every\",\"number\",-,0.1,60,-",
            "\"StayOnFor\",\"Stay on for\",\"number\",-,0,3600,-",
            "\"FaceScore\",\"Face score\",\"number\",-,0.1,1,-",
            "\"PersonScore\",\"Person score\",\"number\",-,0.1,1,-",
        ];
        Check(settings.Select(Shape).SequenceEqual(expected), "the device's inputs and their bounds: " + string.Join(" | ", settings.Select(Shape)));
        Check(settings.All(s => s["description"]?.GetValue<string>() is { Length: > 0 } && s["secret"]?.GetValue<bool>() == false), "each input has its summary as its description: " + signature.ToJsonString());

        // Without a camera, the save is refused, naming it; so is a check more often than every 0.1 s.
        static string Problems(JsonObject answer) => answer["error"]?["detail"]?["diagnostics"]?.ToJsonString() ?? answer.ToJsonString();
        var arguments = Arguments();
        arguments.Remove("Camera");
        var cameraless = await rig.Save("porch", rig.Definition("Porch people", "Porch", arguments));
        Check(cameraless["ok"]!.GetValue<bool>() == false && cameraless["error"]!["code"]!.GetValue<string>() == "validation"
            && cameraless["error"]!["detail"]!["diagnostics"]!.AsArray().Any(d => d!["message"]!.GetValue<string>() == "Camera is required" && d["nodeId"]?.GetValue<string>() == "door" && d["inputId"]?.GetValue<string>() == "Camera"),
            "a save without a camera is refused with Camera is required: " + Problems(cameraless));
        var often = Arguments();
        often["CheckEvery"] = VisualExecutionTests.Lit("number", 0.05);
        var hurried = await rig.Save("porch", rig.Definition("Porch people", "Porch", often));
        var hurriedProblem = hurried["error"]?["detail"]?["diagnostics"]?.AsArray().FirstOrDefault(d => d!["nodeId"]?.GetValue<string>() == "door" && d["inputId"]?.GetValue<string>() == "CheckEvery");
        Check(hurried["ok"]!.GetValue<bool>() == false && hurried["error"]!["code"]!.GetValue<string>() == "validation" && hurriedProblem != null && hurriedProblem["severity"]!.GetValue<int>() == 3,
            "a check every 0.05 s is a problem on the node's CheckEvery: " + Problems(hurried));

        // The defaults, the device's metadata and its entities, as a class deriving it sees them in its constructor (which
        // runs before Setup(), and so before the library this platform has no build for is refused).
        AppContext.SetData(ProbeKey, null);
        const string probe = """
            public class Probe : PeopleDetector
            {
                public Probe()
                {
                    var lines = new List<string> { string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{CheckEvery}|{StayOnFor}|{FaceScore}|{PersonScore}|{Manufacturer}|{Model}") };
                    foreach (var e in Entities)
                        lines.Add($"{e.Key}|{e.Name}|{e.GetType().Name}|{e.DeviceClass}|{e.Icon}|{e.RestoreState}|{(e as SharpSensor)?.StateClass}");
                    AppContext.SetData("HassSharp.PeopleDetectorTests.Probe", string.Join("\n", lines));
                }
            }
            """;
        var probed = await rig.Saved("probe", rig.Definition("Probe people", "Probe", new JsonObject { ["Camera"] = VisualExecutionTests.Lit("entity_id:camera", "camera.porch") }, probe));
        Check(!probed["active"]!.GetValue<bool>(), "the probe does not start here: " + probed.ToJsonString());
        var seen = AppContext.GetData(ProbeKey) as string ?? throw new Exception("the probe's constructor did not run: " + rig.Lines());
        string[] entities =
        [
            "0.5|10|0.7|0.45|HassSharp|People detector",
            "camera||PeopleDetectorCamera|||False|",
            "face_seen|Face seen|SharpBinarySensor|occupancy||False|",
            "person_seen|Person seen|SharpBinarySensor|occupancy||False|",
            "faces|Faces|SharpSensor||mdi:face-recognition|False|measurement",
            "people|People|SharpSensor||mdi:account-multiple|False|measurement",
            "last_face_seen|Last face seen|SharpSensor|timestamp||True|",
            "last_person_seen|Last person seen|SharpSensor|timestamp||True|",
        ];
        Check(seen.Split('\n').SequenceEqual(entities), "the defaults, the metadata and the entities in order:\n" + seen);
        Console.WriteLine("PASS people detector: the libraries preview valid, the device's five inputs have their bounds and defaults, a missing camera and a check every 0.05 s are refused, and it has its seven entities in order");
    }

    static async Task UnsupportedPlatformFailsTheStart()
    {
        await using var rig = await Rig.Start("unsupported", Elsewhere);
        var saved = await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments()));
        var expected = $"Porch people: Porch: The vision library has no native build for {Rid}; it runs on linux-riscv64";
        Check(!saved["active"]!.GetValue<bool>() && saved["activationError"]?.GetValue<string>() == expected, "the start fails naming the platform: " + saved.ToJsonString());
        Check(rig.Server.Requests().Length == 0, "nothing was downloaded");
        Console.WriteLine("PASS people detector: on a platform the library has no build for, the definition does not start, and says why");
    }

    // ---- With the native library ----

    static async Task SeesPeopleAndHoldsSeen()
    {
        // Where the device draws the first face: the detector of Vision.cs on the very frame the fake shows.
        float x, y, w;
        await using (var detection = await VisionTests.SetUp.Start())
        {
            using var frame = SharpImage.FromPixels(640, 426, SharpPixelFormat.Yuv420, Group);
            var found = VisionTests.Items(VisionTests.Loaded.Faces(detection.Detector, frame, 640, 0.7));
            Check(found.Length == 8, $"group.jpg's frame has 8 faces at 640, not {found.Length}");
            (x, y, w) = (VisionTests.Get(found[0], "X"), VisionTests.Get(found[0], "Y"), VisionTests.Get(found[0], "Width"));
        }

        await using var rig = await Rig.Start("sees");
        var started = await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments()));
        Check(started["active"]!.GetValue<bool>() && started["document"]?["status"]?.GetValue<string>() is null or "running", "the definition runs: " + started.ToJsonString() + " | " + rig.Lines());

        // Its entities, in order, with their domains and classes.
        var sync = JsonNode.Parse(rig.Home.Syncs("porch").Last())!;
        var described = sync["entities"]!.AsArray().Select(e => e!.AsObject()).ToArray();
        string Described(JsonObject e) => $"{e["uniqueId"]} {e["domain"]} {e["deviceClass"]?.GetValue<string>() ?? "-"} {e["options"]?["stateClass"]?.GetValue<string>() ?? "-"}";
        string[] entities =
        [
            "porch:door:camera camera - -",
            "porch:door:face_seen binary_sensor occupancy -", "porch:door:person_seen binary_sensor occupancy -",
            "porch:door:faces sensor - measurement", "porch:door:people sensor - measurement",
            "porch:door:last_face_seen sensor timestamp -", "porch:door:last_person_seen sensor timestamp -",
        ];
        Check(described.Select(Described).SequenceEqual(entities), "the device's entities: " + string.Join(" | ", described.Select(Described)));
        int Info(string line) => rig.Home.Logs(PyLogLevel.Info).Count(l => l == line);
        Check(Info($"Porch people: People detector ready ({Rid})") == 1 && Info($"Porch people: Downloading libhs_vision-{Rid}.so from 127.0.0.1") == 1,
            "downloaded once, then ready: " + rig.Lines());

        // Grey frames: nobody, and nobody was ever seen.
        await Until(() => rig.Latest("face_seen")?.IsOn == false && rig.Latest("person_seen")?.IsOn == false && rig.Latest("faces")?.Number == 0 && rig.Latest("people")?.Number == 0,
            "grey frames publish nobody seen and counts of 0: " + string.Join(" | ", Keys.Select(k => rig.Shown(k))));
        Check(rig.Of("last_face_seen").Concat(rig.Of("last_person_seen")).All(p => p.Value == null), "last seen stays unknown: " + rig.Shown("last_face_seen") + " | " + rig.Shown("last_person_seen"));

        // The group photo: 8 faces and 7 people, both seen, and last seen now.
        Show();
        await Until(() => rig.Latest("faces")?.Number == 8 && rig.Latest("people")?.Number == 7 && rig.Latest("face_seen")?.IsOn == true && rig.Latest("person_seen")?.IsOn == true
                && rig.Latest("last_face_seen")?.Time != null && rig.Latest("last_person_seen")?.Time != null,
            "the group photo publishes 8 faces, 7 people, both seen and last seen: " + string.Join(" | ", Keys.Skip(1).Select(k => rig.Shown(k))));
        foreach (var key in new[] { "last_face_seen", "last_person_seen" })
        {
            var ago = DateTimeOffset.UtcNow - rig.Latest(key)!.Time!.Value;
            Check(ago >= TimeSpan.FromSeconds(-0.1) && ago <= TimeSpan.FromSeconds(5), $"{key} is a time within the last 5 s: {rig.Shown(key)}");
        }
        var jpeg = await rig.Api.CameraImage("porch:door:camera");
        var picture = SharpImage.Load(jpeg);
        Check(picture.Width == 640 && picture.Height == 426, $"the camera shows the frame, 640 wide: {picture.Width} × {picture.Height}");
        var rgb = picture.ToPixels();
        int row = Math.Clamp((int)MathF.Round(y) + 1, 0, 425), left = Math.Max(0, (int)MathF.Round(x) + 2), right = Math.Min(640, (int)MathF.Round(x + w) - 2);
        var along = Enumerable.Range(left, Math.Max(0, right - left)).Select(column => (row * 640 + column) * 3).ToArray();
        var green = along.Count(at => VisionTests.Greenish(rgb, at));
        Check(along.Length > 10 && green * 2 >= along.Length, $"the camera's frame has the first face's top edge green: {green} of {along.Length} pixels");

        // Nobody again: the counts go to 0 at once, each seen holds for its second, and last seen keeps the last face's time.
        var hidden = rig.Now;
        var hiddenWhen = DateTimeOffset.UtcNow;
        Hide();
        await Until(() => rig.Of("faces").Any(p => p.At >= hidden && p.Number == 0) && rig.Of("people").Any(p => p.At >= hidden && p.Number == 0), "the counts publish 0");
        var offs = new List<string>();
        foreach (var (count, seen, last) in new[] { ("faces", "face_seen", "last_face_seen"), ("people", "person_seen", "last_person_seen") })
        {
            var zero = rig.Of(count).First(p => p.At >= hidden && p.Number == 0);
            Check(rig.Of(seen).LastOrDefault(p => p.At <= zero.At)?.IsOn == true, $"{seen} is still on when {count} publishes 0: " + rig.Shown(seen));
            await Until(() => rig.Of(seen).Any(p => p.At > zero.At && p.IsOn == false), $"{seen} turns off: " + rig.Shown(seen));
            var off = rig.Of(seen).First(p => p.At > zero.At && p.IsOn == false);
            var held = (off.At - zero.At).TotalSeconds;
            Check(rig.Of(seen).Where(p => p.At > zero.At && p.At < off.At).All(p => p.IsOn == true), $"{seen} stays on until then: " + rig.Shown(seen));
            // Last seen is written in the check that turns seen off, so it is in the same batch: the last detection's time.
            var lastSeen = rig.Latest(last)!.Time!.Value;
            Check(Math.Abs((lastSeen - hiddenWhen).TotalSeconds) <= 0.5, $"{last} is the time of the last frame with one, ±0.5 s: {lastSeen:O}, hidden at {hiddenWhen:O}");
            // Held for Stay on for after the last frame with one, on the device's own clock (last seen is that check's time).
            // After the count turned 0 that is Stay on for less one check, 0.9 s at the least; but as published, the timer's
            // and the debounced publish's jitter put it a few milliseconds either side of 0.9 s (0.896 s was seen), so the
            // exact bound is checked here, and 3 s after the count as the most.
            var since = (off.When - lastSeen).TotalSeconds;
            Check(since >= 1, $"{seen} stays on for 1 s after the last frame with one: off {since:0.000} s after it");
            Check(held <= 3, $"{seen} turns off within 3 s of {count} publishing 0: {held:0.000} s | " + rig.Shown(seen));
            offs.Add($"{seen} {held:0.000} s after 0, {since:0.000} s after the last");
        }
        var settled = rig.Mark;
        var finals = (rig.Latest("last_face_seen")!.Time, rig.Latest("last_person_seen")!.Time);
        await Task.Delay(1500);
        Check(rig.Of("last_face_seen", settled).Concat(rig.Of("last_person_seen", settled)).Count() == 0 && (rig.Latest("last_face_seen")!.Time, rig.Latest("last_person_seen")!.Time) == finals,
            "last seen does not change after: " + rig.Shown("last_face_seen", settled) + " | " + rig.Shown("last_person_seen", settled));
        Console.WriteLine($"PASS people detector: grey frames publish nobody; the group photo 8 faces and 7 people, both seen, last seen and a drawn camera; hidden, the counts go to 0 and seen holds ({string.Join(", ", offs)})");
    }

    static async Task SeenDoesNotFlicker()
    {
        Show();
        await using var rig = await Rig.Start("flicker");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments()));
        await Until(() => rig.Latest("person_seen")?.IsOn == true && rig.Latest("face_seen")?.IsOn == true, "both seen turn on: " + rig.Shown("person_seen"));
        var from = rig.Mark;
        var shown = true;
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(300);
            if (shown) Hide(); else Show();
            shown = !shown;
        }
        Check(rig.Of("person_seen", from).All(p => p.IsOn != false) && rig.Of("face_seen", from).All(p => p.IsOn != false),
            "seen publishes no false while the photo comes and goes every 300 ms: " + rig.Shown("person_seen", from) + " | " + rig.Shown("face_seen", from));
        Check(rig.Of("faces", from).Any(p => p.Number == 0) && rig.Of("people", from).Any(p => p.Number == 0),
            "the counts go to 0 meanwhile: " + rig.Shown("faces", from) + " | " + rig.Shown("people", from));
        Console.WriteLine($"PASS people detector: with the photo coming and going every 300 ms, the counts go to 0 {rig.Of("people", from).Count(p => p.Number == 0)} times and seen stays on");
    }

    static async Task LastSeenIsNotWrittenOnEveryCheck()
    {
        Show();
        await using var rig = await Rig.Start("last-seen");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments()));
        await Until(() => rig.Latest("person_seen")?.IsOn == true, "person seen turns on: " + rig.Shown("person_seen"));
        await Task.Delay(3000);
        Check(rig.Latest("people")?.Number == 7 && rig.Latest("person_seen")?.IsOn == true, "the photo was seen throughout: " + rig.Shown("people"));
        var written = rig.Of("last_person_seen");
        Check(written.Length <= 2 && written.Last().Time != null, "last person seen is published at most twice in about 30 checks: " + rig.Shown("last_person_seen"));
        Console.WriteLine($"PASS people detector: with the photo shown for 3 s, last person seen is published {written.Length} times");
    }

    static async Task AStalledCameraFailsTheCheckAndExpiresSeen()
    {
        const string declared = "static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);";
        var source = Committed();
        Check(source.Split(declared).Length == 2, "PeopleDetector.cs declares FrameTimeout once as 10 s");
        CameraFeeds.StallLimit = TimeSpan.FromSeconds(60);
        Source("camera.porch", Porch("stall=5,"));
        Show();
        await using var rig = await Rig.Start("stalled", people: source.Replace(declared, "static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(1);"));
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments()));
        await Until(() => rig.Of("face_seen").Any(p => p.IsOn == true) && rig.Of("person_seen").Any(p => p.IsOn == true), "both seen turn on before the camera stalls: " + rig.Shown("person_seen"));

        const string failed = "Porch people: the timer callback every 100 ms failed: ";
        string[] Errors() => [.. rig.Home.Logs(PyLogLevel.Error).Where(l => l.StartsWith(failed, StringComparison.Ordinal))];
        await Until(() => Errors().Length > 0, "the check fails: " + rig.Lines());
        await Until(() => rig.Latest("faces")?.Available == false && rig.Latest("people")?.Available == false, "the counts turn unavailable: " + rig.Shown("faces") + " | " + rig.Shown("people"));
        var offs = new List<string>();
        foreach (var (seen, last) in new[] { ("face_seen", "last_face_seen"), ("person_seen", "last_person_seen") })
        {
            await Until(() => rig.Of(seen).Any(p => p.IsOn == true) && rig.Latest(seen)?.IsOn == false, $"{seen} turns off: " + rig.Shown(seen));
            var off = rig.Of(seen).Last();
            await Until(() => rig.Latest(last)?.Time != null, $"{last} is written");
            var detected = rig.Latest(last)!.Time!.Value;
            var after = (off.When - detected).TotalSeconds;
            Check(after <= 1 + 2, $"{seen} turns off within 3 s of the last frame with one: {after:0.000} s");
            offs.Add($"{seen} {after:0.00} s");
        }
        var expired = rig.Mark;
        await Task.Delay(1500);
        Check(rig.Of("face_seen", expired).Concat(rig.Of("person_seen", expired)).All(p => p.IsOn != true), "seen does not turn on again while stalled: " + rig.Shown("face_seen", expired) + " | " + rig.Shown("person_seen", expired));
        var debug = rig.Home.Logs(PyLogLevel.Debug).Count(l => l.StartsWith(failed, StringComparison.Ordinal));
        Check(Errors() is [var error] && error.Contains("no frame from camera.porch within 1 s", StringComparison.Ordinal) && debug >= 2,
            $"one error line for {debug} failing checks, naming the camera and the time: " + string.Join(" | ", Errors()));

        var stopping = Stopwatch.StartNew();
        await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = false });
        Check(stopping.Elapsed < TimeSpan.FromSeconds(2), $"turning it off takes under 2 s: {stopping.Elapsed.TotalSeconds:0.000} s");
        Console.WriteLine($"PASS people detector: a stalled camera fails each check after 1 s, logged once, makes the counts unavailable and lets seen expire ({string.Join(", ", offs)}); off in {stopping.Elapsed.TotalSeconds:0.00} s");
    }

    static async Task AMissingCameraIsLoggedOnce()
    {
        await using var rig = await Rig.Start("missing");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments()));
        await rig.Saved("gone", rig.Definition("Gone people", "Gone", Arguments("camera.gone")));
        const string failed = "Gone people: the timer callback every 100 ms failed: ";
        int Failing() => rig.Home.Logs(PyLogLevel.Debug).Count(l => l.StartsWith(failed, StringComparison.Ordinal));
        await Until(() => Failing() >= 5, "five checks fail: " + rig.Lines());
        var errors = rig.Home.Logs(PyLogLevel.Error).Where(l => l.StartsWith("Gone people: ", StringComparison.Ordinal)).ToArray();
        Check(errors.Length == 1 && errors[0].StartsWith(failed, StringComparison.Ordinal), $"one error line for {Failing()} failing checks: " + string.Join(" | ", errors));
        var error = errors[0];
        Check(!rig.Home.Logs().Any(l => l.StartsWith("Porch people: the timer", StringComparison.Ordinal)), "the porch's checks do not fail: " + rig.Lines());
        foreach (var key in new[] { "face_seen", "person_seen", "faces", "people" })
            Check(rig.Of(key, 0, "gone").All(p => p.Value == null), $"gone's {key} is never anything but unknown: " + rig.Shown(key, 0, "gone"));
        Console.WriteLine($"PASS people detector: a camera that is not there fails each check ({Failing()} so far) and is logged once: {error}");
    }

    static async Task StopClosesTheDetector()
    {
        await using var rig = await Rig.Start("stop");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments()));
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks run: " + rig.Shown("faces"));
        // The device's detector, read from its private field, to call after it stopped.
        var scripts = (UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Api)!;
        Check(scripts.Entities.TryGet("porch:door:camera", out _, out var camera) && camera.AddedTo != null, "porch:door:camera runs on its device");
        var device = camera!.AddedTo!;
        var field = device.GetType().BaseType!.GetField("detector", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("PeopleDetector has no field detector");
        var detector = (IDisposable)field.GetValue(device)!;
        Check(CameraFeeds.All.Any(f => f.EntityId == "camera.porch"), "camera.porch has a live feed");

        var running = rig.Mark;
        await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = false });
        await Until(() => rig.Home.Logs(PyLogLevel.Debug).Contains("Porch people: People detector stopped"), "the device says it stopped: " + rig.Lines());
        // The host shows each entity unavailable as the definition stops; nothing is published after that.
        await Until(() => Keys.All(key => rig.Of(key, running).Any(p => !p.Available)), "the stop shows each entity unavailable: " + string.Join(" | ", Keys.Select(k => rig.Shown(k, running))));
        var stopped = rig.Mark;
        await Until(() => CameraFeeds.All.All(f => f.EntityId != "camera.porch"), "the live feed of camera.porch closes");
        try
        {
            VisionTests.Loaded.Faces(detector, SharpImage.Blank(64, 48), 640, 0.7);
            throw new Exception("the detector still detects after the stop");
        }
        catch (ObjectDisposedException closed) when (closed.ObjectName == "VisionDetector") { }
        await Task.Delay(1000);
        Check(rig.Home.Logs(PyLogLevel.Debug).Count(l => l == "Porch people: People detector stopped") == 1, "said once: " + rig.Lines());
        var after = Keys.SelectMany(key => rig.Of(key, stopped)).ToArray();
        Check(after.Length == 0, "nothing of porch publishes after the stop: " + string.Join(" | ", after.Select(p => p.ToString())));
        Console.WriteLine("PASS people detector: turned off, the device closes its detector and its camera's feed, says so once at Debug, and publishes nothing more");
    }
}

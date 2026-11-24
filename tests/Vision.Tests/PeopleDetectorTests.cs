using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;

/// <summary>
/// <c>nodes/PeopleDetector.cs</c>, saved as library <c>people_detector</c> beside libraries <c>vision</c> and
/// <c>people</c> and derived by a C# device node in an entity definition run by a whole manager
/// (<see cref="EntityTestHome"/>), with the entity descriptions, so that <c>CameraEntity</c> exists. Its camera is
/// <c>camera.porch</c>, whose live feed is the fake ffmpeg of hass-sharp's <see cref="CameraFeedTests"/>: grey frames, or
/// the group photo while the still file exists. The tests that run the detector need a local <c>hs_vision</c> build
/// (<see cref="VisionTests.LocalLibrary"/>), and are skipped without one unless <c>HS_VISION_REQUIRE_LIBRARY=1</c>. Those
/// of recognition derive a class whose recogniser is a fake (<see cref="Fake"/>): the detector is the real one, and each
/// face's embedding is the one the test gives the face's place in the picture. The stores are the manager's own, under
/// the home.
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

    /// <summary><c>nodes/People.cs</c> as committed.</summary>
    static string CommittedPeople() => File.ReadAllText(Path.Combine(VisionTests.Root, "nodes", "People.cs"));

    /// <summary>The people list's shared store.</summary>
    const string PeopleStore = "hass-sharp-vision.people";

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
                (nameof(RecognitionOffOpensNothingAndWritesNothing), RecognitionOffOpensNothingAndWritesNothing),
                (nameof(DetectPeopleOffLeavesThePersonEntitiesUnavailable), DetectPeopleOffLeavesThePersonEntitiesUnavailable),
                (nameof(AFaceThatIsNotUsableIsNeverMatched), AFaceThatIsNotUsableIsNeverMatched),
                (nameof(AtMostFourFacesAreEmbeddedTheLargestFirst), AtMostFourFacesAreEmbeddedTheLargestFirst),
                (nameof(FacesAreRecognisedSuggestedOrGrouped), FacesAreRecognisedSuggestedOrGrouped),
                (nameof(TheCameraShowsTheRecognisedName), TheCameraShowsTheRecognisedName),
                (nameof(ANewLookIsLearnedOnceAndOnlyWhenAllowed), ANewLookIsLearnedOnceAndOnlyWhenAllowed),
                (nameof(GroupsOlderThanTheKeepTimeAreGoneAfterACheck), GroupsOlderThanTheKeepTimeAreGoneAfterACheck),
                (nameof(ARecogniserThatThrowsFailsThatFaceOnly), ARecogniserThatThrowsFailsThatFaceOnly),
                (nameof(ARecogniserThatDoesNotOpenIsTriedAgain), ARecogniserThatDoesNotOpenIsTriedAgain),
                (nameof(StopClosesTheRecogniserAndARestartKeepsPeopleAndGroups), StopClosesTheRecogniserAndARestartKeepsPeopleAndGroups),
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
            ? $"PASS people detector: PeopleDetector.cs reports the faces and people a camera shows, holds seen, writes last seen sparingly, fails a check without a frame, and recognises, groups and learns faces ({VisionTests.LocalLibrary})"
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
        string vision = "", list = "", people = "";

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

        /// <summary>
        /// The rig, with library <c>vision</c> of <paramref name="vision"/> (else <see cref="VisionTests.LocalSource"/>),
        /// <c>people</c> of <c>nodes/People.cs</c> and <c>people_detector</c> of <paramref name="people"/> (else as committed).
        /// </summary>
        public static async Task<Rig> Start(string name, Func<LoopbackServer, string>? vision = null, string? people = null)
        {
            var rig = new Rig(name);
            try
            {
                await rig.Api.Init();
                rig.Api.SetEntityDescriptions(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ha-entity-components.json")), started: true);
                rig.vision = (vision ?? VisionTests.LocalSource)(rig.Server);
                rig.list = CommittedPeople();
                rig.people = people ?? Committed();
                foreach (var (id, file, source) in new[] { ("vision", "Vision.cs", rig.vision), ("people", "People.cs", rig.list), ("people_detector", "PeopleDetector.cs", rig.people) })
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

        /// <summary>The three libraries as saved, previewed as the manager compiles them, with the entity descriptions.</summary>
        public NodeLibraryPreview Preview(params CustomNodeSource[] nodes) =>
            new NodeLibraryCompiler(NodeLibraryFixture.EntityProvider).Preview([new("vision", vision, 1), new("people", list, 1), new("people_detector", people, 1)], nodes);

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

        public UserScriptManager Scripts => (UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Api)!;

        /// <summary>The people list's shared store, as another definition's detector opens it, from the manager's stores.</summary>
        public SharpData Shared() => Scripts.Data!.OpenShared(PeopleStore, new DataUser("tests", "tests", action => Task.Run(action)));

        /// <summary>The private store of the device <c>door</c> of <paramref name="definition"/>, its <c>Data</c>, from the manager's stores.</summary>
        public SharpData DeviceData(string definition = "porch") => Scripts.Data!.OpenDevice(new DataUser(definition, "door", action => Task.Run(action)));

        /// <summary>The running device <c>door</c> of <paramref name="definition"/>: the one its camera is on now.</summary>
        public SharpDevice Detector(string definition = "porch")
        {
            Check(Scripts.Entities.TryGet($"{definition}:door:camera", out _, out var camera) && camera.AddedTo != null, $"{definition}:door:camera runs on its device");
            return camera!.AddedTo!;
        }

        /// <summary>
        /// What is drawn on the picture the camera of <paramref name="definition"/> shows now, in order: each drawing's kind
        /// (<c>Rectangle</c>, <c>Circle</c> or <c>Label</c>), a label's text, and its colour; none before it shows a picture.
        /// Read from a copy of the current frame, which has its drawings until a request makes its JPEG; nothing here asks
        /// for one.
        /// </summary>
        public (string Kind, string? Text, SharpColor Color)[] Drawn(string definition = "porch")
        {
            Check(Scripts.Entities.TryGet($"{definition}:door:camera", out _, out var entity) && entity is SharpCamera, $"{definition}:door:camera is a camera");
            var handed = ((SharpCamera)entity!).Frames.Handed();
            handed.Good?.Image.Dispose();
            // None before the first check has shown one.
            if (handed.Current is not { } current) return [];
            using var image = current.Image;
            Check(current.Jpeg == null, $"{definition}:door:camera's picture was not encoded yet, so it has its drawings");
            var drawings = (System.Collections.IEnumerable)typeof(SharpImage).GetField("drawings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(image)!;
            return [.. drawings.Cast<object>().Select(d => (d.GetType().Name, d.GetType().GetProperty("Text")?.GetValue(d) as string, (SharpColor)d.GetType().GetProperty("Color")!.GetValue(d)!))];
        }

        /// <summary>The texts drawn on the picture the camera of <paramref name="definition"/> shows now.</summary>
        public string[] Labels(string definition = "porch") => [.. Drawn(definition).Where(d => d.Kind == "Label").Select(d => d.Text!)];

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

    /// <summary>The device's arguments: the camera, a check every 0.1 s, seen held for 1 s, and <paramref name="more"/>, each a number or a bool.</summary>
    static JsonObject Arguments(string camera = "camera.porch", params (string Key, object Value)[] more)
    {
        var arguments = new JsonObject
        {
            ["Camera"] = VisualExecutionTests.Lit("entity_id:camera", camera),
            ["CheckEvery"] = VisualExecutionTests.Lit("number", 0.1),
            ["StayOnFor"] = VisualExecutionTests.Lit("number", 1),
        };
        foreach (var (key, value) in more) arguments[key] = VisualExecutionTests.Lit(value is bool ? "boolean" : "number", value);
        return arguments;
    }

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
        var preview = new NodeLibraryCompiler(NodeLibraryFixture.EntityProvider).Preview([new("vision", VisionTests.Committed(), 1), new("people", CommittedPeople(), 1), new("people_detector", Committed(), 1)]);
        Check(preview.Valid && preview.Diagnostics.IsEmpty, "Vision.cs, People.cs and PeopleDetector.cs preview valid: " + string.Join(" | ", preview.Diagnostics.Select(d => $"{d.SourceId} {d.StartLine}:{d.StartColumn} {d.Message}")));

        await using var rig = await Rig.Start("entities", Elsewhere);
        // The C# device's inputs: the eleven settings, with their bounds; the camera alone is required.
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
            "\"DetectPeople\",\"Detect people\",\"boolean\",-,-,-,-",
            "\"RecogniseFaces\",\"Recognise faces\",\"boolean\",-,-,-,-",
            "\"MatchScore\",\"Match score\",\"number\",-,0.3,0.9,-",
            "\"LearnNewLooks\",\"Learn new looks\",\"boolean\",-,-,-,-",
            "\"KeepUnknownFacesFor\",\"Keep unknown faces for\",\"integer\",-,1,365,-",
            "\"NamesOnTheCamera\",\"Names on the camera\",\"boolean\",-,-,-,-",
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
                    var lines = new List<string> { string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{CheckEvery}|{StayOnFor}|{FaceScore}|{PersonScore}|{DetectPeople}|{RecogniseFaces}|{MatchScore}|{LearnNewLooks}|{KeepUnknownFacesFor}|{NamesOnTheCamera}|{Manufacturer}|{Model}") };
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
            "0.5|10|0.7|0.45|True|True|0.5|True|7|True|HassSharp|People detector",
            "camera||PeopleDetectorCamera|||False|",
            "face_seen|Face seen|SharpBinarySensor|occupancy||False|",
            "person_seen|Person seen|SharpBinarySensor|occupancy||False|",
            "faces|Faces|SharpSensor||mdi:face-recognition|False|measurement",
            "people|People|SharpSensor||mdi:account-multiple|False|measurement",
            "last_face_seen|Last face seen|SharpSensor|timestamp||True|",
            "last_person_seen|Last person seen|SharpSensor|timestamp||True|",
        ];
        Check(seen.Split('\n').SequenceEqual(entities), "the defaults, the metadata and the entities in order:\n" + seen);
        Console.WriteLine("PASS people detector: the libraries preview valid, the device's eleven inputs have their bounds and defaults, a missing camera and a check every 0.05 s are refused, and it has its seven entities in order");
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

    // ---- Recognition, with a fake recogniser ----

    /// <summary>A face the detector finds on the group photo's frame: its box, the pixels between its eyes and whether it looks at the camera.</summary>
    internal sealed record Spot(float X, float Y, float Width, float Height, float Eyes, bool Frontal)
    {
        /// <summary>Clear enough to embed by the default rules.</summary>
        public bool Usable => Eyes >= (double)PeopleTests.DefaultRules.UsableEyes && Frontal;
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X:0},{Y:0}) {Width:0}×{Height:0}, eyes {Eyes:0.0}{(Frontal ? "" : ", profile")}");
    }

    static Spot[]? spots;

    /// <summary>The faces of the group photo as the device's detector finds them, at 640 on the fake's frame: the largest (by the pixels between the eyes) first.</summary>
    static async Task<Spot[]> Spots()
    {
        if (spots != null) return spots;
        await using var detection = await VisionTests.SetUp.Start();
        using var frame = SharpImage.FromPixels(640, 426, SharpPixelFormat.Yuv420, Group);
        var found = VisionTests.Items(VisionTests.Loaded.Faces(detection.Detector, frame, 640, 0.7));
        Check(found.Length == 8, $"group.jpg's frame has 8 faces at 640, not {found.Length}");
        return spots = [.. found
            .Select(face => new Spot(VisionTests.Get(face, "X"), VisionTests.Get(face, "Y"), VisionTests.Get(face, "Width"), VisionTests.Get(face, "Height"), detection.Vision.EyeDistance(face), detection.Vision.Frontal(face)))
            .OrderByDescending(spot => spot.Eyes)];
    }

    /// <summary>
    /// The code of a C# device <paramref name="type"/> deriving <c>PeopleDetector</c> whose recogniser is the fake: opening
    /// it, each embedding and its closing ask the test's <see cref="Fake"/>, through hooks it sets in the
    /// <see cref="AppContext"/>, naming the class.
    /// </summary>
    static string Faking(string type) => $$"""
        public class {{type}} : PeopleDetector
        {
            static object Hook(string name) => AppContext.GetData("HassSharp.PeopleDetectorTests." + name) ?? throw new InvalidOperationException("The test set no hook " + name);

            protected override Task<IFaceRecognizer> OpenRecognizer()
            {
                ((Action<string>)Hook("Open"))("{{type}}");
                return Task.FromResult<IFaceRecognizer>(new Fake());
            }

            sealed class Fake : IFaceRecognizer
            {
                public FaceEmbedding Embed(SharpImage image, DetectedFace face)
                {
                    var row = ((Func<string, float[], float[]>)Hook("Embed"))("{{type}}", [face.X, face.Y, face.Width, face.Height]);
                    return new FaceEmbedding(row[..128], row[128]);
                }

                public void Dispose() => ((Action<string>)Hook("Dispose"))("{{type}}");
            }
        }
        """;

    /// <summary>
    /// The test's side of the fake recogniser of <see cref="Faking"/>. A face's embedding is decided by its place in the
    /// picture: the spot (<see cref="Spots"/>) whose box is nearest, and the vector the test gave that spot, else a
    /// stranger's of its own. It counts the opens, the embeddings and the closes of each class, and an embedding asked of
    /// a recogniser already closed.
    /// </summary>
    sealed class Fake : IDisposable
    {
        const string Hooks = "HassSharp.PeopleDetectorTests.";
        // Guards everything below, which the hooks change from the devices' threads.
        readonly Lock gate = new();
        readonly Spot[] spots;
        readonly Dictionary<int, (float[] Values, float Fit)> table = [];
        readonly HashSet<int> failing = [];
        readonly List<(string Type, int Spot)> embedded = [];
        readonly Dictionary<string, int> attempts = [], opened = [], closed = [];
        int failOpens, usedClosed;
        DateTimeOffset? firstOpened;

        public Fake(Spot[] spots)
        {
            this.spots = spots;
            AppContext.SetData(Hooks + "Open", (Action<string>)Open);
            AppContext.SetData(Hooks + "Embed", (Func<string, float[], float[]>)Embed);
            AppContext.SetData(Hooks + "Dispose", (Action<string>)(type => { lock (gate) closed[type] = closed.GetValueOrDefault(type) + 1; }));
        }

        /// <summary>The face at <paramref name="spot"/> embeds as <paramref name="values"/>, with that fit.</summary>
        public void Embeds(int spot, float[] values, float fit = 1.5f) { lock (gate) table[spot] = (values, fit); }

        /// <summary>Embedding the face at <paramref name="spot"/> throws.</summary>
        public void Fails(int spot) { lock (gate) failing.Add(spot); }

        /// <summary>The next <paramref name="times"/> opens throw.</summary>
        public void FailsToOpen(int times) { lock (gate) failOpens = times; }

        /// <summary>The spots embedded, in order, by the class <paramref name="type"/> (by any when null).</summary>
        public int[] Embedded(string? type = null) { lock (gate) return [.. embedded.Where(e => type == null || e.Type == type).Select(e => e.Spot)]; }

        public int Attempts(string type) { lock (gate) return attempts.GetValueOrDefault(type); }
        public int Opened(string type) { lock (gate) return opened.GetValueOrDefault(type); }
        public int Closed(string type) { lock (gate) return closed.GetValueOrDefault(type); }
        public int UsedClosed { get { lock (gate) return usedClosed; } }
        public DateTimeOffset? FirstOpened { get { lock (gate) return firstOpened; } }

        void Open(string type)
        {
            lock (gate)
            {
                attempts[type] = attempts.GetValueOrDefault(type) + 1;
                if (failOpens > 0)
                {
                    failOpens--;
                    throw new InvalidOperationException("The fake model is missing");
                }
                opened[type] = opened.GetValueOrDefault(type) + 1;
                firstOpened ??= DateTimeOffset.UtcNow;
            }
        }

        float[] Embed(string type, float[] box)
        {
            var (x, y, width, height) = (box[0], box[1], box[2], box[3]);
            var spot = Enumerable.Range(0, spots.Length)
                .MinBy(i => MathF.Abs(spots[i].X + spots[i].Width / 2 - (x + width / 2)) + MathF.Abs(spots[i].Y + spots[i].Height / 2 - (y + height / 2)));
            (float[] Values, float Fit) row;
            lock (gate)
            {
                embedded.Add((type, spot));
                if (opened.GetValueOrDefault(type) == closed.GetValueOrDefault(type)) usedClosed++;
                if (failing.Contains(spot)) throw new InvalidOperationException("The fake recogniser failed");
                row = table.TryGetValue(spot, out var given) ? given : (PeopleTests.Unit(1000 + spot), 1.5f);
            }
            return [.. row.Values, row.Fit];
        }

        public void Dispose()
        {
            foreach (var hook in new[] { "Open", "Embed", "Dispose" }) AppContext.SetData(Hooks + hook, null);
        }
    }

    // Names no log line may hold; distinctive, so that no other line holds them by chance.
    const string Ana = "Ana Horvat", Ivan = "Ivan Babic";

    /// <summary>A people list of the people store, as another detector has it: People.cs compiled by <see cref="PeopleTests"/>.</summary>
    static dynamic PeopleOf(Rig rig) => PeopleTests.List(rig.Shared());

    /// <summary>Adds a person to the list with one named sample of <paramref name="seed"/>'s vector, seen elsewhere.</summary>
    static dynamic Named(dynamic list, string name, int seed) =>
        list.Add(name, PeopleTests.Samples(PeopleTests.NewSample(PeopleTests.Unit(seed), PeopleTests.Crop(seed), DateTimeOffset.UtcNow, "camera.elsewhere")));

    /// <summary>The person of the name, as the list has it after reading the store again.</summary>
    static dynamic Reread(dynamic list, string name)
    {
        list.Reload();
        return ((object[])PeopleTests.Items(list.People)).Single(p => ((dynamic)p).Name == name);
    }

    static object[] LearnedOf(dynamic person) => ((object[])PeopleTests.Items(person.Samples)).Where(s => ((dynamic)s).Learned).ToArray();

    /// <summary>A protected member of the running PeopleDetector: <c>People</c> or <c>Unknown</c>.</summary>
    static dynamic Member(SharpDevice device, string name)
    {
        var type = device.GetType();
        while (type != null && type.Name != "PeopleDetector") type = type.BaseType;
        return type?.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(device) ?? throw new Exception($"PeopleDetector has no {name}");
    }

    /// <summary>The unknown groups the running detector of <paramref name="definition"/> shows, the newest sighting first.</summary>
    static dynamic[] Shown(Rig rig, string definition = "porch") => [.. VisionTests.Items((object)Member(rig.Detector(definition), "Unknown").Shown).Select(group => (dynamic)group)];

    static bool IsScore(string text) => System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d\.\d\d$");

    static async Task RecognitionOffOpensNothingAndWritesNothing()
    {
        using var fake = new Fake(await Spots());
        Show();
        await using var rig = await Rig.Start("recognition-off");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("RecogniseFaces", false)), Faking("Porch")));
        await Until(() => rig.Latest("faces")?.Number == 8 && rig.Latest("people")?.Number == 7 && rig.Latest("face_seen")?.IsOn == true && rig.Latest("person_seen")?.IsOn == true,
            "the group photo publishes 8 faces, 7 people and both seen: " + string.Join(" | ", Keys.Skip(1).Select(k => rig.Shown(k))));
        await Task.Delay(1000);
        Check(fake.Attempts("Porch") == 0 && fake.Embedded().Length == 0, $"the recogniser is never opened nor asked: {fake.Attempts("Porch")} opens, {fake.Embedded().Length} faces");
        var (shared, own) = (rig.Shared().Keys(), rig.DeviceData().Keys());
        Check(shared.Count == 0 && own.Count == 0, "no store key is written: " + string.Join(", ", shared.Concat(own)));
        var labels = rig.Labels();
        Check(labels.Length == 15 && labels.All(IsScore), "the camera draws each face's and person's score, no name: " + string.Join(", ", labels));
        Console.WriteLine("PASS people detector: with Recognise faces off, nothing is opened or stored, and the faces and people are counted and drawn as before");
    }

    static async Task DetectPeopleOffLeavesThePersonEntitiesUnavailable()
    {
        Show();
        await using var rig = await Rig.Start("people-off");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: [("DetectPeople", false), ("RecogniseFaces", false)])));
        await Until(() => rig.Latest("faces")?.Number == 8 && rig.Latest("face_seen")?.IsOn == true && rig.Latest("last_face_seen")?.Time != null,
            "faces are still seen: " + rig.Shown("faces") + " | " + rig.Shown("face_seen"));
        await Task.Delay(500);
        foreach (var key in new[] { "person_seen", "people", "last_person_seen" })
            Check(rig.Latest(key) is { Available: false } && rig.Of(key).All(p => p.Value == null), $"{key} is unavailable and never has a value: " + rig.Shown(key));
        var drawn = rig.Drawn();
        var cyan = SharpColor.FromRgb(0, 200, 255);
        Check(drawn.All(d => d.Color != cyan) && drawn.Count(d => d.Kind == "Rectangle") == 8, "the camera has the 8 faces' boxes and no person's: " + string.Join(", ", drawn.Select(d => d.Kind + " " + d.Color)));
        Console.WriteLine("PASS people detector: with Detect people off, Person seen, People and Last person seen are unavailable, no person is drawn, and faces still work");
    }

    static async Task AFaceThatIsNotUsableIsNeverMatched()
    {
        var spots = await Spots();
        var small = Array.FindIndex(spots, spot => spot.Frontal && !spot.Usable);
        var profile = Array.FindIndex(spots, spot => !spot.Frontal);
        Check(small >= 0 && profile >= 0 && spots.Count(spot => spot.Usable) >= 5 && spots[0].Usable,
            "the group photo has a face too small, a profile and five usable faces, the largest among them: " + string.Join(" | ", spots.Select(s => s.ToString())));
        using var fake = new Fake(spots);
        Show();
        await using var rig = await Rig.Start("unusable");
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        // Each of the three would be Ana, but the largest fits badly, and the other two are never embedded.
        var ana = PeopleTests.At(PeopleTests.Unit(1), 0.8, 11);
        fake.Embeds(0, ana, fit: 9);
        fake.Embeds(small, ana);
        fake.Embeds(profile, ana);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => fake.Embedded().Length >= 4 * 4, $"four checks embed faces: {fake.Embedded().Length} so far | " + rig.Lines(), 10);
        var embedded = fake.Embedded();
        Check(!embedded.Contains(small) && !embedded.Contains(profile) && embedded.Contains(0), "the small face and the profile are never embedded, the badly fitting face is: " + string.Join(",", embedded));
        await Until(() => Shown(rig).Length == 3, "the three other faces each become a group: " + Shown(rig).Length);
        Check(rig.Latest("faces")?.Number == 8, "the 8 faces are counted: " + rig.Shown("faces"));
        var drawn = rig.Drawn();
        Check(drawn.Count(d => d.Kind == "Rectangle" && d.Color == SharpColor.Green) == 8 && drawn.Where(d => d.Kind == "Label" && d.Color == SharpColor.White).All(d => IsScore(d.Text!)),
            "the 8 faces are drawn with their scores: " + string.Join(", ", rig.Labels()));
        Check(Reread(list, Ana).LastSeen == null, "Ana is never recognised");
        await Task.Delay(500);
        Check(Shown(rig).Length == 3, "the badly fitting face is never grouped: " + Shown(rig).Length);
        Console.WriteLine("PASS people detector: a face too small, a profile and a face that fits badly are counted and drawn; the first two are never embedded, and none is matched or grouped");
    }

    static async Task AtMostFourFacesAreEmbeddedTheLargestFirst()
    {
        var spots = await Spots();
        using var fake = new Fake(spots);
        Show();
        await using var rig = await Rig.Start("four");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(), Faking("Porch")));
        await Until(() => fake.Embedded().Length >= 3 * 4, $"three checks embed faces: {fake.Embedded().Length} so far | " + rig.Lines(), 10);
        int[] expected = [.. Enumerable.Range(0, spots.Length).Where(i => spots[i].Usable).Take(4)];
        var checks = fake.Embedded().Take(12).Chunk(4).ToArray();
        Check(checks.All(check => check.SequenceEqual(expected)), $"each check embeds the four largest usable faces, the largest first ({string.Join(",", expected)}): " + string.Join(" | ", checks.Select(c => string.Join(",", c))));
        Console.WriteLine($"PASS people detector: of {spots.Count(s => s.Usable)} usable faces, a check embeds the four largest, the largest first");
    }

    static async Task FacesAreRecognisedSuggestedOrGrouped()
    {
        using var fake = new Fake(await Spots());
        Show();
        await using var rig = await Rig.Start("recognised");
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        var ivan = Named(list, Ivan, 2);
        fake.Embeds(0, PeopleTests.At(PeopleTests.Unit(1), 0.8, 11));
        // Under the match score, over the suggest line: a maybe.
        fake.Embeds(1, PeopleTests.At(PeopleTests.Unit(2), 0.45, 12));
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => Shown(rig).Length > 0, "a group is shown: " + rig.Lines(), 10);
        Check(fake.Embedded().Length > 2 * 4, $"a group is shown only from the third check on: {fake.Embedded().Length} faces embedded");
        await Until(() => Shown(rig).Length == 3, "the maybe and the two strangers are each a shown group: " + Shown(rig).Length);
        var groups = Shown(rig);
        Check(groups.All(g => g.Sightings >= 3), "each is shown from its third sighting: " + string.Join(",", groups.Select(g => (int)g.Sightings)));
        Check(groups.Count(g => g.SuggestedPersonId == (string)ivan.Id) == 1 && groups.Count(g => g.SuggestedPersonId == null) == 2, "the maybe's group suggests Ivan, the strangers' none");
        var seen = Reread(list, Ana);
        Check(seen.LastSeen != null && seen.LastSeenAt == "camera.porch", $"Ana is seen at camera.porch: {seen.LastSeen} at {seen.LastSeenAt}");
        Check(Reread(list, Ivan).LastSeen == null, "Ivan, only suggested, is not seen");
        Console.WriteLine("PASS people detector: a named person's face is recognised and seen at the camera, a maybe is grouped with its suggestion, and an unknown face is a shown group from its third check");
    }

    static async Task TheCameraShowsTheRecognisedName()
    {
        using var fake = new Fake(await Spots());
        Show();
        await using var rig = await Rig.Start("names");
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        Named(list, Ivan, 2);
        fake.Embeds(0, PeopleTests.At(PeopleTests.Unit(1), 0.8, 11));
        fake.Embeds(1, PeopleTests.At(PeopleTests.Unit(2), 0.45, 12));
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await rig.Saved("plain", rig.Definition("Plain people", "Plain", Arguments(more: [("LearnNewLooks", false), ("NamesOnTheCamera", false)]), Faking("Plain")));
        await Until(() => rig.Labels("porch").Contains(Ana), "the porch's camera names Ana: " + string.Join(", ", rig.Labels("porch")), 10);
        await Until(() => fake.Embedded("Plain").Length >= 3 * 4, "the plain detector recognises too", 10);
        var named = rig.Labels("porch");
        Check(named.Count(l => l == Ana) == 1 && named.Where(l => l != Ana).All(IsScore), "Ana's name is above her box, the others' scores, and no suggestion: " + string.Join(", ", named));
        var plain = rig.Labels("plain");
        Check(plain.Length == 15 && plain.All(IsScore), "with Names on the camera off, only scores: " + string.Join(", ", plain));
        Console.WriteLine("PASS people detector: the camera draws a recognised face's name and not a maybe's, and no name with Names on the camera off");
    }

    static async Task ANewLookIsLearnedOnceAndOnlyWhenAllowed()
    {
        var spots = await Spots();
        var learnEyes = (double)PeopleTests.DefaultRules.LearnEyes;
        Check(spots[0].Eyes >= learnEyes && spots[1].Eyes >= learnEyes, "the two largest faces are large enough to learn: " + spots[0] + " | " + spots[1]);
        using var fake = new Fake(spots);
        Show();
        await using var rig = await Rig.Start("learn");
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        // Both are Ana beyond doubt, and each adds to her one sample.
        var first = PeopleTests.At(PeopleTests.Unit(1), 0.8, 21);
        fake.Embeds(0, first);
        fake.Embeds(1, PeopleTests.At(PeopleTests.Unit(1), 0.75, 22));

        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => fake.Embedded("Porch").Length >= 5 * 4, "five checks with Learn new looks off", 10);
        Check(Reread(list, Ana).LastSeen != null && LearnedOf(Reread(list, Ana)).Length == 0, "with Learn new looks off Ana is seen and nothing is learned");
        await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = false });

        await rig.Saved("learner", rig.Definition("Learner people", "Learner", Arguments(), Faking("Learner")));
        await Until(() => LearnedOf(Reread(list, Ana)).Length > 0, "Ana gains a learned sample: " + rig.Lines(), 10);
        var from = fake.Embedded("Learner").Length;
        await Until(() => fake.Embedded("Learner").Length >= from + 5 * 4, "five checks more", 10);
        var learned = LearnedOf(Reread(list, Ana));
        Check(learned.Length == 1, $"two faces of Ana in each check, and one learned sample: {learned.Length}");
        dynamic sample = learned[0];
        Check(sample.Camera == "camera.porch" && PeopleTests.Cosine((float[])sample.Embedding.Values, first) > 0.999, "the sample is the largest face, seen at camera.porch");
        var crop = (byte[])list.Crop((string)sample.Id);
        using var picture = SharpImage.Load(crop);
        Check(Math.Max(picture.Width, picture.Height) == 160, $"its crop is a JPEG 160 px on its long side: {picture.Width} × {picture.Height}");
        Console.WriteLine($"PASS people detector: with Learn new looks on, two faces of one person in a check add one learned sample, with its {picture.Width} × {picture.Height} crop; off, none");
    }

    static async Task GroupsOlderThanTheKeepTimeAreGoneAfterACheck()
    {
        using var fake = new Fake(await Spots());
        await using var rig = await Rig.Start("expire");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(), Faking("Porch")));
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks run: " + rig.Shown("faces"));
        await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = false });
        await Until(() => rig.Home.Logs(PyLogLevel.Debug).Contains("Porch people: People detector stopped"), "the detector stops");

        // While it is off, two shown groups: one last seen 10 days ago, past the 7 days kept; one a day ago.
        var groups = Activator.CreateInstance(PeopleTests.Type("FaceGroups"), rig.DeviceData(), "camera.porch", (object)PeopleTests.DefaultRules)!;
        var seen = groups.GetType().GetMethod("Seen")!;
        var now = DateTimeOffset.UtcNow;
        foreach (var (seed, ago) in new[] { (301, TimeSpan.FromDays(10)), (302, TimeSpan.FromDays(1)) })
            for (var i = 0; i < 3; i++)
                seen.Invoke(groups, new object?[] { (object)PeopleTests.Embedding(PeopleTests.Unit(seed)), (Func<byte[]>)(() => PeopleTests.Crop(seed)), 30.0, now - ago + TimeSpan.FromMinutes(i), null });
        var shown = VisionTests.Items(groups.GetType().GetProperty("Shown")!.GetValue(groups)!).Select(group => (dynamic)group).ToArray();
        Check(shown.Length == 2, "two groups are shown");
        var kept = shown.Single(group => group.Last > now - TimeSpan.FromDays(2));

        await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = true });
        await Until(() => Shown(rig).Length == 1, "the old group goes: " + rig.Lines(), 10);
        Check((string)Shown(rig)[0].Id == (string)kept.Id, "the group seen a day ago is kept");
        var keys = rig.DeviceData().Keys("groups/");
        Check(keys.All(key => key.StartsWith($"groups/{kept.Id}", StringComparison.Ordinal)), "nothing of the old group is left in the store: " + string.Join(", ", keys));
        Console.WriteLine("PASS people detector: a shown group last seen longer ago than Keep unknown faces for is gone after a check, and one seen since is kept");
    }

    static async Task ARecogniserThatThrowsFailsThatFaceOnly()
    {
        using var fake = new Fake(await Spots());
        Show();
        await using var rig = await Rig.Start("throws");
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        fake.Fails(0);
        fake.Embeds(1, PeopleTests.At(PeopleTests.Unit(1), 0.8, 11));
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => Reread(list, Ana).LastSeen != null, "the next face is still recognised: " + rig.Lines(), 10);
        var from = fake.Embedded().Length;
        await Until(() => fake.Embedded().Length >= from + 5 * 4, "five checks more", 10);
        Check(rig.Latest("faces")?.Number == 8 && rig.Latest("people")?.Number == 7 && rig.Latest("face_seen")?.IsOn == true && rig.Latest("person_seen")?.IsOn == true,
            "the checks still set the entities: " + string.Join(" | ", Keys.Skip(1).Select(k => rig.Shown(k))));
        Check(rig.Labels().Contains(Ana), "the camera still names Ana");
        var failed = rig.Home.Logs(PyLogLevel.Warn).Where(l => l.Contains("could not be recognised", StringComparison.Ordinal)).ToArray();
        Check(failed is ["Porch people: A face could not be recognised: InvalidOperationException: The fake recogniser failed"], "one line for the failing face in six checks: " + string.Join(" | ", failed));
        Check(!rig.Home.Logs().Any(l => l.Contains(Ana, StringComparison.Ordinal) || l.Contains(Ivan, StringComparison.Ordinal)), "no line names anyone: " + rig.Lines());
        Check(rig.Home.Logs(PyLogLevel.Error).Length == 0, "no check fails: " + rig.Lines());
        Console.WriteLine($"PASS people detector: a face the recogniser fails on fails alone, logged once: {failed[0]}");
    }

    static async Task ARecogniserThatDoesNotOpenIsTriedAgain()
    {
        const string declared = "static readonly TimeSpan OpenAgainAfter = TimeSpan.FromMinutes(10);";
        var source = Committed();
        Check(source.Split(declared).Length == 2, "PeopleDetector.cs declares OpenAgainAfter once as 10 minutes");
        using var fake = new Fake(await Spots());
        fake.FailsToOpen(1);
        Show();
        await using var rig = await Rig.Start("reopen", people: source.Replace(declared, "static readonly TimeSpan OpenAgainAfter = TimeSpan.FromSeconds(1);"));
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        fake.Embeds(0, PeopleTests.At(PeopleTests.Unit(1), 0.8, 11));
        var saved = await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        Check(saved["active"]!.GetValue<bool>() && saved["activationError"] == null, "the definition starts though the recogniser does not open: " + saved.ToJsonString());
        await Until(() => fake.Opened("Porch") == 1, "the recogniser opens at the second try: " + rig.Lines(), 10);
        await Until(() => rig.Labels().Contains(Ana), "and recognises: " + string.Join(", ", rig.Labels()));
        var counted = rig.Of("faces").FirstOrDefault(p => p.Number == 8);
        Check(counted != null && counted.When < fake.FirstOpened, "faces are counted before it opens: " + rig.Shown("faces"));
        var said = rig.Home.Logs().Where(l => l.Contains("face recogniser", StringComparison.Ordinal)).ToArray();
        Check(said is ["Porch people: Faces are not recognised until the face recogniser opens, which is tried again later: The fake model is missing"]
            && rig.Home.Logs(PyLogLevel.Warn).Contains(said[0]), "one warning says why: " + string.Join(" | ", said));
        Check(fake.Attempts("Porch") == 2, $"two tries: {fake.Attempts("Porch")}");
        Console.WriteLine($"PASS people detector: a recogniser that does not open leaves the faces counted, is logged once ({said[0]}), and is tried again");
    }

    static async Task StopClosesTheRecogniserAndARestartKeepsPeopleAndGroups()
    {
        using var fake = new Fake(await Spots());
        Show();
        await using var rig = await Rig.Start("restart");
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        fake.Embeds(0, PeopleTests.At(PeopleTests.Unit(1), 0.8, 11));
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => Shown(rig).Length == 3, "the three strangers are shown groups: " + rig.Lines(), 10);
        string[] ids = [.. Shown(rig).Select(g => (string)g.Id).Order(StringComparer.Ordinal)];
        Check(fake.Opened("Porch") == 1 && fake.Closed("Porch") == 0, "one recogniser open");

        await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = false });
        await Until(() => fake.Closed("Porch") == 1, "the stop closes the recogniser");
        await Task.Delay(300);
        Check(fake.UsedClosed == 0, $"a closed recogniser is never asked: {fake.UsedClosed} times");

        await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = true });
        await Until(() => fake.Opened("Porch") == 2, "the restart opens a recogniser again");
        string[] again = [.. Shown(rig).Select(g => (string)g.Id).Order(StringComparer.Ordinal)];
        Check(again.SequenceEqual(ids), $"the restarted detector shows the same groups: {string.Join(",", ids)} then {string.Join(",", again)}");
        var people = VisionTests.Items((object)Member(rig.Detector(), "People").People);
        Check(people.Length == 1 && ((dynamic)people[0]).Name == Ana && ((object[])PeopleTests.Items(((dynamic)people[0]).Samples)).Length == 1, "and the same people");
        await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = false });
        await Until(() => fake.Closed("Porch") == 2, "the second stop closes the second recogniser");
        Check(fake.UsedClosed == 0, $"a closed recogniser is never asked: {fake.UsedClosed} times");
        Console.WriteLine("PASS people detector: Stop closes the recogniser, which no check uses after, and a restart keeps the people and the groups");
    }
}

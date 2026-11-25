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

    /// <summary>The People detector's own node, of the library saved as <c>people_detector</c>.</summary>
    const string ClassKind = "library:people_detector:global%3A%3ANodeLib.PeopleDetector";

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
                (nameof(ATurnedFaceIsComparedAsAFrontalOneIsButForAMaybe), ATurnedFaceIsComparedAsAFrontalOneIsButForAMaybe),
                (nameof(ATurnedMaybeAGroupIsAlikeToIsThatGroups), ATurnedMaybeAGroupIsAlikeToIsThatGroups),
                (nameof(ATurnedFaceHasWiderLimitsThanAFrontalOne), ATurnedFaceHasWiderLimitsThanAFrontalOne),
                (nameof(TheCameraShowsTheRecognisedName), TheCameraShowsTheRecognisedName),
                (nameof(TheCameraDrawsTheBoxesAndThePointsOnlyWhenAsked), TheCameraDrawsTheBoxesAndThePointsOnlyWhenAsked),
                (nameof(TheCameraPlaysTheSourceBetweenChecks), TheCameraPlaysTheSourceBetweenChecks),
                (nameof(TheDrawingsStayUntilTheNextCheck), TheDrawingsStayUntilTheNextCheck),
                (nameof(NothingIsDrawnBeforeTheFirstCheck), NothingIsDrawnBeforeTheFirstCheck),
                (nameof(AFailedCheckClearsTheDrawings), AFailedCheckClearsTheDrawings),
                (nameof(ACheckThatFailsLateClearsTheDrawings), ACheckThatFailsLateClearsTheDrawings),
                (nameof(AFrameOfAnotherSizeIsNotDrawnOn), AFrameOfAnotherSizeIsNotDrawnOn),
                (nameof(NoFrameIsMadeWhenNobodyAsks), NoFrameIsMadeWhenNobodyAsks),
                (nameof(StoppingWhileAFrameIsAwaitedEndsCleanly), StoppingWhileAFrameIsAwaitedEndsCleanly),
                (nameof(ANewLookIsLearnedOnceAndOnlyWhenAllowed), ANewLookIsLearnedOnceAndOnlyWhenAllowed),
                (nameof(ARemovedLearnedSampleIsNotLearnedAgainAtOnce), ARemovedLearnedSampleIsNotLearnedAgainAtOnce),
                (nameof(APersonIsGivenToOneFaceOfACheck), APersonIsGivenToOneFaceOfACheck),
                (nameof(TwoLookAlikesOfOneFrameAreNamedEachOnTheirOwnCard), TwoLookAlikesOfOneFrameAreNamedEachOnTheirOwnCard),
                (nameof(AFaceAlikeToAPersonNeverSeenWithThemIsThatPerson), AFaceAlikeToAPersonNeverSeenWithThemIsThatPerson),
                (nameof(ASaveDuringACheckIsNotUndoneByIt), ASaveDuringACheckIsNotUndoneByIt),
                (nameof(ASaveOnAnotherDetectorDuringACheckIsNotUndoneByIt), ASaveOnAnotherDetectorDuringACheckIsNotUndoneByIt),
                (nameof(PressesAndChecksOfTwoDetectorsNeverWaitOnEachOther), PressesAndChecksOfTwoDetectorsNeverWaitOnEachOther),
                (nameof(GroupsOlderThanTheKeepTimeAreGoneAfterACheck), GroupsOlderThanTheKeepTimeAreGoneAfterACheck),
                (nameof(ARecogniserThatThrowsFailsThatFaceOnly), ARecogniserThatThrowsFailsThatFaceOnly),
                (nameof(ARecogniserThatDoesNotOpenIsTriedAgain), ARecogniserThatDoesNotOpenIsTriedAgain),
                (nameof(StopClosesTheRecogniserAndARestartKeepsPeopleAndGroups), StopClosesTheRecogniserAndARestartKeepsPeopleAndGroups),
                (nameof(ThePageListsTheUnknownFacesToNameOrDismiss), ThePageListsTheUnknownFacesToNameOrDismiss),
                (nameof(TheUnknownCardsKeepTheirPlaces), TheUnknownCardsKeepTheirPlaces),
                (nameof(NamingAFaceIsASightingOfItsPerson), NamingAFaceIsASightingOfItsPerson),
                (nameof(SavingAFaceForAFullPersonKeepsTheirMostDifferentSamples), SavingAFaceForAFullPersonKeepsTheirMostDifferentSamples),
                (nameof(ASaveForSomeoneTheFacesLookUnlikeAsksFirst), ASaveForSomeoneTheFacesLookUnlikeAsksFirst),
                (nameof(ASaveAskedAboutAsksAgainLaterOrForOtherFaces), ASaveAskedAboutAsksAgainLaterOrForOtherFaces),
                (nameof(AStrangerIsDismissed), AStrangerIsDismissed),
                (nameof(ThePageListsThePeopleToRenameMergeAndDelete), ThePageListsThePeopleToRenameMergeAndDelete),
                (nameof(ThePageListsAPersonsSamplesToRemove), ThePageListsAPersonsSamplesToRemove),
                (nameof(APressForWhatIsGoneIsRefused), APressForWhatIsGoneIsRefused),
                (nameof(AChangeOnAnotherDetectorRedrawsThePage), AChangeOnAnotherDetectorRedrawsThePage),
                (nameof(TheRecognisedSensorSaysWhoIsInFrame), TheRecognisedSensorSaysWhoIsInFrame),
                (nameof(TheRecognisedSensorIsUnavailableUntilTheModelOpens), TheRecognisedSensorIsUnavailableUntilTheModelOpens),
                (nameof(AFailedCheckClearsNothingEarly), AFailedCheckClearsNothingEarly),
                (nameof(TheRecognisedSensorFollowsRenamesMergesAndDeletes), TheRecognisedSensorFollowsRenamesMergesAndDeletes),
                (nameof(TheLastRecognisedOutlivesARestart), TheLastRecognisedOutlivesARestart),
                (nameof(TheRecognisedStateIsCutAtAWholeName), TheRecognisedStateIsCutAtAWholeName),
                (nameof(PicturesOffKeepNothing), PicturesOffKeepNothing),
                (nameof(AnArrivalsPictureIsOnDiskWhenRecognisedNamesIt), AnArrivalsPictureIsOnDiskWhenRecognisedNamesIt),
                (nameof(TwoArrivalsOfOneCheckHaveTwoPictures), TwoArrivalsOfOneCheckHaveTwoPictures),
                (nameof(AtMostTwentyPicturesAreKeptAndNoneOlderThanADay), AtMostTwentyPicturesAreKeptAndNoneOlderThanADay),
                (nameof(APersonDeletedOrMergedAwayLosesTheirPictures), APersonDeletedOrMergedAwayLosesTheirPictures),
                (nameof(APictureThatCannotBeSavedLeavesRecognitionGoing), APictureThatCannotBeSavedLeavesRecognitionGoing),
                (nameof(TheLastPictureOutlivesARestartWhileItsFileIsThere), TheLastPictureOutlivesARestartWhileItsFileIsThere),
                (nameof(TheClassNodeDetectsAndRecognises), TheClassNodeDetectsAndRecognises),
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
            ? $"PASS people detector: PeopleDetector.cs reports the faces and people a camera shows, holds seen, writes last seen sparingly, fails a check without a frame, recognises, groups and learns faces, and has a page to name, merge and delete them ({VisionTests.LocalLibrary})"
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
        /// <summary>The state as text; null when it is none, or not text.</summary>
        public string? Text => Snapshot["values"]!["value"] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
        /// <summary>The attributes sent with the state.</summary>
        public JsonObject Attributes => Snapshot["attributes"]!.AsObject();
        public override string ToString() => $"{At.TotalSeconds:0.000} s {Snapshot.ToJsonString()}";
    }

    /// <summary>
    /// A frame a camera shows: its size, the luma of its first pixel when it is the video's own pixels (null for a JPEG),
    /// which tells each grey frame of the fake from the one before it, and what is drawn on it, in order: each drawing's
    /// kind (<c>Rectangle</c>, <c>Circle</c> or <c>Label</c>), a label's text, and its colour.
    /// </summary>
    sealed record Frame(int Width, int Height, int? Luma, (string Kind, string? Text, SharpColor Color)[] Drawn)
    {
        public override string ToString() => $"{Width} × {Height}, {(Luma is { } luma ? $"luma {luma}" : "a JPEG")}, {Drawn.Length} drawings";
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
                // At the push, before anything else: each picture a Recognised names is on disk by then.
                if (MediaRoot != null)
                    foreach (var snapshot in snapshots.Where(p => p.UniqueId.EndsWith(":recognised", StringComparison.Ordinal) && p.Available))
                        foreach (var path in Named(snapshot))
                            if (!File.Exists(OnDisk(path)))
                                lock (recording) missing.Add($"{path} in {snapshot}");
                lock (recording) pushed.AddRange(snapshots);
                push(batch);
            };
        }

        // Guarded by recording: each picture a Recognised named when it was pushed whose file was not on disk.
        readonly List<string> missing = [];

        /// <summary>
        /// Home Assistant's media folder, the entry <c>local</c>, when the rig was started with one (<see cref="Start"/>);
        /// a picture served at <c>/media/local/&lt;rest&gt;</c> is at <c>&lt;MediaRoot&gt;/&lt;rest&gt;</c>.
        /// </summary>
        public string? MediaRoot { get; private set; }

        /// <summary>Where on disk a path Home Assistant serves from the entry <c>local</c> is.</summary>
        public string OnDisk(string path)
        {
            const string local = "/media/local/";
            Check(MediaRoot != null && path.StartsWith(local, StringComparison.Ordinal), $"{path} is served from the media folder's entry local");
            return Path.Combine([MediaRoot!, .. path[local.Length..].Split('/')]);
        }

        /// <summary>The folder of the media files of the device <c>door</c> of <paramref name="definition"/>.</summary>
        public string MediaOf(string definition = "porch") => Path.Combine(MediaRoot ?? throw new Exception("the rig has no media folder"), "hass_sharp", definition, "door");

        /// <summary>The names of the media files of the device <c>door</c> of <paramref name="definition"/>, in ordinal order; none without its folder.</summary>
        public string[] MediaFiles(string definition = "porch")
        {
            var folder = MediaOf(definition);
            return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder).Select(file => Path.GetFileName(file)).Where(name => !name.StartsWith('.')).Order(StringComparer.Ordinal)] : [];
        }

        /// <summary>Each picture a pushed Recognised named whose file was not on disk at that push.</summary>
        public string[] Missing() { lock (recording) return [.. missing]; }

        /// <summary>The paths of pictures a Recognised snapshot names: in <c>pictures</c>, <c>last_recognised_picture</c> and <c>unknown_picture</c>.</summary>
        public static string[] Named(Pushed snapshot) =>
            [.. Pictures(snapshot).Append(LastPicture(snapshot)).Append(UnknownPicture(snapshot)).OfType<string>()];

        public LoopbackServer Server { get; }
        public EntityTestHome Home { get; }
        public HassSharpManager Api { get; }
        public TimeSpan Now => clock.Elapsed;
        /// <summary>How many snapshots were pushed so far: where to read on from.</summary>
        public int Mark { get { lock (recording) return pushed.Count; } }

        /// <summary>
        /// The rig, with library <c>vision</c> of <paramref name="vision"/> (else <see cref="VisionTests.LocalSource"/>),
        /// <c>people</c> of <c>nodes/People.cs</c> and <c>people_detector</c> of <paramref name="people"/> (else as committed);
        /// with Home Assistant's media folder, <see cref="MediaRoot"/>, when <paramref name="media"/>, else with none.
        /// </summary>
        public static async Task<Rig> Start(string name, Func<LoopbackServer, string>? vision = null, string? people = null, bool media = false)
        {
            var rig = new Rig(name);
            try
            {
                if (media) rig.MediaRoot = rig.Home.MediaFolder(rig.Api);
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

        /// <summary>
        /// A definition of one node <c>door</c> of the People detector's own kind, the class of library
        /// <c>people_detector</c>: no code and no signature, as the editor drops it.
        /// </summary>
        public static JsonObject ClassNodeDefinition(string name, JsonObject arguments) => new()
        {
            ["name"] = name, ["settings"] = new JsonArray(),
            ["logic"] = new JsonObject
            {
                ["kind"] = "graph",
                ["graph"] = new JsonObject
                {
                    ["version"] = 5, ["name"] = name, ["edges"] = new JsonArray(),
                    ["nodes"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "door", ["kind"] = ClassKind, ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 },
                        ["config"] = new JsonObject { ["key"] = "door", ["name"] = "Door", ["arguments"] = arguments },
                    }),
                },
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

        /// <summary>
        /// Makes the manager's pump send every snapshot posted for <c>{definition}:door:{key}</c>, also one equal to the
        /// last it sent, which it otherwise leaves out: so the snapshots recorded of it are its publishes (those within the
        /// pump's 25 ms of each other still come as one). Call it before the definition runs. It gives the pump's private
        /// field <c>sent</c> a comparer under which that id is never found.
        /// </summary>
        public void SendsEveryPublishOf(string key, string definition = "porch")
        {
            var field = typeof(EntityStatePump).GetField("sent", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("EntityStatePump has no field sent");
            var sent = (Dictionary<string, string>)field.GetValue(Scripts.Pump)!;
            field.SetValue(Scripts.Pump, new Dictionary<string, string>(sent, new NeverFound($"{definition}:door:{key}")));
        }

        /// <summary>Ordinal, but the id given is equal to nothing, not even itself.</summary>
        sealed class NeverFound(string id) : IEqualityComparer<string>
        {
            public bool Equals(string? x, string? y) => x != id && y != id && string.Equals(x, y, StringComparison.Ordinal);
            public int GetHashCode(string text) => StringComparer.Ordinal.GetHashCode(text);
        }

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

        /// <summary>The camera of <paramref name="definition"/>, while it runs.</summary>
        public SharpCamera Camera(string definition = "porch") =>
            Scripts.Entities.TryGet($"{definition}:door:camera", out _, out var entity) && entity is SharpCamera camera ? camera : throw new Exception($"{definition}:door:camera is not a running camera");

        /// <summary>
        /// The camera of <paramref name="definition"/> makes a frame, as it does when Home Assistant asks for one, up to its
        /// JPEG, which is not made, as its drawings are gone then: what it shows now. None before it shows a frame. It is
        /// waited for here, at most 10 s, so that a condition and a message can read it.
        /// </summary>
        public Frame? Frame(string definition = "porch")
        {
            var camera = Camera(definition);
            var making = Task.Run(camera.MakeFrame);
            if (!making.Wait(TimeSpan.FromSeconds(10))) throw new Exception($"{definition}:door:camera made no frame within 10 s");
            // As the host does with a frame it is given.
            using (var made = making.Result)
                if (made != null) camera.Show(made);
            var handed = camera.Frames.Handed();
            handed.Good?.Image.Dispose();
            if (handed.Current is not { } current) return null;
            using var image = current.Image;
            Check(current.Jpeg == null, $"{definition}:door:camera's picture was not encoded yet, so it has its drawings");
            var drawings = (System.Collections.IEnumerable)typeof(SharpImage).GetField("drawings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(image)!;
            var video = (PixelFrame?)typeof(SharpImage).GetField("frame", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(image);
            return new(image.Width, image.Height, video?.Data[0],
                [.. drawings.Cast<object>().Select(d => (d.GetType().Name, d.GetType().GetProperty("Text")?.GetValue(d) as string, (SharpColor)d.GetType().GetProperty("Color")!.GetValue(d)!))]);
        }

        /// <summary>What is drawn on a frame the camera of <paramref name="definition"/> makes now (<see cref="Frame"/>); nothing before it shows one.</summary>
        public (string Kind, string? Text, SharpColor Color)[] Drawn(string definition = "porch") => Frame(definition)?.Drawn ?? [];

        /// <summary>The texts drawn on a frame the camera of <paramref name="definition"/> makes now.</summary>
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

    /// <summary>
    /// Waits up to <paramref name="limit"/> (5 s unless given) for the condition, then fails with what
    /// <paramref name="what"/> says at that moment, so the message is the state it timed out in.
    /// </summary>
    static async Task Until(Func<bool> condition, Func<string> what, double limit = 5)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(limit)) throw new Exception("timed out waiting: " + what());
            await Task.Delay(5);
        }
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
        // The C# device's inputs: the fourteen settings, with their bounds; the camera alone is required.
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
            "\"KeepPictures\",\"Keep recognition pictures\",\"boolean\",-,-,-,-",
            "\"BoxesOnTheCamera\",\"Boxes on the camera\",\"boolean\",-,-,-,-",
            "\"FacePointsOnTheCamera\",\"Face points on the camera\",\"boolean\",-,-,-,-",
            "\"NamesOnTheCamera\",\"Names on the camera\",\"boolean\",-,-,-,-",
        ];
        Check(settings.Select(Shape).SequenceEqual(expected), "the device's inputs and their bounds: " + string.Join(" | ", settings.Select(Shape)));
        Check(settings.All(s => s["description"]?.GetValue<string>() is { Length: > 0 } && s["secret"]?.GetValue<bool>() == false), "each input has its summary as its description: " + signature.ToJsonString());
        // Match score's default, which the editor shows beside it, is the rules' default match score.
        var matchScore = settings.Single(s => s["key"]!.GetValue<string>() == "MatchScore");
        double rulesMatchScore = PeopleTests.DefaultRules.MatchScore;
        Check(matchScore["default"]?.GetValue<double>() == rulesMatchScore, string.Create(CultureInfo.InvariantCulture, $"Match score's default is the rules' {rulesMatchScore}: ") + matchScore.ToJsonString());

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
                    var lines = new List<string> { string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{CheckEvery}|{StayOnFor}|{FaceScore}|{PersonScore}|{DetectPeople}|{RecogniseFaces}|{MatchScore}|{LearnNewLooks}|{KeepUnknownFacesFor}|{KeepPictures}|{BoxesOnTheCamera}|{FacePointsOnTheCamera}|{NamesOnTheCamera}|{Manufacturer}|{Model}") };
                    foreach (var e in Entities)
                        lines.Add($"{e.Key}|{e.Name}|{e.GetType().Name}|{e.DeviceClass}|{e.Icon}|{e.RestoreState}|{(e as SharpSensor)?.StateClass}");
                    // A frame asked for before Start().
                    var frame = Entities.OfType<PeopleDetectorCamera>().Single().OnFrame?.Invoke();
                    lines.Add(frame == null ? "makes no frames" : frame.IsCompletedSuccessfully && frame.Result == null ? "no frame yet" : $"{frame.Status}");
                    AppContext.SetData("HassSharp.PeopleDetectorTests.Probe", string.Join("\n", lines));
                }
            }
            """;
        var probed = await rig.Saved("probe", rig.Definition("Probe people", "Probe", new JsonObject { ["Camera"] = VisualExecutionTests.Lit("entity_id:camera", "camera.porch") }, probe));
        Check(!probed["active"]!.GetValue<bool>(), "the probe does not start here: " + probed.ToJsonString());
        var seen = AppContext.GetData(ProbeKey) as string ?? throw new Exception("the probe's constructor did not run: " + rig.Lines());
        string[] entities =
        [
            "0.5|10|0.7|0.45|True|True|0.5|True|7|False|True|True|True|HassSharp|People detector",
            "camera||PeopleDetectorCamera|||False|",
            "face_seen|Face seen|SharpBinarySensor|occupancy||False|",
            "person_seen|Person seen|SharpBinarySensor|occupancy||False|",
            "faces|Faces|SharpSensor||mdi:face-recognition|False|measurement",
            "people|People|SharpSensor||mdi:account-multiple|False|measurement",
            "last_face_seen|Last face seen|SharpSensor|timestamp||True|",
            "last_person_seen|Last person seen|SharpSensor|timestamp||True|",
            "recognised|Recognised|SharpSensor||mdi:account-eye|False|",
            "no frame yet",
        ];
        Check(seen.Split('\n').SequenceEqual(entities), "the defaults, the metadata, the entities in order, and a frame asked for before the start:\n" + seen);
        Console.WriteLine("PASS people detector: the libraries preview valid, the device's fourteen inputs have their bounds and defaults, a missing camera and a check every 0.05 s are refused, and it has its eight entities in order");
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
            "porch:door:recognised sensor - -",
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

    /// <summary>
    /// A face the detector finds on the group photo's frame: its box, the pixels between its eyes, whether it looks at the
    /// camera by the frontal rule, and how far it is turned.
    /// </summary>
    internal sealed record Spot(float X, float Y, float Width, float Height, float Eyes, bool Frontal, float Turn)
    {
        /// <summary>Clear enough to embed by the default rules: large enough, and frontal or turned no further than the limit with its eyes a fifth of its box apart.</summary>
        public bool Usable => Eyes >= (double)PeopleTests.DefaultRules.UsableEyes && (Frontal || Turn <= TurnLimit && Eyes >= Width / 5);
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X:0},{Y:0}) {Width:0}×{Height:0}, eyes {Eyes:0.0}, turned {Turn:0.00}{(Frontal ? "" : ", not frontal")}");
    }

    /// <summary>The default rules' turn limit: the furthest a face the frontal rule turns away may be turned to be compared.</summary>
    static double TurnLimit => (double)PeopleTests.DefaultRules.TurnLimit;

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
            .Select(face => new Spot(VisionTests.Get(face, "X"), VisionTests.Get(face, "Y"), VisionTests.Get(face, "Width"), VisionTests.Get(face, "Height"), detection.Vision.EyeDistance(face), detection.Vision.Frontal(face), detection.Vision.Turn(face)))
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

            // The test's clock while it has one (Clock), else the system's.
            protected override DateTimeOffset Now => AppContext.GetData("HassSharp.PeopleDetectorTests.Now") is Func<string, DateTimeOffset> clock ? clock("{{type}}") : DateTimeOffset.UtcNow;

            // The faces as the test places them (Fake.Turns), while it has a hook for them.
            protected override IReadOnlyList<DetectedFace> FacesFound(IReadOnlyList<DetectedFace> found)
            {
                if (AppContext.GetData("HassSharp.PeopleDetectorTests.Faces") is not Func<string, float[], float[]> place) return found;
                return [.. found.Select(f =>
                {
                    var v = place("{{type}}", [f.X, f.Y, f.Width, f.Height, f.Score, f.RightEye.X, f.RightEye.Y, f.LeftEye.X, f.LeftEye.Y, f.NoseTip.X, f.NoseTip.Y,
                        f.RightMouthCorner.X, f.RightMouthCorner.Y, f.LeftMouthCorner.X, f.LeftMouthCorner.Y]);
                    return new DetectedFace(v[0], v[1], v[2], v[3], v[4], new VisionPoint(v[5], v[6]), new VisionPoint(v[7], v[8]), new VisionPoint(v[9], v[10]),
                        new VisionPoint(v[11], v[12]), new VisionPoint(v[13], v[14]));
                })];
            }

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
    /// a recogniser already closed. It also places the faces the detector finds: a spot the test turned has its nose tip
    /// moved onto the line between its eyes.
    /// </summary>
    sealed class Fake : IDisposable
    {
        const string Hooks = "HassSharp.PeopleDetectorTests.";
        // Guards everything below, which the hooks change from the devices' threads.
        readonly Lock gate = new();
        readonly Spot[] spots;
        readonly Dictionary<int, (float[] Values, float Fit)> table = [];
        readonly Dictionary<(string Type, int Spot), float> turns = [];
        readonly List<(string Type, int Spot, float[] Face)> placed = [];
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
            AppContext.SetData(Hooks + "Faces", (Func<string, float[], float[]>)Place);
        }

        /// <summary>
        /// The face at <paramref name="spot"/>, as the class <paramref name="type"/> finds it, is turned by
        /// <paramref name="turn"/>: its nose tip moved onto the line between its eyes, that many eye distances from their
        /// midpoint, towards the left eye when positive and the right eye when negative; so past an eye across the picture,
        /// however the head is tilted, when the turn is over 0.5. Its box, eyes and mouth corners stay.
        /// </summary>
        public void Turns(int spot, float turn, string type = "Porch") { lock (gate) turns[(type, spot)] = turn; }

        /// <summary>The faces placed so far: the class, the spot and the face's fifteen values as the class was given them.</summary>
        public (string Type, int Spot, float[] Face)[] Placed() { lock (gate) return [.. placed]; }

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

        /// <summary>
        /// The next embedding of <paramref name="spot"/> (of any spot when null) by the class <paramref name="type"/> (by
        /// any when null) blocks inside <c>Embed</c> until <see cref="Release"/>, or for 30 seconds at most. A hold held
        /// already is released first.
        /// </summary>
        public void HoldAt(int? spot = null, string? type = null)
        {
            Release();
            lock (gate) (hold, holdSpot, holdType, held) = (new ManualResetEventSlim(false), spot, type, false);
        }

        /// <summary>Whether an embedding is blocked inside <c>Embed</c> by <see cref="HoldAt"/> now.</summary>
        public bool Held { get { lock (gate) return held; } }

        /// <summary>Lets the embedding held go on, and holds no other.</summary>
        public void Release()
        {
            ManualResetEventSlim? releasing;
            lock (gate) (releasing, hold, held) = (hold, null, false);
            releasing?.Set();
        }

        ManualResetEventSlim? hold;
        int? holdSpot;
        string? holdType;
        bool held;

        // The spot whose box is nearest the box.
        int Nearest(float x, float y, float width, float height) => Enumerable.Range(0, spots.Length)
            .MinBy(i => MathF.Abs(spots[i].X + spots[i].Width / 2 - (x + width / 2)) + MathF.Abs(spots[i].Y + spots[i].Height / 2 - (y + height / 2)));

        float[] Place(string type, float[] face)
        {
            var spot = Nearest(face[0], face[1], face[2], face[3]);
            float turn;
            lock (gate)
                if (!turns.TryGetValue((type, spot), out turn)) return face;
            var (rx, ry, lx, ly) = (face[5], face[6], face[7], face[8]);
            float[] moved = [.. face];
            (moved[9], moved[10]) = ((rx + lx) / 2 + turn * (lx - rx), (ry + ly) / 2 + turn * (ly - ry));
            lock (gate) placed.Add((type, spot, moved));
            return moved;
        }

        float[] Embed(string type, float[] box)
        {
            var (x, y, width, height) = (box[0], box[1], box[2], box[3]);
            var spot = Nearest(x, y, width, height);
            (float[] Values, float Fit) row;
            ManualResetEventSlim? waiting = null;
            lock (gate)
            {
                embedded.Add((type, spot));
                if (opened.GetValueOrDefault(type) == closed.GetValueOrDefault(type)) usedClosed++;
                if (hold != null && !held && (holdSpot == null || holdSpot == spot) && (holdType == null || holdType == type)) (waiting, held) = (hold, true);
            }
            waiting?.Wait(TimeSpan.FromSeconds(30));
            lock (gate)
            {
                if (failing.Contains(spot)) throw new InvalidOperationException("The fake recogniser failed");
                row = table.TryGetValue(spot, out var given) ? given : (PeopleTests.Unit(1000 + spot), 1.5f);
            }
            return [.. row.Values, row.Fit];
        }

        public void Dispose()
        {
            Release();
            foreach (var hook in new[] { "Open", "Embed", "Dispose", "Faces" }) AppContext.SetData(Hooks + hook, null);
        }
    }

    /// <summary>
    /// The clock of the detectors of <see cref="Faking"/> while it exists, through a hook in the <see cref="AppContext"/>
    /// naming the class. It starts at the time it was made and moves on a millisecond at each reading (a check reads it
    /// once), so that each check has its own time, as the unknown groups need; it moves further only when the test moves
    /// it, so a hold ends when the test says. It counts the readings of each class.
    /// </summary>
    sealed class Clock : IDisposable
    {
        const string Hook = "HassSharp.PeopleDetectorTests.Now";
        // Guards everything below, which the hook reads and changes from the devices' threads.
        readonly Lock gate = new();
        readonly Dictionary<string, int> reads = [];
        DateTimeOffset now = DateTimeOffset.UtcNow;

        public Clock() => AppContext.SetData(Hook, (Func<string, DateTimeOffset>)Read);

        /// <summary>The time the next reading gives.</summary>
        public DateTimeOffset Now { get { lock (gate) return now; } }

        /// <summary>Moves the clock on by <paramref name="by"/>.</summary>
        public void Advance(TimeSpan by) { lock (gate) now += by; }

        /// <summary>How often the class <paramref name="type"/> has read the clock.</summary>
        public int Reads(string type) { lock (gate) return reads.GetValueOrDefault(type); }

        DateTimeOffset Read(string type)
        {
            lock (gate)
            {
                reads[type] = reads.GetValueOrDefault(type) + 1;
                var read = now;
                now += TimeSpan.FromMilliseconds(1);
                return read;
            }
        }

        public void Dispose() => AppContext.SetData(Hook, null);
    }

    /// <summary>Waits for <paramref name="count"/> more checks of the class <paramref name="type"/>, as its readings of <paramref name="clock"/> count them.</summary>
    static Task Checks(Clock clock, int count, string type = "Porch")
    {
        var from = clock.Reads(type);
        return Until(() => clock.Reads(type) >= from + count, $"{count} more checks of {type}", 10);
    }

    // Names no log line may hold; distinctive, so that no other line holds them by chance.
    const string Ana = "Ana Horvat", Ivan = "Ivan Babic";

    /// <summary>A people list of the people store, as another detector has it: People.cs compiled by <see cref="PeopleTests"/>.</summary>
    static dynamic PeopleOf(Rig rig) => PeopleTests.List(rig.Shared());

    /// <summary>Adds a person of the full name (<see cref="Names"/>) to the list with one named sample of <paramref name="seed"/>'s vector, seen elsewhere.</summary>
    static dynamic Named(dynamic list, string name, int seed)
    {
        var (first, last) = Names(name);
        return list.Add(first, last, PeopleTests.Samples(PeopleTests.NewSample(PeopleTests.Unit(seed), PeopleTests.Crop(seed), DateTimeOffset.UtcNow, "camera.elsewhere")));
    }

    /// <summary>A full name's first and last name: split at its first space; no last name without one.</summary>
    static (string First, string? Last) Names(string name) => name.IndexOf(' ') is var space and >= 0 ? (name[..space], name[(space + 1)..]) : (name, null);

    /// <summary>The first name of a full name, which the camera draws for a person whose first name no one else has.</summary>
    static string First(string name) => Names(name).First;

    /// <summary>The person of the full name, as the list has it after reading the store again.</summary>
    static dynamic Reread(dynamic list, string name)
    {
        list.Reload();
        return ((object[])PeopleTests.Items(list.People)).Single(p => ((dynamic)p).FullName == name);
    }

    /// <summary>Whether no one of the list has the full name, after reading the store again.</summary>
    static bool NoOne(dynamic list, string name)
    {
        list.Reload();
        return ((object[])PeopleTests.Items(list.People)).All(p => ((dynamic)p).FullName != name);
    }

    /// <summary>A pick field's value in a press: the option chosen, by its id.</summary>
    static object Picked(string id) => new { picked = id };

    /// <summary>A pick field's value in a press: the new person's form filled in with the full name's first and last name (<see cref="Names"/>).</summary>
    static object NewPerson(string name)
    {
        var (first, last) = Names(name);
        return new { @new = new { first_name = first, last_name = last ?? "" } };
    }

    static object[] LearnedOf(dynamic person) => ((object[])PeopleTests.Items(person.Samples)).Where(s => ((dynamic)s).Learned).ToArray();

    /// <summary>A protected member of the running PeopleDetector: <c>People</c> or <c>Unknown</c>.</summary>
    static dynamic Member(SharpDevice device, string name)
    {
        var type = device.GetType();
        while (type != null && type.Name != "PeopleDetector") type = type.BaseType;
        return type?.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(device) ?? throw new Exception($"PeopleDetector has no {name}");
    }

    /// <summary>The unknown groups the running detector of <paramref name="definition"/> shows, in the order they were first shown.</summary>
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
        Check(Page(rig) == null, "the detector has no page: " + Page(rig)?.ToJsonString());
        Check(rig.Of("recognised").Length > 0 && rig.Of("recognised").All(p => !p.Available && p.Value == null), "Recognised is unavailable and never has a value: " + rig.Shown("recognised"));
        var counts = await rig.Ok(rig.Api.DeviceUiRequest, new { operation = "counts" });
        Check(counts["attention"]!["porch"] == null, "and nothing waits: " + counts.ToJsonString());
        Console.WriteLine("PASS people detector: with Recognise faces off, nothing is opened or stored, the detector has no page, Recognised is unavailable, and the faces and people are counted and drawn as before");
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
        Check(small >= 0 && profile >= 0 && spots[profile].Turn > TurnLimit && spots.Count(spot => spot.Usable) >= 5 && spots[0].Usable && spots[0].Frontal,
            "the group photo has a face too small, a profile turned beyond the limit and five usable faces, the largest among them and frontal: " + string.Join(" | ", spots.Select(s => s.ToString())));
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

    /// <summary>Whether the nose tip of a face's fifteen values (<see cref="Fake.Placed"/>) is beside both eyes across the picture, as the frontal rule turns a face away.</summary>
    static bool PastTheFrontalRule(float[] face) => face[9] < MathF.Min(face[5], face[7]) || face[9] > MathF.Max(face[5], face[7]);

    // The same faces seen by two detectors: on the porch the four largest usable are turned, three of them past the frontal
    // rule but inside the limit and one beyond it; the plain detector sees them as they are. A turned face of Ana is
    // recognised and learned, a turned stranger is an unknown face as a frontal one is, and the face beyond the limit is
    // never embedded, so the next face is; but a turned face that is only a maybe of Ivan is neither grouped nor counted,
    // where the plain detector groups it with Ivan suggested and counts it.
    static async Task ATurnedFaceIsComparedAsAFrontalOneIsButForAMaybe()
    {
        var spots = await Spots();
        int[] usable = [.. Enumerable.Range(0, spots.Length).Where(i => spots[i].Usable)];
        Check(usable.Length >= 5 && spots[usable[0]].Eyes >= (double)PeopleTests.DefaultRules.LearnEyes,
            "the group photo has five usable faces, the largest large enough to learn: " + string.Join(" | ", spots.Select(s => s.ToString())));
        var (ana, stranger, maybe, beyond, fifth) = (usable[0], usable[1], usable[2], usable[3], usable[4]);
        var inside = (float)TurnLimit - 0.03f;
        using var fake = new Fake(spots);
        fake.Embeds(ana, FaceOf(1));
        fake.Embeds(maybe, PeopleTests.At(PeopleTests.Unit(2), 0.45, 12));
        fake.Turns(ana, inside);
        fake.Turns(stranger, -inside);
        fake.Turns(maybe, inside);
        fake.Turns(beyond, (float)TurnLimit + 0.2f);
        Show();
        await using var rig = await Rig.Start("turned");
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        var ivan = Named(list, Ivan, 2);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(), Faking("Porch")));
        await rig.Saved("plain", rig.Definition("Plain people", "Plain", Arguments(more: ("LearnNewLooks", false)), Faking("Plain")));

        await Until(() => Shown(rig, "plain").Length == 3, "the plain detector shows three groups", 10);
        await Until(() => LearnedOf(Reread(list, Ana)).Length > 0, "Ana gains a learned sample, which only the porch learns", 10);
        await Until(() => Shown(rig).Length == 2, "the porch shows two groups", 10);
        var placed = fake.Placed();
        Check(placed.Length > 0 && placed.All(p => p.Type == "Porch" && PastTheFrontalRule(p.Face)), "the porch's turned faces are past the frontal rule, and the plain detector's faces are as found");
        var from = (Porch: fake.Embedded("Porch").Length, Plain: fake.Embedded("Plain").Length);
        await Until(() => fake.Embedded("Porch").Length >= from.Porch + 5 * 4 && fake.Embedded("Plain").Length >= from.Plain + 5 * 4, "five checks more of each", 10);

        var porch = fake.Embedded("Porch").Chunk(4).ToArray();
        Check(porch.All(check => check.SequenceEqual([ana, stranger, maybe, fifth])),
            $"the porch embeds the three turned inside the limit and the fifth face, never the one beyond it ({ana},{stranger},{maybe},{fifth}): " + string.Join(" | ", porch.Select(c => string.Join(",", c))));
        var plain = fake.Embedded("Plain").Chunk(4).ToArray();
        Check(plain.All(check => check.SequenceEqual([ana, stranger, maybe, beyond])), "the plain detector embeds the four largest: " + string.Join(" | ", plain.Select(c => string.Join(",", c))));

        var learned = LearnedOf(Reread(list, Ana));
        Check(learned.Length == 1 && PeopleTests.Cosine((float[])((dynamic)learned[0]).Embedding.Values, FaceOf(1)) > 0.999, "the turned face of Ana is her learned sample");
        Check(rig.Labels("porch").Count(label => label == First(Ana)) == 1 && rig.Labels("plain").Count(label => label == First(Ana)) == 1, "both cameras name Ana: " + string.Join(", ", rig.Labels("porch")));
        Check(Sensor(rig) == $"{Ana} | [{Ana}] | [{First(Ana)}] | 2 | {Ana}", "on the porch, Ana is in frame with two unknown faces, the turned stranger and the fifth face: " + Sensor(rig));
        Check(Sensor(rig, "plain") == $"{Ana} | [{Ana}] | [{First(Ana)}] | 3 | {Ana}", "the plain detector counts three, the maybe among them: " + Sensor(rig, "plain"));

        var unknown = (Porch: Member(rig.Detector(), "Unknown"), Plain: Member(rig.Detector("plain"), "Unknown"));
        float[] Of(int spot) => PeopleTests.Unit(1000 + spot);
        var porchGroups = Shown(rig);
        Check(porchGroups.Length == 2 && porchGroups.Any(g => HoldsFace(unknown.Porch, g, Of(stranger))) && porchGroups.Any(g => HoldsFace(unknown.Porch, g, Of(fifth)))
            && porchGroups.All(g => g.SuggestedPersonId == null), "the porch's groups are the turned stranger's and the fifth face's, suggesting no one");
        Check(AllGroups(unknown.Porch).Length == 2, "and it keeps no group of the maybe, shown or not: " + AllGroups(unknown.Porch).Length);
        var plainGroups = Shown(rig, "plain");
        Check(plainGroups.Count(g => g.SuggestedPersonId == (string)ivan.Id) == 1 && plainGroups.Count(g => g.SuggestedPersonId == null) == 2, "the plain detector's maybe is a group suggesting Ivan");
        Check(Reread(list, Ivan).LastSeen == null, "Ivan is never seen");
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: a face turned past the frontal rule but inside the limit is recognised, learned and grouped as a frontal one is, one beyond the limit is never embedded, and a turned maybe is neither grouped nor counted, where a frontal maybe is");
    }

    // The look-alikes: A named Ivan, so that B's card is known not to be him. B's face, turned, scores as a maybe of Ivan
    // but is more alike to B's card: it is B's, counted and grouped with the card. A turned maybe of Ivan that no card is
    // alike to is neither.
    static async Task ATurnedMaybeAGroupIsAlikeToIsThatGroups()
    {
        var spots = await Spots();
        using var fake = new Fake(spots);
        var (first, last, a, b) = LookAlikes(spots, fake);
        Show();
        await using var rig = await Rig.Start("turned-maybe");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => Shown(rig).Length == 2, "the look-alikes are two cards: " + Shown(rig).Length, 10);
        var unknown = Member(rig.Detector(), "Unknown");
        string aId = Shown(rig).Single(group => HoldsFace(unknown, group, a)).Id;
        string bId = Shown(rig).Single(group => HoldsFace(unknown, group, b)).Id;
        Check(await Press(rig, "unknown", aId, "save", new { person = NewPerson(Ivan) }) == "done", "Save of A's card as Ivan");
        // A, still in view, is recognised as Ivan, which begins his visit: he is the last recognised from then on, so what
        // follows does not depend on whether a check saw A between the Save and A going out of view.
        await Until(() => Sensor(rig).StartsWith($"{Ivan} | [{Ivan}] |", StringComparison.Ordinal), () => "A is recognised as Ivan: " + Sensor(rig), 10);
        int Sightings() => (int)unknown.Find(bId).Sightings;
        async Task Checks(int count)
        {
            var from = fake.Embedded("Porch").Length;
            await Until(() => fake.Embedded("Porch").Length >= from + count * 4, $"{count} checks", 10);
        }

        // A out of view; B turned, a maybe of Ivan, and more alike to B's card.
        bool Maybe(float[] face) => PeopleTests.Cosine(face, a) is >= 0.42 and < 0.48;
        var turned = Enumerable.Range(60, 200).Select(seed => PeopleTests.At(b, 0.8, seed)).First(face => Maybe(face));
        fake.Embeds(first, a, fit: 9);
        fake.Embeds(last, turned);
        fake.Turns(last, (float)TurnLimit - 0.03f);
        await Checks(2);
        var seen = Sightings();
        await Until(() => Sensor(rig) == $"Unknown | [] | [] | 1 | {Ivan}", () => "the turned face of B is an unknown face: " + rig.Shown("recognised"), 10);
        await Checks(3);
        Check(Sightings() > seen && Shown(rig).Length == 1, $"and a sighting of B's card, the only one: {seen} to {Sightings()}, {Shown(rig).Length} cards");
        Check(fake.Placed().Length > 0 && fake.Placed().All(p => PastTheFrontalRule(p.Face)), "B's face is past the frontal rule");

        // A turned maybe of Ivan no card is alike to.
        var other = Enumerable.Range(300, 200).Select(seed => PeopleTests.At(a, 0.45, seed))
            .First(face => Maybe(face) && PeopleTests.Cosine(face, b) < 0.4 && PeopleTests.Cosine(face, turned) < 0.4);
        fake.Embeds(last, other);
        await Until(() => Sensor(rig) == $"Nobody | [] | [] | 0 | {Ivan}", () => "a turned maybe of Ivan alone is no unknown face: " + rig.Shown("recognised"), 10);
        seen = Sightings();
        var groups = AllGroups(unknown).Length;
        await Checks(5);
        Check(Sightings() == seen && AllGroups(unknown).Length == groups && Sensor(rig) == $"Nobody | [] | [] | 0 | {Ivan}",
            $"it is neither a sighting of B's card nor a group of its own: {seen} to {Sightings()}, {groups} to {AllGroups(unknown).Length} groups, " + Sensor(rig));
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: a turned face that is a maybe of a person but more alike to a card known not to be them is that card's and counts, and a turned maybe no card is alike to is neither");
    }

    // A turned face may fit worse than a frontal one and be turned further than 0.55: of the five largest usable faces, the
    // largest is turned 0.75, beyond the limit, and never embedded; the next four are. Ana's face turned 0.60 that fits at
    // 9 is recognised and learned; a stranger's turned 0.65 is an unknown face; but Ivan's face turned 0.60 that fits at
    // 10.5, and his frontal face that fits at 9, are compared with no one, grouped nor counted.
    static async Task ATurnedFaceHasWiderLimitsThanAFrontalOne()
    {
        var spots = await Spots();
        int[] usable = [.. Enumerable.Range(0, spots.Length).Where(i => spots[i].Usable)];
        Check(usable.Length >= 5 && spots[usable[1]].Eyes >= (double)PeopleTests.DefaultRules.LearnEyes && usable[..4].All(i => spots[i].Eyes >= spots[i].Width / 5) && spots[usable[4]].Frontal,
            "the group photo has five usable faces, the second largest large enough to learn, the four largest with their eyes a fifth of their box apart, the fifth frontal: " + string.Join(" | ", spots.Select(s => s.ToString())));
        var (beyond, ana, stranger, worse, frontal) = (usable[0], usable[1], usable[2], usable[3], usable[4]);
        var limits = (Turn: (float)TurnLimit, Fit: (float)(double)PeopleTests.DefaultRules.TurnedFitLimit, Frontal: (float)(double)PeopleTests.DefaultRules.FitLimit);
        Check(limits == (0.70f, 10f, 7f), $"the limits are 0.70 and 10 for a turned face, 7 for a frontal one: {limits}");
        using var fake = new Fake(spots);
        fake.Turns(beyond, 0.75f);
        fake.Turns(ana, 0.60f);
        fake.Turns(stranger, -0.65f);
        fake.Turns(worse, 0.60f);
        fake.Embeds(ana, FaceOf(1), fit: 9);
        fake.Embeds(worse, FaceOf(2), fit: 10.5f);
        fake.Embeds(frontal, FaceOf(2), fit: 9);
        Show();
        await using var rig = await Rig.Start("turned-limits");
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        Named(list, Ivan, 2);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(), Faking("Porch")));

        await Until(() => LearnedOf(Reread(list, Ana)).Length > 0, "Ana gains a learned sample: " + rig.Lines(), 10);
        await Until(() => Shown(rig).Length == 1, "the stranger is a card: " + Shown(rig).Length, 10);
        var from = fake.Embedded().Length;
        await Until(() => fake.Embedded().Length >= from + 4 * 4, "four checks more", 10);
        var checks = fake.Embedded().Chunk(4).ToArray();
        Check(checks.All(check => check.SequenceEqual([ana, stranger, worse, frontal])),
            $"each check embeds the four faces after the one turned beyond the limit ({ana},{stranger},{worse},{frontal}): " + string.Join(" | ", checks.Select(c => string.Join(",", c))));
        Check(fake.Placed().Where(p => p.Spot != beyond).All(p => PastTheFrontalRule(p.Face)), "the turned faces are past the frontal rule");

        var learned = LearnedOf(Reread(list, Ana));
        Check(learned.Length == 1 && PeopleTests.Cosine((float[])((dynamic)learned[0]).Embedding.Values, FaceOf(1)) > 0.999, "Ana's turned face is her learned sample");
        Check(Sensor(rig) == $"{Ana} | [{Ana}] | [{First(Ana)}] | 1 | {Ana}", "Ana is in frame with one unknown face, the stranger's: " + Sensor(rig));
        var unknown = Member(rig.Detector(), "Unknown");
        Check(AllGroups(unknown).Length == 1 && HoldsFace(unknown, Shown(rig)[0], PeopleTests.Unit(1000 + stranger)), "the one group is the stranger's: " + AllGroups(unknown).Length);
        Check(Reread(list, Ivan).LastSeen == null, "Ivan's faces, the turned one fitting at 10.5 and the frontal one at 9, are never recognised");
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: a face the frontal rule turns away is compared turned up to 0.70 and fitting within 10, and learned by the same rules; one turned 0.75, one fitting at 10.5 and a frontal face fitting at 9 are not");
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
        await Until(() => rig.Labels("porch").Contains(First(Ana)), "the porch's camera names Ana by her first name: " + string.Join(", ", rig.Labels("porch")), 10);
        await Until(() => fake.Embedded("Plain").Length >= 3 * 4, "the plain detector recognises too", 10);
        var named = rig.Labels("porch");
        Check(named.Count(l => l == First(Ana)) == 1 && named.Where(l => l != First(Ana)).All(IsScore), "Ana's first name is above her box, the others' scores, and no suggestion: " + string.Join(", ", named));
        var plain = rig.Labels("plain");
        Check(plain.Length == 15 && plain.All(IsScore), "with Names on the camera off, only scores: " + string.Join(", ", plain));
        // Another Ana in the list, added on another detector: the camera tells them apart by the last name's first letter.
        Named(list, "Ana Kovac", 3);
        await Until(() => rig.Labels("porch").Contains("Ana H."), "with another Ana in the list, the porch's camera names her Ana H.: " + string.Join(", ", rig.Labels("porch")), 10);
        Check(rig.Labels("porch").Count(l => !IsScore(l)) == 1, "and names no one else: " + string.Join(", ", rig.Labels("porch")));
        Console.WriteLine("PASS people detector: the camera draws a recognised face's first name, with the last name's first letter when another person has it, and not a maybe's, and no name with Names on the camera off");
    }

    // Boxes on the camera and Face points on the camera reach the camera picture: by default both are drawn; each off
    // leaves out its own; both off leave the frame with the names alone, or with nothing, and the picture is still shown.
    static async Task TheCameraDrawsTheBoxesAndThePointsOnlyWhenAsked()
    {
        var spots = await Spots();
        using var fake = new Fake(spots);
        Show();
        await using var rig = await Rig.Start("drawing");
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        fake.Embeds(0, PeopleTests.At(PeopleTests.Unit(1), 0.8, 11));
        // Porch has no value for either: their defaults.
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await rig.Saved("boxes", rig.Definition("Boxes people", "Boxes", Arguments(more: [("LearnNewLooks", false), ("FacePointsOnTheCamera", false)]), Faking("Boxes")));
        await rig.Saved("points", rig.Definition("Points people", "Points", Arguments(more: [("LearnNewLooks", false), ("BoxesOnTheCamera", false), ("NamesOnTheCamera", false)]), Faking("Points")));
        await rig.Saved("bare", rig.Definition("Bare people", "Bare", Arguments(more: [("LearnNewLooks", false), ("BoxesOnTheCamera", false), ("FacePointsOnTheCamera", false)]), Faking("Bare")));
        await rig.Saved("blank", rig.Definition("Blank people", "Blank", Arguments(more: [("RecogniseFaces", false), ("BoxesOnTheCamera", false), ("FacePointsOnTheCamera", false)])));
        await rig.Saved("off", rig.Definition("Off people", "Off", Arguments(more: [("LearnNewLooks", false), ("BoxesOnTheCamera", false), ("FacePointsOnTheCamera", false), ("NamesOnTheCamera", false)]), Faking("Off")));

        // What each camera draws: its faces' boxes, its people's, its dots, its scores and its names.
        var cyan = SharpColor.FromRgb(0, 200, 255);
        string Summary(string definition)
        {
            var drawn = rig.Drawn(definition);
            int Count(Func<(string Kind, string? Text, SharpColor Color), bool> which) => drawn.Count(which);
            var names = drawn.Where(d => d.Kind == "Label" && !IsScore(d.Text!)).Select(d => d.Text);
            return $"{Count(d => d.Kind == "Rectangle" && d.Color == SharpColor.Green)} faces, {Count(d => d.Kind == "Rectangle" && d.Color == cyan)} people, "
                + $"{Count(d => d.Kind == "Circle")} dots, {Count(d => d.Kind == "Label" && IsScore(d.Text!))} scores, names {string.Join("+", names)}; {drawn.Length} in all";
        }
        var expected = new Dictionary<string, string>
        {
            ["porch"] = $"8 faces, 7 people, 40 dots, 14 scores, names {First(Ana)}; 70 in all",
            ["boxes"] = $"8 faces, 7 people, 0 dots, 14 scores, names {First(Ana)}; 30 in all",
            ["points"] = "0 faces, 0 people, 40 dots, 0 scores, names ; 40 in all",
            ["bare"] = $"0 faces, 0 people, 0 dots, 0 scores, names {First(Ana)}; 1 in all",
        };
        string All() => string.Join(" | ", expected.Keys.Select(definition => $"{definition}: {Summary(definition)}"));
        await Until(() => expected.All(pair => Summary(pair.Key) == pair.Value), "each camera draws what its options say: " + All(), 20);
        var summaries = All();

        // Both off and no name: the frame itself is shown, with nothing drawn on it.
        await Until(() => rig.Latest("faces", "blank")?.Number == 8, "the blank detector checks the group photo: " + rig.Shown("faces", definition: "blank"), 10);
        Check(rig.Frame("blank") is { Luma: not null, Drawn.Length: 0 }, "the blank camera shows the video's own frame with nothing drawn on it: " + Summary("blank"));
        // All three off: not even the name of the face it recognises.
        await Until(() => fake.Embedded("Off").Length >= 2 * 4 && rig.Latest("faces", "off")?.Number == 8, "the detector with all three off recognises the group photo's faces", 10);
        Check(rig.Frame("off") is { Luma: not null, Drawn.Length: 0 }, "with all three off, the camera shows the video's own frame with nothing drawn on it: " + Summary("off"));
        var frame = SharpImage.FromPixels(640, 426, SharpPixelFormat.Yuv420, Group).ToPixels();
        int[] edges = [.. spots.SelectMany(spot =>
        {
            int x = (int)MathF.Round(spot.X), row = Math.Clamp((int)MathF.Round(spot.Y) + 1, 0, 425), w = (int)MathF.Round(spot.Width);
            return Enumerable.Range(Math.Max(0, x + 2), Math.Max(0, Math.Min(640, x + w - 2) - Math.Max(0, x + 2))).Select(column => (row * 640 + column) * 3);
        })];
        async Task<byte[]> CameraPixels(string definition)
        {
            var picture = SharpImage.Load(await rig.Api.CameraImage($"{definition}:door:camera"));
            Check(picture.Width == 640 && picture.Height == 426, $"{definition}'s camera shows the frame, 640 wide: {picture.Width} × {picture.Height}");
            return picture.ToPixels();
        }
        var blank = await CameraPixels("blank");
        var off = Enumerable.Range(0, frame.Length).Average(at => Math.Abs(blank[at] - frame[at]));
        int green = edges.Count(at => VisionTests.Greenish(blank, at)), framed = edges.Count(at => VisionTests.Greenish(frame, at));
        Check(off < 8 && green <= framed + edges.Length / 20, $"the blank camera shows the frame: {off:0.0} off on average, {green} of {edges.Length} top edge pixels green, {framed} in the frame");
        var boxed = await CameraPixels("boxes");
        var boxedGreen = edges.Count(at => VisionTests.Greenish(boxed, at));
        Check(boxedGreen * 2 >= edges.Length, $"the boxes camera has its faces' top edges green: {boxedGreen} of {edges.Length}");
        Console.WriteLine($"PASS people detector: the camera draws boxes and face points by default, each only with its option on, a name in every way, and with both off and no name, or all three off, shows the frame as it is ({summaries})");
    }

    // ---- The camera's frames ----

    /// <summary>
    /// The faces the detectors of <see cref="Faking"/> find, through the hook <see cref="Fake"/> places them with, for a test
    /// without one: as found, but a check waits with its first face while they are held, and fails while they fail.
    /// </summary>
    sealed class Finding : IDisposable
    {
        const string Hook = "HassSharp.PeopleDetectorTests.Faces";
        readonly ManualResetEventSlim open = new(true);
        volatile bool failing, held;

        public Finding() => AppContext.SetData(Hook, (Func<string, float[], float[]>)Find);

        /// <summary>The next check waits until <see cref="Release"/>, or for 30 seconds at most.</summary>
        public void Hold() => open.Reset();

        /// <summary>Whether a check waits now.</summary>
        public bool Held => held;

        /// <summary>Lets the check that waits go on.</summary>
        public void Release() => open.Set();

        /// <summary>Whether the checks of a frame with a face fail.</summary>
        public bool Fails { set => failing = value; }

        float[] Find(string type, float[] face)
        {
            if (!open.IsSet)
            {
                held = true;
                try { open.Wait(TimeSpan.FromSeconds(30)); }
                finally { held = false; }
            }
            return failing ? throw new InvalidOperationException("The faces are not found") : face;
        }

        public void Dispose()
        {
            Release();
            AppContext.SetData(Hook, null);
        }
    }

    // What is drawn of the group photo without names: 8 faces' boxes, scores and 40 dots, and 7 people's boxes and scores.
    const int DrawnOfTheGroup = 70;

    /// <summary>Waits until the frames the camera of <paramref name="rig"/> makes are the fake's grey ones, each of its own luma, after <see cref="Hide"/>.</summary>
    static Task Grey(Rig rig) => Until(() => rig.Frame()?.Luma is { } one && rig.Frame()?.Luma is { } other && one != other, () => "the camera's frames are the source's grey ones, each newer than the last: " + rig.Frame());

    /// <summary>Makes <paramref name="count"/> frames of the camera of <paramref name="rig"/>, the source showing grey ones: each is newer than the last, with <paramref name="drawn"/> drawings.</summary>
    static void GreyFrames(Rig rig, int count, int drawn, string what)
    {
        int? last = null;
        for (var i = 0; i < count; i++)
        {
            var frame = rig.Frame();
            Check(frame is { Luma: not null } && frame.Luma != last, $"{what}: frame {i} is a newer one of the source, of another luma than {last}: {frame}");
            Check(frame!.Drawn.Length == drawn, $"{what}: frame {i} has {drawn} drawings, not {frame.Drawn.Length}");
            last = frame.Luma;
        }
    }

    // With a check a minute, there is one, at the start: each frame Home Assistant asks for after it is a newer one of the
    // source, with that check's drawings on it, and one ffmpeg gives them all.
    static async Task TheCameraPlaysTheSourceBetweenChecks()
    {
        var spot = (await Spots())[0];
        using var clock = new Clock();
        Show();
        await using var rig = await Rig.Start("plays");
        // Without recognition, the clock is read by the checks alone, once each.
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: [("RecogniseFaces", false), ("CheckEvery", 60)]), Faking("Porch")));
        await Until(() => rig.Latest("faces")?.Number == 8 && rig.Latest("people")?.Number == 7, () => "the check finds the group photo's faces and people: " + rig.Shown("faces") + " | " + rig.Shown("people"), 10);
        Hide();
        await Grey(rig);
        GreyFrames(rig, 20, DrawnOfTheGroup, "after the check");

        // As Home Assistant gets them.
        var pictures = new List<byte[]>();
        var asking = Stopwatch.StartNew();
        while (pictures.Count < 10)
        {
            Check(asking.Elapsed < TimeSpan.FromSeconds(10), $"Home Assistant gets 10 pictures, each unlike the others, within 10 s: {pictures.Count}");
            var picture = await rig.Api.CameraImage("porch:door:camera");
            if (!pictures.Any(p => p.SequenceEqual(picture))) pictures.Add(picture);
        }
        int row = Math.Clamp((int)MathF.Round(spot.Y) + 1, 0, 425), left = Math.Max(0, (int)MathF.Round(spot.X) + 2), right = Math.Min(640, (int)MathF.Round(spot.X + spot.Width) - 2);
        foreach (var picture in pictures)
        {
            var rgb = SharpImage.Load(picture).ToPixels();
            var green = Enumerable.Range(left, right - left).Count(column => VisionTests.Greenish(rgb, (row * 640 + column) * 3));
            Check(rgb.Length == 640 * 426 * 3 && green * 2 >= right - left, $"each has the first face's top edge green: {green} of {right - left} pixels");
        }
        Check(clock.Reads("Porch") == 1, $"one check was run meanwhile: {clock.Reads("Porch")}");
        Check(CameraFeeds.All is [{ EntityId: "camera.porch", Started: 1 }], "one feed, whose ffmpeg was started once, gives the checks and the camera their frames: " + string.Join(", ", CameraFeeds.All.Select(f => $"{f.EntityId} {f.Started}")));
        Console.WriteLine("PASS people detector: with a check a minute, the camera shows the source's newest frames with the check's drawings, all of one ffmpeg");
    }

    // A check that takes long holds nothing up: the frames go on with the last check's drawings until it has ended, and
    // then have its own.
    static async Task TheDrawingsStayUntilTheNextCheck()
    {
        using var finding = new Finding();
        Show();
        await using var rig = await Rig.Start("drawings-stay");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("RecogniseFaces", false)), Faking("Porch")));
        await Until(() => rig.Drawn().Length == DrawnOfTheGroup, () => "the group photo's faces and people are drawn: " + rig.Drawn().Length, 10);
        finding.Hold();
        await Until(() => finding.Held, "a check is held as it finds its faces", 10);
        Hide();
        await Grey(rig);
        GreyFrames(rig, 10, DrawnOfTheGroup, "while a check is held");
        Check(finding.Held, "the check is held still");
        finding.Release();
        await Until(() => rig.Drawn().Length == 0, () => "once a check has found no one, nothing is drawn: " + rig.Drawn().Length, 10);
        GreyFrames(rig, 5, 0, "after a check that found no one");
        Console.WriteLine("PASS people detector: while a check runs, the camera goes on with the drawings of the one before it, until it has ended");
    }

    static async Task NothingIsDrawnBeforeTheFirstCheck()
    {
        using var finding = new Finding();
        finding.Hold();
        Show();
        await using var rig = await Rig.Start("before-a-check");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("RecogniseFaces", false)), Faking("Porch")));
        await Until(() => finding.Held, "the first check is held as it finds its faces", 10);
        Hide();
        await Grey(rig);
        GreyFrames(rig, 5, 0, "before the first check has ended");
        var picture = SharpImage.Load(await rig.Api.CameraImage("porch:door:camera"));
        Check((picture.Width, picture.Height) == (640, 426), $"Home Assistant gets the frame: {picture.Width} × {picture.Height}");
        Check(finding.Held && rig.Of("faces").All(p => p.Value == null), "the first check is held still: " + rig.Shown("faces"));
        Show();
        finding.Release();
        await Until(() => rig.Drawn().Length == DrawnOfTheGroup, () => "once it has ended, what it found is drawn: " + rig.Drawn().Length, 10);
        Console.WriteLine("PASS people detector: before the first check has ended, the camera shows the source as it is");
    }

    static async Task AFailedCheckClearsTheDrawings()
    {
        using var finding = new Finding();
        Show();
        await using var rig = await Rig.Start("failed-clears");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("RecogniseFaces", false)), Faking("Porch")));
        await Until(() => rig.Drawn().Length == DrawnOfTheGroup, () => "the group photo's faces and people are drawn: " + rig.Drawn().Length, 10);
        finding.Fails = true;
        await Until(() => rig.Home.Logs(PyLogLevel.Error).Length > 0, () => "a check fails: " + rig.Lines(), 10);
        // Every check fails from now on, as the photo is still shown.
        for (var i = 0; i < 10; i++)
        {
            var frame = rig.Frame();
            Check(frame is { Luma: not null, Drawn.Length: 0 }, "after a failed check the camera shows the source's frame with nothing drawn on it: " + frame);
        }
        finding.Fails = false;
        await Until(() => rig.Drawn().Length == DrawnOfTheGroup, () => "after the next that works, what it found is drawn: " + rig.Drawn().Length, 10);
        Check(rig.Home.Logs(PyLogLevel.Error) is [var error] && error.EndsWith("The faces are not found", StringComparison.Ordinal), "the checks' failure is the one error: " + rig.Lines());
        Console.WriteLine("PASS people detector: a check that fails clears the drawings, the frames go on, and the next check that works draws again");
    }

    // A check that fails after what it found was given to the camera, as it sets the entities, clears that too.
    static async Task ACheckThatFailsLateClearsTheDrawings()
    {
        const string counted = "            faces.Checked(foundFaces.Count, now, hold);\n";
        var source = Committed();
        Check(source.Split(counted).Length == 2, "PeopleDetector.cs counts the faces of a check once");
        using var clock = new Clock();
        Show();
        await using var rig = await Rig.Start("late-failure", people: source.Replace(counted, "            if (DetectPeople) throw new InvalidOperationException(\"The faces are not counted\");\n" + counted));
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: [("RecogniseFaces", false), ("CheckEvery", 60)]), Faking("Porch")));
        await Until(() => rig.Home.Logs(PyLogLevel.Error).Any(l => l.EndsWith("The faces are not counted", StringComparison.Ordinal)), () => "the check fails: " + rig.Lines(), 10);
        Hide();
        await Grey(rig);
        GreyFrames(rig, 5, 0, "after a check that failed late");
        Console.WriteLine("PASS people detector: a check that fails as it sets the entities clears its drawings");
    }

    // The places of what a check found are in the pixels of the frame it checked, so a frame of another size has nothing
    // drawn on it.
    static async Task AFrameOfAnotherSizeIsNotDrawnOn()
    {
        // After its 100th frame, five seconds in, the fake gives frames of half the size.
        Source("camera.porch", Porch("resize=100:320:214,"));
        Show();
        await using var rig = await Rig.Start("resized");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: [("RecogniseFaces", false), ("CheckEvery", 60)])));
        await Until(() => rig.Drawn().Length == DrawnOfTheGroup, () => "what the check found is drawn: " + rig.Drawn().Length, 10);
        Frame? frame = null;
        await Until(() =>
        {
            frame = rig.Frame();
            Check(frame?.Width == 320 || frame is { Width: 640, Drawn.Length: DrawnOfTheGroup }, "a frame of the size checked has the drawings: " + frame);
            return frame?.Width == 320;
        }, () => "the source gives frames of another size: " + frame, 15);
        for (var i = 0; i < 5; i++, frame = rig.Frame()) Check(frame is { Width: 320, Height: 214, Luma: not null, Drawn.Length: 0 }, "a frame of another size than the one checked has nothing drawn on it: " + frame);
        Console.WriteLine("PASS people detector: a frame of another size than the one checked is shown with nothing drawn on it");
    }

    static async Task NoFrameIsMadeWhenNobodyAsks()
    {
        using var clock = new Clock();
        Show();
        await using var rig = await Rig.Start("nobody-asks");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("RecogniseFaces", false)), Faking("Porch")));
        await Until(() => rig.Latest("faces")?.Number == 8, () => "the checks run: " + rig.Shown("faces"), 10);
        await Checks(clock, 5);
        var frames = rig.Camera().Frames;
        Check(frames.Begun == 0 && frames.Handed().Current == null, $"no frame was made or shown: {frames.Begun} made");

        // Someone looks, and looks away again.
        byte[] jpeg = [];
        for (var i = 0; i < 3; i++) jpeg = await rig.Api.CameraImage("porch:door:camera");
        var begun = frames.Begun;
        await Checks(clock, 5);
        Check(begun > 0 && frames.Begun == begun && ReferenceEquals(frames.Handed().Current?.Jpeg, jpeg), $"after the last request no frame was made or shown: {begun} made, then {frames.Begun}");
        Console.WriteLine("PASS people detector: a frame of the camera is made only when Home Assistant asks for one; the checks run without");
    }

    static async Task StoppingWhileAFrameIsAwaitedEndsCleanly()
    {
        // The fake gives one frame and hangs, and is left to.
        CameraFeeds.StallLimit = TimeSpan.FromSeconds(60);
        Source("camera.porch", Porch("stall=1,"));
        Show();
        await using var rig = await Rig.Start("stop-awaited");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("RecogniseFaces", false))));
        await Until(() => rig.Latest("faces")?.Number == 8, () => "the checks run: " + rig.Shown("faces"), 10);
        var camera = rig.Camera();
        // The frame the check took is the camera's too, which reads for itself; then there is none.
        Check((await rig.Api.CameraImage("porch:door:camera").WaitAsync(TimeSpan.FromSeconds(5))).Length > 0, "the camera gives the frame that was checked");
        var pending = rig.Api.CameraImage("porch:door:camera");
        await Until(() =>
        {
            // A request that comes right after shares the frame before it: the next is asked for.
            if (camera.Frames.Begun < 2 && pending.IsCompleted) pending = rig.Api.CameraImage("porch:door:camera");
            return camera.Frames.Begun >= 2;
        }, "a request makes a second frame", 10);
        Check(camera.Frames.Busy && !pending.IsCompleted, "a request waits for the next frame of a source that gives none");
        // The frame shown was given back by all but the feed, whose newest it still is, and this probe of it.
        using (var newest = CameraFeeds.Newest("camera.porch", (1280, 0)) ?? throw new Exception("camera.porch has a newest frame"))
        {
            var held = (PixelFrame)typeof(SharpImage).GetField("frame", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(newest)!;
            await Until(() => held.References == 2, () => $"the frame shown was given back: {held.References - 2} references left");
        }

        var stopping = Stopwatch.StartNew();
        await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = false });
        await Until(() => rig.Home.Logs(PyLogLevel.Debug).Contains("Porch people: People detector stopped"), () => "the device says it stopped: " + rig.Lines());
        Check(stopping.Elapsed < TimeSpan.FromSeconds(2), $"it stops in under 2 s: {stopping.Elapsed.TotalSeconds:0.000} s");
        // Overtaken by the stop, the request is answered with the last good frame.
        Check((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Length > 0, "the request that waited is answered");
        await Until(() => CameraFeeds.All.All(f => f.EntityId != "camera.porch"), "the live feed of camera.porch closes");
        Check(await camera.MakeFrame() == null, "a frame asked for after the stop is none");
        var said = rig.Home.Logs(PyLogLevel.Error).Concat(rig.Home.Logs(PyLogLevel.Warn)).ToArray();
        Check(said.Length == 0, "nothing is logged as a warning or an error: " + string.Join(" | ", said));
        Console.WriteLine("PASS people detector: a stop while the camera waits for a frame ends at once, logs nothing, and leaves no frame held");
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

    // Two faces of one check recognised as one person: the name goes to the better match alone, which alone is learned
    // from; the other is drawn with its score, and goes to the unknown faces, on a card that does not suggest her.
    static async Task APersonIsGivenToOneFaceOfACheck()
    {
        var spots = await Spots();
        var learnEyes = (double)PeopleTests.DefaultRules.LearnEyes;
        Check(spots[0].Usable && spots[1].Usable && spots[0].Eyes >= learnEyes && spots[1].Eyes >= learnEyes, "the two largest faces are usable and large enough to learn");
        using var fake = new Fake(spots);
        Show();
        await using var rig = await Rig.Start("one-name");
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        // Both are Ana beyond doubt and would be learned; the smaller is the better match.
        var larger = PeopleTests.At(PeopleTests.Unit(1), 0.7, 31);
        var better = PeopleTests.At(PeopleTests.Unit(1), 0.85, 32);
        fake.Embeds(0, larger);
        fake.Embeds(1, better);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(), Faking("Porch")));
        await Until(() => LearnedOf(Reread(list, Ana)).Length > 0, "Ana gains a learned sample: " + rig.Lines(), 10);
        var learned = LearnedOf(Reread(list, Ana));
        Check(learned.Length == 1 && PeopleTests.Cosine((float[])((dynamic)learned[0]).Embedding.Values, better) > 0.999, "the better match is learned, not the larger face");
        var from = fake.Embedded("Porch").Length;
        await Until(() => fake.Embedded("Porch").Length >= from + 6 * 4, "six checks more", 10);
        var labels = rig.Labels();
        Check(labels.Count(label => label == First(Ana)) == 1, "one face is named Ana, the others show their scores: " + string.Join(", ", labels));
        Check(LearnedOf(Reread(list, Ana)).Length == 1, "the other face is never learned from");
        var unknown = Member(rig.Detector(), "Unknown");
        bool Holds(dynamic group, float[] face) => VisionTests.Items((object)unknown.Samples((string)group.Id)).Any(sample => PeopleTests.Cosine((float[])((dynamic)sample).Embedding.Values, face) > 0.999);
        var groups = Shown(rig);
        Check(groups.Length == 3 && groups.Count(group => Holds(group, larger)) == 1, "the face not given the name is a group, as the two strangers are: " + groups.Length);
        Check(groups.All(group => group.SuggestedPersonId == null), "and no group suggests Ana");
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: a person is given to one face of a check, the best match, which alone is learned from; the other is drawn with its score and goes to the unknown faces without suggesting them");
    }

    // As seen live on group.jpg: two men of one frame, 0.55 alike, became one card; on two cards, the one left after the
    // other was named was taken for him and removed. Named each on their own card, each is recognised by their own name,
    // together and alone. The two other faces embedded fit too badly to be compared, so the frame has these two people.
    static async Task TwoLookAlikesOfOneFrameAreNamedEachOnTheirOwnCard()
    {
        var spots = await Spots();
        int[] chosen = [.. Enumerable.Range(0, spots.Length).Where(i => spots[i].Usable).Take(4)];
        Check(chosen.Length == 4, "four faces are embedded in each check");
        var (first, last) = (chosen[0], chosen[3]);
        using var fake = new Fake(spots);
        // The largest and the smallest embedded, as SFace has the 32.3 and 17.9 pixel faces of group.jpg.
        var a = PeopleTests.Unit(41);
        var b = PeopleTests.At(a, 0.55, 42);
        fake.Embeds(first, a);
        fake.Embeds(last, b);
        fake.Embeds(chosen[1], PeopleTests.Unit(43), fit: 9);
        fake.Embeds(chosen[2], PeopleTests.Unit(44), fit: 9);
        Show();
        await using var rig = await Rig.Start("look-alikes");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        async Task Checks(int count, string what)
        {
            var from = fake.Embedded("Porch").Length;
            await Until(() => fake.Embedded("Porch").Length >= from + count * 4, what, 10);
        }
        await Checks(20, "twenty checks");
        await Until(() => CardsOf(Page(rig), "unknown").Length == 2, "two faces, two cards: " + Shown(rig).Length, 2);
        var unknown = Member(rig.Detector(), "Unknown");
        bool Holds(dynamic group, float[] face) => VisionTests.Items((object)unknown.Samples((string)group.Id)).Any(sample => PeopleTests.Cosine((float[])((dynamic)sample).Embedding.Values, face) > 0.999);
        string aId = Shown(rig).Single(group => Holds(group, a)).Id;
        string bId = Shown(rig).Single(group => Holds(group, b)).Id;
        Check(aId != bId, "the two faces are on two cards");
        int Sightings() => (int)unknown.Find(bId).Sightings;
        string[] Names() => [.. rig.Labels().Where(label => !IsScore(label))];

        // A saved as Ivan while B is alone in view, so that nothing but B could have Ivan seen.
        fake.Embeds(first, a, fit: 9);
        await Checks(2, "two checks with B alone");
        Check(await Press(rig, "unknown", aId, "save", new { person = NewPerson(Ivan) }) == "done", "Save of A's card as Ivan");
        var list = PeopleOf(rig);
        // The naming is a sighting of Ivan, at A's card's last; a check's is later.
        DateTimeOffset? named = Reread(list, Ivan).LastSeen;
        Check(named != null, "the naming is a sighting of Ivan");
        var seen = Sightings();
        await Checks(5, "five checks with B alone");
        var card = CardOf(Page(rig), "unknown", bId);
        Check(CardsOf(Page(rig), "unknown").Length == 1 && card != null && Detail(card) == null, "B's card stays, and does not suggest Ivan: " + Page(rig)?.ToJsonString());
        Check(Names().Length == 0 && Reread(list, Ivan).LastSeen == named, "B alone is not named Ivan, and Ivan is not seen: " + string.Join(", ", Names()));
        Check(Sightings() > seen, "B's card counts his checks");

        // Both in view: A is Ivan, and B's card keeps counting.
        fake.Embeds(first, a);
        seen = Sightings();
        await Checks(5, "five checks with both");
        Check(Names().SequenceEqual([First(Ivan)]), "A is named Ivan, and B is not: " + string.Join(", ", Names()));
        Check(Reread(list, Ivan).LastSeen > named && Sightings() > seen && CardsOf(Page(rig), "unknown").Length == 1, "Ivan is seen, and B's card counts");

        // B alone again, after Ivan was seen with him.
        fake.Embeds(first, a, fit: 9);
        seen = Sightings();
        await Checks(5, "five checks with B alone");
        Check(Names().Length == 0 && Sightings() > seen, "B alone is still not Ivan, and his card counts: " + string.Join(", ", Names()));

        // B saved as Ivo: each is recognised by his own name, together and alone, and no card is left.
        Check(await Press(rig, "unknown", bId, "save", new { person = NewPerson(Ivo) }) == "done", "Save of B's card as Ivo");
        fake.Embeds(first, a);
        await Checks(5, "five checks with both");
        Check(Names().Order().SequenceEqual(new[] { First(Ivan), First(Ivo) }.Order()), "together, each by his name: " + string.Join(", ", Names()));
        fake.Embeds(first, a, fit: 9);
        await Checks(3, "three checks with Ivo alone");
        Check(Names().SequenceEqual([First(Ivo)]), "Ivo alone: " + string.Join(", ", Names()));
        fake.Embeds(first, a);
        fake.Embeds(last, b, fit: 9);
        await Checks(3, "three checks with Ivan alone");
        Check(Names().SequenceEqual([First(Ivan)]), "Ivan alone: " + string.Join(", ", Names()));
        Check(CardsOf(Page(rig), "unknown").Length == 0, "no card is left: " + Shown(rig).Length);
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: two look-alikes of one frame are two cards; named one by one, the other's card stays, is not taken for him and counts, and both are then recognised by their own names, together and alone");
    }

    // Without a frame that has them both, a face as alike to a person is that person.
    static async Task AFaceAlikeToAPersonNeverSeenWithThemIsThatPerson()
    {
        using var fake = new Fake(await Spots());
        Show();
        await using var rig = await Rig.Start("alike");
        var list = PeopleOf(rig);
        Named(list, Ivan, 2);
        var face = PeopleTests.At(PeopleTests.Unit(2), 0.55, 51);
        fake.Embeds(0, face);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => rig.Labels().Contains(First(Ivan)), "the face 0.55 alike to Ivan is named Ivan: " + string.Join(", ", rig.Labels()), 10);
        await Until(() => Reread(list, Ivan).LastSeen != null, "Ivan is seen", 10);
        var from = fake.Embedded("Porch").Length;
        await Until(() => fake.Embedded("Porch").Length >= from + 5 * 4, "five checks more", 10);
        Check(rig.Labels().Count(label => label == First(Ivan)) == 1, "still named Ivan: " + string.Join(", ", rig.Labels()));
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: a face 0.55 alike to a person, never seen in a frame with them, is recognised as them");
    }

    /// <summary>Every group of a running detector's <c>Unknown</c>, shown or not, as the internal <c>All</c> gives them.</summary>
    static dynamic[] AllGroups(dynamic unknown)
    {
        object target = unknown;
        var all = target.GetType().GetProperty("All", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("FaceGroups has no All");
        return [.. ((IEnumerable<object>)all.GetValue(target)!).Select(group => (dynamic)group)];
    }

    /// <summary>Whether a group of <paramref name="unknown"/> has a sample of the face.</summary>
    static bool HoldsFace(dynamic unknown, dynamic group, float[] face) =>
        VisionTests.Items((object)unknown.Samples((string)group.Id)).Any(sample => PeopleTests.Cosine((float[])((dynamic)sample).Embedding.Values, face) > 0.999);

    /// <summary>
    /// The look-alikes of <see cref="TwoLookAlikesOfOneFrameAreNamedEachOnTheirOwnCard"/>: A at the largest face embedded,
    /// B 0.55 alike to A at the smallest, and the two faces between fitting too badly to be compared.
    /// </summary>
    static (int First, int Last, float[] A, float[] B) LookAlikes(Spot[] spots, Fake fake)
    {
        int[] chosen = [.. Enumerable.Range(0, spots.Length).Where(i => spots[i].Usable).Take(4)];
        Check(chosen.Length == 4, "four faces are embedded in each check");
        var a = PeopleTests.Unit(41);
        var b = PeopleTests.At(a, 0.55, 42);
        fake.Embeds(chosen[0], a);
        fake.Embeds(chosen[3], b);
        fake.Embeds(chosen[1], PeopleTests.Unit(43), fit: 9);
        fake.Embeds(chosen[2], PeopleTests.Unit(44), fit: 9);
        return (chosen[0], chosen[3], a, b);
    }

    // As the controller's run saw it on group.jpg: a check took the people list before embedding its faces, the page's
    // Save of A's card landed meanwhile, and the check then noted A's face against the list without her: it joined B's
    // group, which the Save had just marked as not her, and B's face started a fourth card. The check is held inside
    // Embed while the Save runs.
    static async Task ASaveDuringACheckIsNotUndoneByIt()
    {
        var spots = await Spots();
        using var fake = new Fake(spots);
        var (first, _, a, b) = LookAlikes(spots, fake);
        Show();
        await using var rig = await Rig.Start("save-in-check");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => CardsOf(Page(rig), "unknown").Length == 2, "two faces, two cards: " + Shown(rig).Length, 10);
        var unknown = Member(rig.Detector(), "Unknown");
        string oneId = Shown(rig).Single(group => HoldsFace(unknown, group, a)).Id;
        string otherId = Shown(rig).Single(group => HoldsFace(unknown, group, b)).Id;

        fake.HoldAt(first);
        await Until(() => fake.Held, "a check is held inside Embed", 10);
        var seen = (int)unknown.Find(otherId).Sightings;
        var press = Press(rig, "unknown", oneId, "save", new { person = NewPerson(Ana) });
        Check(await Task.WhenAny(press, Task.Delay(TimeSpan.FromSeconds(10))) == press && await press == "done", "the Save runs while the check is held");
        var from = fake.Embedded("Porch").Length;
        fake.Release();
        await Until(() => fake.Embedded("Porch").Length >= from + 2 * 4, "the held check and one more", 10);

        var other = unknown.Find(otherId);
        Check(other != null && !HoldsFace(unknown, other, a), "B's group has no sample of A's face");
        Check((int)other!.Sightings > seen, "and has taken B's sightings");
        var groups = ((dynamic[])AllGroups(unknown));
        Check(groups.Length == 1 && CardsOf(Page(rig), "unknown").Length == 1, "it is the only group, and the only card: " + string.Join(", ", groups));
        Check(rig.Labels().Where(label => !IsScore(label)).SequenceEqual([First(Ana)]), "A is named Ana: " + string.Join(", ", rig.Labels()));
        // In the running detector's own types, which are not those PeopleTests compiles.
        object everyone = Member(rig.Detector(), "People").People;
        var library = ((object)unknown).GetType().Assembly;
        var embedding = Activator.CreateInstance(library.GetType("NodeLib.FaceEmbedding", true)!, a, 1.5f)!;
        var rules = library.GetType("NodeLib.RecognitionRules", true)!.GetProperty("Default")!.GetValue(null)!;
        var match = library.GetType("NodeLib.FaceMatcher", true)!.GetMethod("Match")!.Invoke(null, [embedding, everyone, rules])!;
        Check(((dynamic)match).Person?.FullName == Ana && !(bool)unknown.KnownNotToBe((dynamic)embedding, (dynamic)match), "no group takes A's face from Ana");
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: a Save on the page while a check is embedding its faces is not undone by that check: the named face is recognised, and the look-alike's card keeps his face alone");
    }

    // The same with the change made by another detector on the shared store: it lands while this detector's check is held
    // inside Embed, and this detector's list has read it before the check goes on.
    static async Task ASaveOnAnotherDetectorDuringACheckIsNotUndoneByIt()
    {
        var spots = await Spots();
        using var fake = new Fake(spots);
        var (first, _, a, _) = LookAlikes(spots, fake);
        Source("camera.hall", Porch());
        Show();
        await using var rig = await Rig.Start("save-elsewhere");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await rig.Saved("hall", rig.Definition("Hall people", "Hall", Arguments("camera.hall", ("LearnNewLooks", false)), Faking("Hall")));
        await Until(() => CardsOf(Page(rig), "unknown").Length == 2 && CardsOf(Page(rig, "hall"), "unknown").Length == 2, "two cards on each page", 10);
        var unknown = Member(rig.Detector(), "Unknown");
        var hall = Member(rig.Detector("hall"), "Unknown");
        string hallOne = VisionTests.Items((object)hall.Shown).Select(group => (dynamic)group).Single(group => HoldsFace(hall, group, a)).Id;

        fake.HoldAt(first, "Porch");
        await Until(() => fake.Held, "the porch's check is held inside Embed", 10);
        Check(await Press(rig, "unknown", hallOne, "save", new { person = NewPerson(Ana) }, "hall") == "done", "Save of A's card on the hall's page");
        // The hall stopped, so that no later write of its (Ana's last seen) has the porch match its groups again after
        // the held check.
        await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "hall", enabled = false });
        await Until(() => rig.Home.Logs(PyLogLevel.Debug).Contains("Hall people: People detector stopped"), "the hall stops");
        var porchPeople = Member(rig.Detector(), "People");
        await Until(() => VisionTests.Items((object)porchPeople.People).Any(person => ((dynamic)person).FullName == Ana), "the porch's list has Ana", 10);
        var from = fake.Embedded("Porch").Length;
        fake.Release();
        await Until(() => fake.Embedded("Porch").Length >= from + 2 * 4, "the held check and one more", 10);

        var holding = ((dynamic[])AllGroups(unknown)).Where(group => HoldsFace(unknown, group, a)).ToArray();
        Check(holding.Length == 0, "no group of the porch's holds A's face: " + string.Join(", ", holding));
        Check(rig.Labels().Contains(First(Ana)), "the porch names A Ana: " + string.Join(", ", rig.Labels()));
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: a Save on another detector's page while this one's check is embedding is not undone by the check: the named face goes to no group of this one's");
    }

    // A press never waits on a check, and no lock of one detector is held while another's is waited for: two detectors on
    // one store check without a pause, an embedding of one or the other held now and then, while the pages are pressed.
    static async Task PressesAndChecksOfTwoDetectorsNeverWaitOnEachOther()
    {
        using var fake = new Fake(await Spots());
        Source("camera.hall", Porch());
        Show();
        await using var rig = await Rig.Start("presses");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await rig.Saved("hall", rig.Definition("Hall people", "Hall", Arguments("camera.hall", ("LearnNewLooks", false)), Faking("Hall")));
        await Until(() => CardsOf(Page(rig), "unknown").Length > 0 && CardsOf(Page(rig, "hall"), "unknown").Length > 0, "cards on both pages", 10);
        var random = new Random(4242);
        string[] names = [Ana, Ivan, Mia, Ivo];
        var (pressed, done) = (0, 0);
        for (var round = 0; round < 60; round++)
        {
            var definition = round % 2 == 0 ? "porch" : "hall";
            if (random.Next(3) == 0) fake.Release();
            else if (!fake.Held) fake.HoldAt(type: random.Next(2) == 0 ? "Porch" : "Hall");
            var page = Page(rig, definition);
            var (cards, people) = (CardsOf(page, "unknown"), CardsOf(page, "people"));
            Task<string>? press = random.Next(3) switch
            {
                0 when people.Length >= 2 => Press(rig, "people", (string)people[0]["id"]!, "merge", new { into = Picked((string)people[1]["id"]!) }, definition),
                1 when people.Length > 0 => Press(rig, "people", (string)people[random.Next(people.Length)]["id"]!, "delete", new { }, definition),
                // The person of the name when the page has them, as the panel's dialog would choose them, else a new one.
                _ when cards.Length > 0 && names[random.Next(names.Length)] is var name => Press(rig, "unknown", (string)cards[random.Next(cards.Length)]["id"]!, "save",
                    new { person = people.FirstOrDefault(card => Title(card) == name) is { } known ? Picked((string)known["id"]!) : NewPerson(name) }, definition),
                _ => null,
            };
            if (press != null)
            {
                pressed++;
                Check(await Task.WhenAny(press, Task.Delay(TimeSpan.FromSeconds(10))) == press, $"press {pressed} completes within 10 s");
                if (await press == "done") done++;
            }
            // Paced by the checks, so that faces come back as cards: one more face embedded by either.
            var embedded = fake.Embedded().Length;
            await Until(() => fake.Embedded().Length > embedded, "the checks go on", 10);
        }
        fake.Release();
        var (porch, hall) = (fake.Embedded("Porch").Length, fake.Embedded("Hall").Length);
        await Until(() => fake.Embedded("Porch").Length >= porch + 8 && fake.Embedded("Hall").Length >= hall + 8, "both detectors still check", 10);
        var list = PeopleOf(rig);
        string Ids(object people) => string.Join(",", VisionTests.Items(people).Select(person => (string)((dynamic)person).Id).Order(StringComparer.Ordinal));
        await Until(() =>
        {
            list.Reload();
            var stored = Ids((object)list.People);
            return Ids((object)Member(rig.Detector(), "People").People) == stored && Ids((object)Member(rig.Detector("hall"), "People").People) == stored;
        }, "both detectors' lists agree with the store", 10);
        Check(done >= 20, $"most presses were done: {done} of {pressed}");
        NoNameIsLogged(rig);
        Console.WriteLine($"PASS people detector: {pressed} presses of Save, Merge and Delete on two detectors' pages, with their checks held and let go now and then, each complete, and the lists agree");
    }

    // As seen live: a learned sample removed on the page, while the same face stays in view, was learned again at the
    // next check. The ten minutes go from the removal; the test moves the person's clock past them by writing the
    // removal's time ten minutes back into the shared store, as another detector's write reaches this one.
    static async Task ARemovedLearnedSampleIsNotLearnedAgainAtOnce()
    {
        using var fake = new Fake(await Spots());
        Show();
        await using var rig = await Rig.Start("unlearn");
        var list = PeopleOf(rig);
        string anaId = Named(list, Ana, 1).Id;
        fake.Embeds(0, PeopleTests.At(PeopleTests.Unit(1), 0.8, 21));
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(), Faking("Porch")));
        await Until(() => LearnedOf(Reread(list, Ana)).Length == 1, "Ana gains a learned sample: " + rig.Lines(), 10);
        string learnedId = ((dynamic)LearnedOf(Reread(list, Ana))[0]).Id;

        Check(await Press(rig, "people", anaId, "samples", new { }) == "done", "Samples of Ana");
        Check(await Press(rig, "samples", learnedId, "remove", new { }) == "done", "Remove of the learned sample");
        var from = fake.Embedded("Porch").Length;
        await Until(() => fake.Embedded("Porch").Length >= from + 10 * 4, "ten checks more, with Ana's face in each", 10);
        Check(LearnedOf(Reread(list, Ana)).Length == 0, "the face is not learned again within ten minutes of the removal: " + LearnedOf(Reread(list, Ana)).Length);

        rig.Shared().Update<JsonObject>($"people/{anaId}", record =>
        {
            record!["learnedChanged"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("o", CultureInfo.InvariantCulture);
            return record;
        });
        await Until(() => LearnedOf(Reread(list, Ana)).Length == 1, "ten minutes after the removal it is learned again: " + rig.Lines(), 10);
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: a learned sample removed on the page while its face stays in view is not learned again until ten minutes after the removal");
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
                seen.Invoke(groups, new object?[] { (object)PeopleTests.Embedding(PeopleTests.Unit(seed)), (Func<byte[]>)(() => PeopleTests.Crop(seed)), 30.0, now - ago + TimeSpan.FromMinutes(i), null, null });
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
        Check(rig.Labels().Contains(First(Ana)), "the camera still names Ana");
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
        await Until(() => rig.Labels().Contains(First(Ana)), "and recognises: " + string.Join(", ", rig.Labels()));
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
        Check(people.Length == 1 && ((dynamic)people[0]).FullName == Ana && ((object[])PeopleTests.Items(((dynamic)people[0]).Samples)).Length == 1, "and the same people");
        await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = false });
        await Until(() => fake.Closed("Porch") == 2, "the second stop closes the second recogniser");
        Check(fake.UsedClosed == 0, $"a closed recogniser is never asked: {fake.UsedClosed} times");
        Console.WriteLine("PASS people detector: Stop closes the recogniser, which no check uses after, and a restart keeps the people and the groups");
    }

    // ---- The page, read and pressed as the panel does ----

    /// <summary>The page of the device <c>door</c> of <paramref name="definition"/>, as the panel reads it; null when it has none or the definition is not running.</summary>
    static JsonObject? Page(Rig rig, string definition = "porch")
    {
        var read = Rig.Answer(rig.Api.DeviceUiRequest, new { operation = "read", definitionId = definition }).GetAwaiter().GetResult();
        Check(read["ok"]!.GetValue<bool>(), "the page is read: " + read.ToJsonString());
        var result = read["result"]!.AsObject();
        if (!result["running"]!.GetValue<bool>()) return null;
        return result["pages"]!.AsArray().Select(page => page!.AsObject()).SingleOrDefault(page => (string)page["nodeKey"]! == "door");
    }

    /// <summary>The list of the page with that id; null when it has none.</summary>
    static JsonObject? ListOf(JsonObject? page, string id) =>
        page?["lists"]!.AsArray().Select(list => list!.AsObject()).SingleOrDefault(list => (string)list["id"]! == id);

    /// <summary>The cards of the page's list; none when it has no such list.</summary>
    static JsonObject[] CardsOf(JsonObject? page, string list) => ListOf(page, list)?["cards"]!.AsArray().Select(card => card!.AsObject()).ToArray() ?? [];

    static JsonObject? CardOf(JsonObject? page, string list, string id) => CardsOf(page, list).SingleOrDefault(card => (string)card["id"]! == id);

    static string? Title(JsonObject card) => (string?)card["title"];
    static string? Detail(JsonObject card) => (string?)card["detail"];
    static JsonObject? FieldOf(JsonObject card, string key) => card["fields"]!.AsArray().Select(f => f!.AsObject()).SingleOrDefault(f => (string)f["key"]! == key);
    static string[] FieldKeys(JsonObject card) => [.. card["fields"]!.AsArray().Select(f => (string)f!["key"]!)];
    static string[] ActionIds(JsonObject card) => [.. card["actions"]!.AsArray().Select(a => (string)a!["id"]!)];
    static JsonObject ActionOf(JsonObject card, string id) => card["actions"]!.AsArray().Select(a => a!.AsObject()).Single(a => (string)a["id"]! == id);
    static string[] Texts(JsonNode? array) => array == null ? [] : [.. array.AsArray().Select(item => (string)item!)];

    /// <summary>The options of the page's set with that id; none when it has no such set.</summary>
    static JsonObject[] OptionsOf(JsonObject? page, string set = "people") =>
        page?["options"]!.AsArray().Select(each => each!.AsObject()).SingleOrDefault(each => (string)each["id"]! == set)?["items"]!.AsArray().Select(item => item!.AsObject()).ToArray() ?? [];

    /// <summary>Presses an action of a card of <paramref name="definition"/>'s page with the values given: <c>done</c>, <c>changed</c>, or the status and its sentence.</summary>
    static async Task<string> Press(Rig rig, string list, string card, string action, object values, string definition = "porch")
    {
        var acted = await rig.Ok(rig.Api.DeviceUiRequest, new { operation = "act", definitionId = definition, nodeKey = "door", list, card, action, values });
        var message = acted["message"]?.GetValue<string>();
        return message == null ? (string)acted["status"]! : $"{acted["status"]}: {message}";
    }

    /// <summary>The JPEG of a card's image, as the panel fetches it; null when the page has no such image.</summary>
    static async Task<byte[]?> Picture(Rig rig, JsonObject card, string definition = "porch")
    {
        if (card["image"]?.GetValue<string>() is not { } hash) return null;
        var answer = await Rig.Answer(rig.Api.DeviceUiRequest, new { operation = "image", definitionId = definition, nodeKey = "door", hash });
        return answer["ok"]!.GetValue<bool>() ? Convert.FromBase64String((string)answer["result"]!["jpeg"]!) : null;
    }

    /// <summary>The cards waiting on the device <c>door</c> of <paramref name="definition"/>, as the Entities page counts them.</summary>
    static async Task<int> Waiting(Rig rig, string definition = "porch")
    {
        var counts = await rig.Ok(rig.Api.DeviceUiRequest, new { operation = "counts" });
        return counts["attention"]![definition]?["door"]?.GetValue<int>() ?? 0;
    }

    /// <summary>Home Assistant's entities, as <c>State</c> reads them, for the test: each camera of <paramref name="names"/> with its friendly name; any other is not there.</summary>
    static IDisposable Cameras(params (string EntityId, string Name)[] cameras)
    {
        var before = PyInterop.Entity;
        var names = cameras.ToDictionary(camera => camera.EntityId, camera => camera.Name, StringComparer.Ordinal);
        PyInterop.Entity = id => names.TryGetValue(id, out var name)
            ? new HasEntityState { EntityId = id, Domain = "camera", ObjectId = id["camera.".Length..], State = "idle", Attributes = new() { ["friendly_name"] = name } }
            : null;
        return new Restore(() => PyInterop.Entity = before);
    }

    sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    // A time relative to now, as the page writes one drawn just now.
    const string Moment = "a moment ago";

    // Names no log line may hold, beside Ana and Ivan.
    const string Mia = "Mia Kovac", Ivo = "Ivo Babic", Eva = "Eva Juric";

    static void NoNameIsLogged(Rig rig) =>
        Check(!rig.Home.Logs().Any(l => new[] { Ana, Ivan, Mia, Ivo, Eva }.Any(name => l.Contains(name, StringComparison.OrdinalIgnoreCase))), "no line names anyone: " + rig.Lines());

    static async Task ThePageListsTheUnknownFacesToNameOrDismiss()
    {
        using var fake = new Fake(await Spots());
        Show();
        await using var rig = await Rig.Start("page-unknown");
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        var ivan = Named(list, Ivan, 2);
        fake.Embeds(0, PeopleTests.At(PeopleTests.Unit(1), 0.8, 11));
        fake.Embeds(1, PeopleTests.At(PeopleTests.Unit(2), 0.45, 12));
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => CardsOf(Page(rig), "unknown").Length == 3, "the maybe and the two strangers are drawn: " + Page(rig)?.ToJsonString(), 10);
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");

        var page = Page(rig);
        Check(page!["lists"]!.AsArray().Select(l => (string)l!["id"]!).SequenceEqual(["unknown", "people"]), "the page has the unknown faces, then the people: " + page.ToJsonString());
        // The people are a set of options the pick fields choose from, once for the page, as the People cards show them.
        var options = OptionsOf(page);
        Check(page["options"]!.AsArray().Count == 1 && options.Select(o => (string)o["id"]!).SequenceEqual(CardsOf(page, "people").Select(c => (string)c["id"]!)),
            "one set, people, of the people in the People list's order: " + page["options"]!.ToJsonString());
        foreach (var (option, person) in options.Zip(CardsOf(page, "people")))
            Check((string)option["title"]! == Title(person) && (string?)option["detail"] == Detail(person) && (string?)option["image"] == (string?)person["image"] && option["image"] != null,
                "an option has the person's full name, the People card's detail and its picture: " + option.ToJsonString() + " | " + person.ToJsonString());
        Check(options.Select(o => (string)o["title"]!).SequenceEqual([Ana, Ivan]), "the options are titled by full name: " + string.Join(", ", options.Select(o => (string)o["title"]!)));
        var unknown = ListOf(page, "unknown")!;
        Check((string)unknown["title"]! == "Unknown faces" && unknown["attention"]!.GetValue<bool>() && (string)unknown["empty"]! == "No unknown faces.", "the unknown list: " + unknown.ToJsonString());
        var groups = Shown(rig);
        var cards = CardsOf(page, "unknown");
        Check(cards.Select(c => (string)c["id"]!).SequenceEqual(groups.Select(g => (string)g.Id)), "a card for each shown group, in the order shown (the order the groups were first shown in): " + string.Join(",", cards.Select(c => (string)c["id"]!)));
        Check(page["attention"]!.GetValue<int>() == 3 && await Waiting(rig) == 3, $"the three wait: {page["attention"]}, {await Waiting(rig)}");
        var unknownFaces = Member(rig.Detector(), "Unknown");
        foreach (var card in cards)
        {
            string id = (string)card["id"]!;
            var group = groups.Single(g => (string)g.Id == id);
            Check(System.Text.RegularExpressions.Regex.IsMatch(Title(card) ?? "", @"^Seen \d+ times · last (a moment|\d+ minutes?) ago$"), "the card says how often and when: " + Title(card));
            Check((await Picture(rig, card))?.SequenceEqual((byte[])unknownFaces.Crop(id)) == true, "the card shows the group's crop");
            Check(FieldKeys(card).SequenceEqual(["person"]), "one field, Person: " + card.ToJsonString());
            var person = FieldOf(card, "person")!;
            Check((string)person["label"]! == "Person" && (string)person["type"]! == "pick" && (string)person["set"]! == "people" && person["required"]!.GetValue<bool>() && person["except"] == null,
                "Person is required and chooses from the people: " + person.ToJsonString());
            var form = person["add"]!.AsObject();
            var fields = form["fields"]!.AsArray().Select(f => f!.AsObject()).ToArray();
            Check((string)form["title"]! == "New person" && fields.Select(f => (string)f["key"]!).SequenceEqual(["first_name", "last_name"])
                && (string)fields[0]["label"]! == "First name" && (string)fields[0]["type"]! == "text" && fields[0]["required"]!.GetValue<bool>()
                && (string)fields[1]["label"]! == "Last name" && (string)fields[1]["type"]! == "text" && !fields[1]["required"]!.GetValue<bool>(),
                "or a new person, of a required First name and a Last name: " + form.ToJsonString());
            Check(ActionIds(card).SequenceEqual(["save", "dismiss"]) && (string)ActionOf(card, "save")["style"]! == "primary" && ActionOf(card, "save")["validate"]!.GetValue<bool>()
                && !ActionOf(card, "dismiss")["validate"]!.GetValue<bool>() && ActionOf(card, "dismiss")["confirm"] == null, "Save, the main action, and Dismiss, which does not validate: " + card.ToJsonString());
            if (group.SuggestedPersonId == (string)ivan.Id)
                Check((string?)person["value"] == (string)ivan.Id && Detail(card) == $"Might be {Ivan}", "the maybe has Ivan chosen and suggested: " + card.ToJsonString());
            else
                Check(person["value"] == null && Detail(card) == null, "a stranger's Person is not chosen and nothing is suggested: " + card.ToJsonString());
        }
        var maybe = groups.Single(g => g.SuggestedPersonId == (string)ivan.Id);
        var (first, second) = (groups.First(g => g.SuggestedPersonId == null), groups.Last(g => g.SuggestedPersonId == null));

        // Save with a new person: a person of the form's two names with the group's samples as named samples, and the group gone.
        int faces = unknownFaces.Samples((string)first.Id).Count;
        Check(await Press(rig, "unknown", (string)first.Id, "save", new { person = new { @new = new { first_name = $"  {First(Mia)} ", last_name = $" {Names(Mia).Last}  " } } }) == "done", "Save of a new person");
        var mia = Reread(list, Mia);
        var samples = (object[])PeopleTests.Items(mia.Samples);
        Check(mia.FirstName == First(Mia) && mia.LastName == Names(Mia).Last, $"Mia has the two names, trimmed: {mia.FirstName}|{mia.LastName}");
        Check(samples.Length == faces && faces > 0 && samples.All(s => !((dynamic)s).Learned), $"Mia has the group's {faces} faces as named samples: {samples.Length}");
        page = Page(rig);
        Check(CardOf(page, "unknown", (string)first.Id) == null && Shown(rig).Length == 2 && CardOf(page, "people", (string)mia.Id) is { } miaCard && Title(miaCard) == Mia,
            "the group is gone, and Mia is listed: " + page?.ToJsonString());
        Check(OptionsOf(page).Select(o => (string)o["title"]!).SequenceEqual([Ana, Ivan, Mia]), "and is an option: " + page!["options"]!.ToJsonString());
        Check(page!["attention"]!.GetValue<int>() == 2 && await Waiting(rig) == 2, "two wait");

        // A new person of a full name someone has, in another case, is refused: the user chooses them from the list.
        var refused = await Press(rig, "unknown", (string)second.Id, "save", new { person = NewPerson(Ana.ToLowerInvariant()) });
        Check(refused == $"refused: There is already a person named {Ana}: choose them from the list.", "Save of a new person of Ana's name: " + refused);
        Check(CardOf(Page(rig), "unknown", (string)second.Id) != null && ((object[])PeopleTests.Items(Reread(list, Ana).Samples)).Length == 1, "the group stays, and Ana gains nothing");

        // Save with a person chosen: the group's samples go to that person, by id. A stranger's face looks unlike Ana, so
        // the first Save asks (ASaveForSomeoneTheFacesLookUnlikeAsksFirst), and the second saves.
        faces = unknownFaces.Samples((string)second.Id).Count;
        Check(await Press(rig, "unknown", (string)second.Id, "save", new { person = Picked((string)Reread(list, Ana).Id) }) == Unlike(Ana), "Save of Ana, chosen, asks");
        Check(await Press(rig, "unknown", (string)second.Id, "save", new { person = Picked((string)Reread(list, Ana).Id) }) == "done", "Save of Ana, chosen");
        var ana = Reread(list, Ana);
        Check(((object[])PeopleTests.Items(ana.Samples)).Count(s => !((dynamic)s).Learned) == 1 + faces, $"Ana gains the group's {faces} faces as named samples");
        Check(((object[])PeopleTests.Items(list.People)).Length == 3 && ana.FullName == Ana, "no one new, and Ana keeps her name");
        Check(CardOf(Page(rig), "unknown", (string)second.Id) == null, "the group is gone");

        // The maybe's suggestion, as drawn, is the choice of a press that sends nothing for Person.
        faces = unknownFaces.Samples((string)maybe.Id).Count;
        Check(await Press(rig, "unknown", (string)maybe.Id, "save", new { }) == "done", "Save of the maybe as drawn");
        Check(((object[])PeopleTests.Items(Reread(list, Ivan).Samples)).Length == 1 + faces, $"Ivan gains the maybe's {faces} faces");
        Check(CardsOf(Page(rig), "unknown").Length == 0, "the maybe's card is gone");
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: the page draws the people as a set of options first, and lists each shown group with its crop, its sightings and a required Person chosen from them (a maybe's chosen) or a new person; Save makes a person of the two names or adds to the one chosen by id, refuses a name someone has, and the attention counts the groups");
    }

    // Naming a group is a sighting of its person at this camera, at the group's last sighting: a new person's card and option
    // say so at once; a person last seen earlier, here within the minute, moves forward; one seen since at another camera stays.
    // The panel reads the page again every 2 seconds while someone works on a card: a card stays where it is when its
    // group or another is seen again, and a new one is added at the end, so that a click never lands on a card that moved
    // under the pointer.
    static async Task TheUnknownCardsKeepTheirPlaces()
    {
        using var fake = new Fake(await Spots());
        Show();
        await using var rig = await Rig.Start("page-order");
        var list = PeopleOf(rig);
        string[] names = [Ana, Ivan, Mia, Ivo];
        for (var spot = 0; spot < names.Length; spot++) Named(list, names[spot], spot + 1);
        // The face at each of the four spots embedded is the one named for it, or a stranger of its own.
        void Known(int spot) => fake.Embeds(spot, PeopleTests.At(PeopleTests.Unit(spot + 1), 0.8, 20 + spot));
        void Stranger(int spot) => fake.Embeds(spot, PeopleTests.Unit(8000 + spot));
        string[] Ids() => [.. CardsOf(Page(rig), "unknown").Select(card => (string)card["id"]!)];
        string Drawn() => string.Join(",", Ids());
        Stranger(0);
        Known(1);
        Known(2);
        Known(3);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => Ids().Length == 1, "the first stranger is drawn: " + Drawn(), 10);
        var first = Ids()[0];

        // The first stranger goes, and another comes: its card is added after the first's.
        Known(0);
        Stranger(1);
        await Until(() => Ids().Length == 2, "the second stranger is drawn: " + Drawn(), 10);
        var second = Ids().Single(id => id != first);
        Check(Ids().SequenceEqual([first, second]), $"the second card is added after the first: {Drawn()}, the first {first}");

        // The first is seen again, and a third comes: the first stays first, and the third is last.
        Stranger(0);
        Known(1);
        Stranger(2);
        await Until(() => Ids().Length == 3, "the third stranger is drawn: " + Drawn(), 10);
        var third = Ids().Single(id => id != first && id != second);
        Check(Ids().SequenceEqual([first, second, third]), $"the cards keep their places, and the third is last: {Drawn()}, the first {first}, the second {second}");

        // The first is dismissed while its face is in frame, alone: the group made of it again is drawn last.
        Known(2);
        Check(await Press(rig, "unknown", first, "dismiss", new { person = (object?)null }) == "done", "Dismiss of the first");
        await Until(() => Ids().Length == 3 && !Ids().Contains(first), "the first face's group is made and drawn again: " + Drawn(), 10);
        var again = Ids().Single(id => id != second && id != third);
        Check(Ids().SequenceEqual([second, third, again]), $"the group made again is drawn last, the others where they were: {Drawn()}, the second {second}, the third {third}");
        Hide();
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: the unknown cards keep their places across checks, a new one is added at the end, and a group made again comes back last");
    }

    static async Task NamingAFaceIsASightingOfItsPerson()
    {
        using var cameras = Cameras(("camera.porch", "Porch camera"));
        using var fake = new Fake(await Spots());
        Show();
        await using var rig = await Rig.Start("page-sighting");
        var list = PeopleOf(rig);
        var ana = Named(list, Ana, 1);
        var ivan = Named(list, Ivan, 2);
        // Ana is recognised at each check, her last seen written at the first only: the checks take less than a minute.
        fake.Embeds(0, PeopleTests.At(PeopleTests.Unit(1), 0.8, 11));
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => CardsOf(Page(rig), "unknown").Length == 3, "three strangers are drawn: " + Page(rig)?.ToJsonString(), 10);
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");
        var groups = Shown(rig);
        DateTimeOffset LastOf(int group) => (DateTimeOffset)groups[group].Last;
        (DateTimeOffset? At, string? Camera) SeenOf(string name)
        {
            var person = Reread(list, name);
            return (person.LastSeen, person.LastSeenAt);
        }
        Check(SeenOf(Ana).At < LastOf(1) && SeenOf(Ana).Camera == "camera.porch", $"Ana was last written as seen before the groups' last sighting: {SeenOf(Ana)} | {LastOf(1):O}");
        // Ivan is seen at the hall after the groups were last seen here.
        var atTheHall = DateTimeOffset.UtcNow;
        list.Seen((string)ivan.Id, atTheHall, "camera.hall");

        Check(await Press(rig, "unknown", (string)groups[0].Id, "save", new { person = NewPerson(Mia) }) == "done", "Save of a new person");
        Check(SeenOf(Mia) == (LastOf(0), "camera.porch"), $"Mia is last seen at the group's last sighting, here: {SeenOf(Mia)} | {LastOf(0):O}");
        var mia = (string)Reread(list, Mia).Id;
        Check(Detail(CardOf(Page(rig), "people", mia)!)!.EndsWith($" · last seen {Moment} at Porch camera", StringComparison.Ordinal), "her card says so at once: " + Page(rig)?.ToJsonString());
        Check(((string?)OptionsOf(Page(rig)).Single(o => (string)o["id"]! == mia)["detail"])!.EndsWith($" · last seen {Moment} at Porch camera", StringComparison.Ordinal),
            "and her option: " + Page(rig)?["options"]?.ToJsonString());

        // A stranger's face looks unlike Ana and Ivan: the first Save asks, and the second saves.
        Check(await Press(rig, "unknown", (string)groups[1].Id, "save", new { person = Picked((string)ana.Id) }) == Unlike(Ana), "Save of Ana, chosen, asks");
        Check(await Press(rig, "unknown", (string)groups[1].Id, "save", new { person = Picked((string)ana.Id) }) == "done", "Save of Ana, chosen");
        Check(SeenOf(Ana) == (LastOf(1), "camera.porch"), $"Ana's last seen moves forward to the group's last sighting, within the minute of the one written: {SeenOf(Ana)} | {LastOf(1):O}");

        Check(await Press(rig, "unknown", (string)groups[2].Id, "save", new { person = Picked((string)ivan.Id) }) == Unlike(Ivan), "Save of Ivan, chosen, asks");
        Check(await Press(rig, "unknown", (string)groups[2].Id, "save", new { person = Picked((string)ivan.Id) }) == "done", "Save of Ivan, chosen");
        Check(SeenOf(Ivan) == (atTheHall, "camera.hall"), $"Ivan, seen at the hall since, stays: {SeenOf(Ivan)}");
        Check(Detail(CardOf(Page(rig), "people", (string)ivan.Id)!)!.EndsWith($" · last seen {Moment} at camera.hall", StringComparison.Ordinal), "his card names the hall: " + Page(rig)?.ToJsonString());
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: naming a face is a sighting of its person at the group's last sighting here, written at once, and never moves last seen back");
    }

    static async Task SavingAFaceForAFullPersonKeepsTheirMostDifferentSamples()
    {
        using var cameras = Cameras(("camera.porch", "Porch camera"));
        var spots = await Spots();
        using var fake = new Fake(spots);
        // Each face of the porch a look on an axis of its own, unlike every sample of Ana's.
        foreach (var spot in Enumerable.Range(0, spots.Length)) fake.Embeds(spot, PeopleTests.Axis(30 + spot));
        Show();
        await using var rig = await Rig.Start("page-full");
        var list = PeopleOf(rig);
        // Ana has 20 named samples: eighteen on axes of their own, and a pair 0.9 alike, the older second.
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        var given = Enumerable.Range(0, 18).Select(axis => (object)PeopleTests.NewSample(PeopleTests.Axis(axis), PeopleTests.Crop(2000 + axis), at.AddMinutes(axis), "camera.elsewhere"))
            .Append((object)PeopleTests.NewSample(PeopleTests.Axis(18), PeopleTests.Crop(2018), at.AddMinutes(30), "camera.elsewhere"))
            .Append((object)PeopleTests.NewSample(PeopleTests.Between(18, 19, 0.9), PeopleTests.Crop(2019), at.AddMinutes(20), "camera.elsewhere"));
        var ana = list.Add(First(Ana), Names(Ana).Last, PeopleTests.Samples([.. given]));
        string anaId = ana.Id;
        string older = ((object[])PeopleTests.Items(ana.Samples)).Select(s => (dynamic)s).Single(s => s.At == at.AddMinutes(20)).Id;
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => CardsOf(Page(rig), "unknown").Length == 4, "four new looks are drawn: " + Page(rig)?.ToJsonString(), 10);
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");
        var group = Shown(rig)[0];
        string groupId = group.Id;
        DateTimeOffset last = group.Last;

        // The look is unlike every sample of hers: the first Save asks, and changes nothing of hers.
        var unknown = Member(rig.Detector(), "Unknown");
        Check(await Press(rig, "unknown", groupId, "save", new { person = Picked(anaId) }) == Unlike(Ana), "the first Save for a person with 20 named samples asks");
        var asked = ((object[])PeopleTests.Items(Reread(list, Ana).Samples)).Select(s => (dynamic)s).ToArray();
        Check(unknown.Find(groupId) != null && asked.Length == 20 && asked.Any(s => s.Id == older) && asked.All(s => s.Camera != "camera.porch")
            && rig.Shared().Keys($"samples/{anaId}/").Count == 40 && Reread(list, Ana).LastSeen == null, "her samples are as they were, and the card stays");
        Check(await Press(rig, "unknown", groupId, "save", new { person = Picked(anaId) }) == "done", "Save for a person with 20 named samples is not refused");
        Check(unknown.Find(groupId) == null && CardOf(Page(rig), "unknown", groupId) == null && CardsOf(Page(rig), "unknown").Length == 3, "the card goes: " + Page(rig)?.ToJsonString());
        var person = Reread(list, Ana);
        var named = ((object[])PeopleTests.Items(person.Samples)).Select(s => (dynamic)s).Where(s => !s.Learned).ToArray();
        Check(named.Length == 20 && named.Any(s => s.Camera == "camera.porch") && named.All(s => s.Id != older), "she keeps 20: the new look in, the older of her closest pair out");
        Check(rig.Shared().Keys($"samples/{anaId}/{older}/").Count == 0 && rig.Shared().Keys($"samples/{anaId}/").Count == 40, "the sample gone leaves no key");
        Check(person.LastSeen == last && person.LastSeenAt == "camera.porch", $"the naming is a sighting at the group's last: {person.LastSeen} at {person.LastSeenAt}");
        Check(Detail(CardOf(Page(rig), "people", anaId)!)!.StartsWith("20 samples · last seen", StringComparison.Ordinal), "her card is drawn again: " + Page(rig)?.ToJsonString());
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: Save names a face for a person with 20 named samples: a look unlike hers is asked about first, changing nothing, and at the second Save is kept in place of the older of their closest pair, the card goes, and it is a sighting");
    }

    /// <summary>What a first Save answers when the card's faces look unlike the person chosen, as <see cref="Press"/> gives it.</summary>
    static string Unlike(string name) => $"refused: This face does not look like {name}'s samples. Press Save again to add it to {name} anyway.";

    /// <summary>How many Saves the running detector remembers having asked about: its private <c>asked</c>.</summary>
    static int Asking(SharpDevice device)
    {
        var type = device.GetType();
        while (type != null && type.Name != "PeopleDetector") type = type.BaseType;
        var field = type?.GetField("asked", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("PeopleDetector has no asked");
        return ((System.Collections.ICollection)field.GetValue(device)!).Count;
    }

    // A face named for someone it looks nothing like is most likely the wrong person chosen: the first Save asks, and stores
    // nothing; a second Save of the card for the same person saves it as any Save does. Another person chosen in between is
    // judged afresh. A new person, or a person the faces are alike enough to, is saved at the first Save.
    static async Task ASaveForSomeoneTheFacesLookUnlikeAsksFirst()
    {
        using var cameras = Cameras(("camera.porch", "Porch camera"));
        var spots = await Spots();
        var four = Embeddable(spots);
        using var fake = new Fake(spots);
        // Three strangers, and a face 0.45 alike to Ivan's sample: a maybe, alike enough to him.
        for (var i = 0; i < 3; i++) fake.Embeds(four[i], PeopleTests.Unit(8100 + i));
        fake.Embeds(four[3], PeopleTests.At(PeopleTests.Unit(2), 0.45, 8103));
        Show();
        await using var rig = await Rig.Start("page-unlike");
        var list = PeopleOf(rig);
        string anaId = Named(list, Ana, 1).Id, ivanId = Named(list, Ivan, 2).Id;
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => CardsOf(Page(rig), "unknown").Length == 4, "the maybe and the three strangers are drawn: " + Page(rig)?.ToJsonString(), 10);
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");
        var unknown = Member(rig.Detector(), "Unknown");
        var groups = Shown(rig);
        string maybe = groups.Single(g => g.SuggestedPersonId == ivanId).Id;
        string[] strangers = [.. groups.Where(g => g.SuggestedPersonId == null).Select(g => (string)g.Id)];
        Check(strangers.Length == 3, "three strangers' cards");
        var first = strangers[0];
        DateTimeOffset last = Shown(rig).Single(g => g.Id == first).Last;
        int faces = unknown.Samples(first).Count;
        var keys = rig.Shared().Keys().Count;
        int SamplesOf(string name) => ((object[])PeopleTests.Items(Reread(list, name).Samples)).Length;
        Task<string> Save(string card, string personId) => Press(rig, "unknown", card, "save", new { person = Picked(personId) });
        bool Untouched() => rig.Shared().Keys().Count == keys && SamplesOf(Ana) == 1 && SamplesOf(Ivan) == 1 && Reread(list, Ana).LastSeen == null
            && Reread(list, Ivan).LastSeen == null && unknown.Find(first) != null && CardOf(Page(rig), "unknown", first) != null;

        // The first Save for Ana asks, and stores nothing: no sample, no crop, no sighting; the card stays.
        Check(await Save(first, anaId) == Unlike(Ana), "the first Save for Ana asks");
        Check(Untouched(), "nothing is stored, and the card stays: " + string.Join(", ", rig.Shared().Keys()));
        Check(Asking(rig.Detector()) == 1, "the detector remembers that it asked");

        // Ivan chosen next is judged afresh, and the faces look unlike him too; Ana chosen after him is asked about again.
        Check(await Save(first, ivanId) == Unlike(Ivan), "a Save for Ivan after the question about Ana asks about Ivan");
        Check(await Save(first, anaId) == Unlike(Ana), "and one for Ana after it asks about Ana again");
        Check(Untouched() && Asking(rig.Detector()) == 1, "nothing is stored yet, and one question is remembered");

        // The second Save for Ana saves: her samples, the card gone, and a sighting at the group's last.
        Check(await Save(first, anaId) == "done", "the second Save for Ana saves");
        var ana = Reread(list, Ana);
        Check(SamplesOf(Ana) == 1 + faces && unknown.Find(first) == null && CardOf(Page(rig), "unknown", first) == null, $"Ana has the card's {faces} faces, and the card goes");
        Check(ana.LastSeen == last && ana.LastSeenAt == "camera.porch", $"the naming is a sighting at the group's last: {ana.LastSeen} at {ana.LastSeenAt}");
        Check(Asking(rig.Detector()) == 0, "nothing of the question is kept once the card goes");

        // A card asked about and then dismissed leaves nothing of the question.
        Check(await Save(strangers[1], ivanId) == Unlike(Ivan), "a Save of another card for Ivan asks");
        Check(await Press(rig, "unknown", strangers[1], "dismiss", new { person = (object?)null }) == "done" && Asking(rig.Detector()) == 0, "Dismiss leaves nothing of it");

        // A new person, and a person the faces are alike enough to, are saved at the first Save.
        Check(await Press(rig, "unknown", strangers[2], "save", new { person = NewPerson(Mia) }) == "done", "a Save for a new person saves at once");
        Check(SamplesOf(Mia) == faces, "Mia is made of the card's faces");
        int maybeFaces = unknown.Samples(maybe).Count;
        Check(await Save(maybe, ivanId) == "done", "a Save for Ivan of a face 0.45 alike to him saves at once");
        Check(SamplesOf(Ivan) == 1 + maybeFaces && CardsOf(Page(rig), "unknown").Length == 0, "Ivan has its faces, and no card is left");
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: a Save for someone the card's faces look unlike asks first and stores nothing, the card staying; the second Save for the same person saves and is a sighting; another person is judged afresh; a new person, or one the faces are alike to, is saved at once");
    }

    // The second Save counts within two minutes of the question, for the faces asked about: later, or once a check has given
    // the card another face, it asks again. Two Saves of one card at once, as from two browsers, run one after the other:
    // the first asks, and the second, within the time and for the same faces, saves; the faces are added once.
    static async Task ASaveAskedAboutAsksAgainLaterOrForOtherFaces()
    {
        var spots = await Spots();
        var four = Embeddable(spots);
        using var fake = new Fake(spots);
        using var clock = new Clock();
        var (stranger, other) = (PeopleTests.Unit(8200), PeopleTests.Unit(8201));
        fake.Embeds(four[0], stranger);
        fake.Embeds(four[1], other);
        foreach (var spot in four[2..]) Uncompared(fake, spot);
        Show();
        await using var rig = await Rig.Start("page-unlike-later");
        var list = PeopleOf(rig);
        string anaId = Named(list, Ana, 1).Id;
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => CardsOf(Page(rig), "unknown").Length == 2, "the two strangers are drawn: " + Page(rig)?.ToJsonString(), 10);
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");
        var unknown = Member(rig.Detector(), "Unknown");
        string card = Shown(rig).Single(group => HoldsFace(unknown, group, stranger)).Id;
        string second = Shown(rig).Single(group => HoldsFace(unknown, group, other)).Id;
        int SamplesOf() => ((object[])PeopleTests.Items(Reread(list, Ana).Samples)).Length;
        Task<string> Save(string id) => Press(rig, "unknown", id, "save", new { person = Picked(anaId) });

        // A Save more than two minutes after the question asks again.
        Check(await Save(card) == Unlike(Ana), "the first Save asks");
        clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        Check(await Save(card) == Unlike(Ana), "a Save two minutes and a second later asks again");

        // A check gives the card another face: the faces asked about are not the card's any more, and the next Save asks.
        clock.Advance(TimeSpan.FromMinutes(1));
        fake.Embeds(four[0], PeopleTests.At(stranger, 0.7, 8202));
        Show();
        await Until(() => unknown.Samples(card).Count == 2, "the card's group keeps the new face", 10);
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");
        Check(await Save(card) == Unlike(Ana), "a Save after the card's faces changed asks again");
        Check(SamplesOf() == 1 && unknown.Find(card) != null, "nothing is stored yet");

        // Within the two minutes, the Save after the question saves.
        clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        Check(await Save(card) == "done", "a Save two minutes less a second after the question saves");
        Check(SamplesOf() == 3 && unknown.Find(card) == null, "Ana has the card's two faces, and the card goes");

        // Two Saves of the other card at once: one asks and one saves, and its face is added once.
        var both = await Task.WhenAll(Save(second), Save(second));
        Check(both.Order(StringComparer.Ordinal).SequenceEqual(new[] { "done", Unlike(Ana) }.Order(StringComparer.Ordinal)), "of two Saves at once one asks and one saves: " + string.Join(" | ", both));
        Check(SamplesOf() == 4 && unknown.Find(second) == null && Asking(rig.Detector()) == 0, "Ana has its face once, the card goes, and no question is kept");
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: a Save asked about asks again two minutes later or once the card's faces changed, and saves within the two minutes; of two Saves at once one asks and one saves");
    }

    static async Task AStrangerIsDismissed()
    {
        using var fake = new Fake(await Spots());
        Show();
        await using var rig = await Rig.Start("page-dismiss");
        var list = PeopleOf(rig);
        var ivan = Named(list, Ivan, 2);
        fake.Embeds(1, PeopleTests.At(PeopleTests.Unit(2), 0.45, 12));
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => CardsOf(Page(rig), "unknown").Length == 4, "the maybe and the three strangers are drawn: " + Page(rig)?.ToJsonString(), 10);
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");
        var maybe = Shown(rig).Single(g => g.SuggestedPersonId == (string)ivan.Id);
        var stranger = Shown(rig).First(g => g.SuggestedPersonId == null);

        // Save with nothing chosen and no new person: Person is required.
        Check(await Press(rig, "unknown", (string)stranger.Id, "save", new { person = (object?)null }) == "refused: Person is required.", "Save with nothing chosen");
        // Dismiss, with nothing chosen: the group goes, and its samples with it.
        foreach (var group in Shown(rig))
            Check(await Press(rig, "unknown", (string)group.Id, "dismiss", new { person = (object?)null }) == "done", "Dismiss with Person empty");
        var page = Page(rig);
        Check(CardsOf(page, "unknown").Length == 0 && Shown(rig).Length == 0 && page!["attention"]!.GetValue<int>() == 0 && await Waiting(rig) == 0, "nothing waits: " + page?.ToJsonString());
        Check(rig.DeviceData().Keys("groups/").Count == 0, "no group's key is left: " + string.Join(", ", rig.DeviceData().Keys("groups/")));
        Check(Reread(list, Ivan).Samples.Count == 1 && ((object[])PeopleTests.Items(list.People)).Length == 1, "Ivan, only suggested, gained nothing, and no one was made");
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: Save with no person chosen is refused, Dismiss removes a group with its samples, the maybe's too, and nothing is made of them");
    }

    static async Task ThePageListsThePeopleToRenameMergeAndDelete()
    {
        using var cameras = Cameras(("camera.porch", "Porch camera"));
        using var fake = new Fake(await Spots());
        Show();
        await using var rig = await Rig.Start("page-people");
        var list = PeopleOf(rig);
        var ana = Named(list, Ana, 1);
        var ivan = Named(list, Ivan, 2);
        list.AddSamples((string)ivan.Id, PeopleTests.Samples(PeopleTests.NewSample(PeopleTests.Unit(3), PeopleTests.Crop(3), DateTimeOffset.UtcNow.AddHours(-1), "camera.elsewhere")), false);
        list.AddSamples((string)ivan.Id, PeopleTests.Samples(PeopleTests.NewSample(PeopleTests.At(PeopleTests.Unit(2), 0.7, 4), PeopleTests.Crop(4), DateTimeOffset.UtcNow.AddHours(-2), "camera.elsewhere")), true);
        // Last seen five minutes ago at a camera Home Assistant no longer has.
        list.Seen((string)ivan.Id, DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(-10), "camera.gone");
        fake.Embeds(0, PeopleTests.At(PeopleTests.Unit(1), 0.8, 11));
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => CardOf(Page(rig), "people", (string)ana.Id) is { } card && Detail(card) == $"1 sample · last seen {Moment} at Porch camera",
            "Ana's card says she was seen just now at the camera's friendly name: " + Page(rig)?.ToJsonString(), 10);
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");

        var page = Page(rig);
        var people = ListOf(page, "people")!;
        Check((string)people["title"]! == "People" && !people["attention"]!.GetValue<bool>() && (string)people["empty"]! == "Nobody is named yet.", "the people list: " + people.ToJsonString());
        var cards = CardsOf(page, "people");
        Check(cards.Select(Title).SequenceEqual([Ana, Ivan]) && cards.Select(c => (string)c["id"]!).SequenceEqual([(string)ana.Id, (string)ivan.Id]), "a card for each person, by name: " + people.ToJsonString());
        var (anaCard, ivanCard) = (cards[0], cards[1]);
        Check(Detail(ivanCard) == "3 samples (1 learned) · last seen 5 minutes ago at camera.gone", "Ivan's card counts his samples and names a camera that is gone by its id: " + Detail(ivanCard));
        Check((await Picture(rig, anaCard))?.SequenceEqual(PeopleTests.Crop(1)) == true && (await Picture(rig, ivanCard))?.SequenceEqual(PeopleTests.Crop(2)) == true,
            "each card shows the person's newest named sample's crop");
        Check(FieldKeys(anaCard).SequenceEqual(["first_name", "last_name", "into"]), "First name, Last name and Merge into: " + anaCard.ToJsonString());
        var firstName = FieldOf(anaCard, "first_name")!;
        var lastName = FieldOf(anaCard, "last_name")!;
        var into = FieldOf(anaCard, "into")!;
        Check((string)firstName["label"]! == "First name" && (string)firstName["type"]! == "text" && (string)firstName["value"]! == First(Ana) && firstName["required"]!.GetValue<bool>(),
            "First name has her first name and is required: " + firstName.ToJsonString());
        Check((string)lastName["label"]! == "Last name" && (string)lastName["type"]! == "text" && (string)lastName["value"]! == Names(Ana).Last && !lastName["required"]!.GetValue<bool>(),
            "Last name has her last name: " + lastName.ToJsonString());
        Check((string)into["label"]! == "Merge into" && (string)into["type"]! == "pick" && (string)into["set"]! == "people" && Texts(into["except"]).SequenceEqual([(string)ana.Id])
            && into["add"] == null && into["value"] == null && !into["required"]!.GetValue<bool>(),
            "Merge into chooses from the people but her, with no new one and nothing chosen: " + into.ToJsonString());
        Check(OptionsOf(page).Select(o => (string)o["id"]!).SequenceEqual([(string)ana.Id, (string)ivan.Id]), "the set has them both: " + page!["options"]!.ToJsonString());
        Check(ActionIds(anaCard).SequenceEqual(["save", "merge", "samples", "delete"]), "Save, Merge, Samples and Delete: " + anaCard.ToJsonString());
        Check((string)ActionOf(anaCard, "save")["style"]! == "primary" && (string?)ActionOf(anaCard, "merge")["confirm"] == $"Merge {Ana} into the person chosen in Merge into?"
            && (string?)ActionOf(anaCard, "delete")["confirm"] == $"Delete {Ana} and their samples?" && (string)ActionOf(anaCard, "delete")["style"]! == "danger"
            && !ActionOf(anaCard, "delete")["validate"]!.GetValue<bool>() && !ActionOf(anaCard, "samples")["validate"]!.GetValue<bool>(),
            "Merge and Delete ask first, and Delete and Samples need no field: " + anaCard.ToJsonString());

        // A rename to a full name someone else has is refused in a sentence; to a free one, renames.
        Check(await Press(rig, "people", (string)ivan.Id, "save", new { first_name = First(Ana).ToUpperInvariant(), last_name = Names(Ana).Last!.ToUpperInvariant() })
            == $"refused: There is already a person named {Ana}: choose them from the list.", "a rename to Ana's full name");
        Check(await Press(rig, "people", (string)ivan.Id, "save", new { first_name = $" {First(Ivo)} ", last_name = $" {Names(Ivo).Last} " }) == "done", "a rename");
        Check(Reread(list, Ivo).Id == (string)ivan.Id && Title(CardOf(Page(rig), "people", (string)ivan.Id)!) == Ivo, "Ivan is Ivo now, on the page too");
        Check(OptionsOf(Page(rig)).Select(o => (string)o["title"]!).SequenceEqual([Ana, Ivo]), "and in the set: " + Page(rig)?["options"]?.ToJsonString());

        // Merge needs someone chosen, other than the card's person; it moves Ivo's samples to the one chosen and removes him.
        Check(await Press(rig, "people", (string)ivan.Id, "merge", new { into = (object?)null }) == "refused: Choose a person in Merge into first.", "Merge with no one chosen");
        Check(await Press(rig, "people", (string)ivan.Id, "merge", new { into = Picked((string)ivan.Id) }) == "changed", "Merge into himself, whom the field does not offer");
        Check(((object[])PeopleTests.Items(Reread(list, Ivo).Samples)).Length == 3, "Ivo is as he was");
        Check(await Press(rig, "people", (string)ivan.Id, "merge", new { into = Picked((string)ana.Id) }) == "done", "Merge Ivo into Ana, chosen");
        var merged = Reread(list, Ana);
        var samples = (object[])PeopleTests.Items(merged.Samples);
        Check(((object[])PeopleTests.Items(list.People)).Length == 1 && samples.Length == 4 && samples.Count(s => ((dynamic)s).Learned) == 1, $"Ana has the four samples, one learned: {samples.Length}");
        page = Page(rig);
        Check(CardsOf(page, "people") is [var only] && (string)only["id"]! == (string)ana.Id && Detail(only)!.StartsWith("4 samples (1 learned) · last seen", StringComparison.Ordinal),
            "one card, Ana's, with the samples: " + page?.ToJsonString());
        Check(FieldKeys(CardsOf(page, "people")[0]).SequenceEqual(["first_name", "last_name"]) && ActionIds(CardsOf(page, "people")[0]).SequenceEqual(["save", "samples", "delete"]),
            "alone, she has no Merge into and no Merge: " + page?.ToJsonString());

        // A rename can take the last name away.
        Check(await Press(rig, "people", (string)ana.Id, "save", new { first_name = First(Ana), last_name = "" }) == "done", "a rename to her first name alone");
        Check(Reread(list, First(Ana)).LastName == null && Title(CardsOf(Page(rig), "people")[0]) == First(Ana), "she has no last name now");

        // Delete removes her with her samples.
        Check(await Press(rig, "people", (string)ana.Id, "delete", new { }) == "done", "Delete");
        list.Reload();
        Check(((object[])PeopleTests.Items(list.People)).Length == 0 && rig.Shared().Keys("samples/").Count == 0, "nobody is left, nor a sample: " + string.Join(", ", rig.Shared().Keys("samples/")));
        Check(CardsOf(Page(rig), "people").Length == 0, "the people list is empty: " + Page(rig)?.ToJsonString());
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: the page lists the people by full name with their newest named crop, their samples and where they were last seen by the camera's friendly name; Save renames by first and last name and refuses a taken full name, Merge asks and moves to the person chosen, Delete asks and removes");
    }

    static async Task ThePageListsAPersonsSamplesToRemove()
    {
        using var fake = new Fake(await Spots());
        await using var rig = await Rig.Start("page-samples");
        var list = PeopleOf(rig);
        var ana = Named(list, Ana, 1);
        string anaId = ana.Id;
        list.AddSamples(anaId, PeopleTests.Samples(PeopleTests.NewSample(PeopleTests.Unit(3), PeopleTests.Crop(3), DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(-1), "camera.elsewhere")), false);
        list.AddSamples(anaId, PeopleTests.Samples(PeopleTests.NewSample(PeopleTests.At(PeopleTests.Unit(1), 0.7, 4), PeopleTests.Crop(4), DateTimeOffset.UtcNow.AddHours(-1).AddMinutes(-1), "camera.elsewhere")), true);
        var ivan = Named(list, Ivan, 2);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => CardsOf(Page(rig), "people").Length == 2, "the two are drawn: " + Page(rig)?.ToJsonString());
        Check(ListOf(Page(rig), "samples") == null, "no samples list before Samples");

        Check(await Press(rig, "people", anaId, "samples", new { }) == "done", "Samples");
        var page = Page(rig);
        var shown = ListOf(page, "samples");
        Check(shown != null && (string)shown["title"]! == $"Samples of {Ana}" && !shown["attention"]!.GetValue<bool>(), "the samples list: " + shown?.ToJsonString());
        var held = (object[])PeopleTests.Items(Reread(list, Ana).Samples);
        string IdOf(int seed) => (string)((dynamic)held.Single(s => PeopleTests.Cosine((float[])((dynamic)s).Embedding.Values, seed == 4 ? PeopleTests.At(PeopleTests.Unit(1), 0.7, 4) : PeopleTests.Unit(seed)) > 0.999)).Id;
        var cards = CardsOf(page, "samples");
        Check(cards.Select(c => (string)c["id"]!).SequenceEqual([IdOf(1), IdOf(3), IdOf(4)]), "named first, then learned, the newest first: " + shown!.ToJsonString());
        Check(cards.Select(Title).SequenceEqual([$"Named · {Moment} at camera.elsewhere", "Named · 2 hours ago at camera.elsewhere", "Learned · 1 hour ago at camera.elsewhere"]),
            "each says what it is, when and where: " + string.Join(" | ", cards.Select(Title)));
        Check(ActionIds(cards[0]).SequenceEqual(["remove", "close"]) && cards.Skip(1).All(c => ActionIds(c).SequenceEqual(["remove"])) && cards.All(c => FieldKeys(c).Length == 0),
            "each has Remove, and the first Close: " + shown.ToJsonString());
        foreach (var (card, seed) in cards.Zip([1, 3, 4]))
            Check((await Picture(rig, card))?.SequenceEqual(PeopleTests.Crop(seed)) == true, $"the card of seed {seed} shows its crop");

        // Remove takes a sample; the last named one is refused.
        Check(await Press(rig, "samples", IdOf(3), "remove", new { }) == "done", "Remove of a named sample");
        Check(CardsOf(Page(rig), "samples").Select(c => (string)c["id"]!).SequenceEqual([IdOf(1), IdOf(4)]), "it is gone: " + Page(rig)?.ToJsonString());
        Check(await Press(rig, "samples", IdOf(1), "remove", new { }) == "refused: The last sample a person was named with cannot be removed; delete the person instead.", "Remove of the last named sample");
        Check(await Press(rig, "samples", IdOf(4), "remove", new { }) == "done", "Remove of the learned sample");
        Check(((object[])PeopleTests.Items(Reread(list, Ana).Samples)).Length == 1, "Ana keeps her one named sample");
        Check(Detail(CardOf(Page(rig), "people", anaId)!) == "1 sample · not seen yet", "her card counts it: " + Detail(CardOf(Page(rig), "people", anaId)!));

        // Close takes the list away.
        Check(await Press(rig, "samples", IdOf(1), "close", new { }) == "done", "Close");
        Check(ListOf(Page(rig), "samples") == null, "the samples list is gone: " + Page(rig)?.ToJsonString());

        // The list goes when another detector deletes the person, and when the person is deleted here.
        Check(await Press(rig, "people", (string)ivan.Id, "samples", new { }) == "done", "Ivan's samples");
        Check((string?)ListOf(Page(rig), "samples")?["title"] == $"Samples of {Ivan}", "Ivan's samples are shown");
        list.Delete((string)ivan.Id);
        await Until(() => ListOf(Page(rig), "samples") == null && CardsOf(Page(rig), "people").Length == 1, "Ivan's samples go with him: " + Page(rig)?.ToJsonString(), 2);
        Check(await Press(rig, "people", anaId, "samples", new { }) == "done", "Ana's samples again");
        Check(ListOf(Page(rig), "samples") != null, "they are shown");
        Check(await Press(rig, "people", anaId, "delete", new { }) == "done", "Delete Ana");
        Check(ListOf(Page(rig), "samples") == null && CardsOf(Page(rig), "people").Length == 0, "her samples go with her: " + Page(rig)?.ToJsonString());
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: Samples lists a person's samples, named first and the newest first, with Remove and Close; the last named one cannot be removed, and the list goes with Close and with its person");
    }

    // Each press is made against the page as the test set it up: a check redraws the page when the groups it shows change,
    // or when the people list changed while it ran, so the checks are held from the first change behind the page's back
    // on (one waits inside the fake recogniser), and a change made then is on the page only once a press redraws it.
    static async Task APressForWhatIsGoneIsRefused()
    {
        var spots = await Spots();
        using var fake = new Fake(spots);
        Show();
        await using var rig = await Rig.Start("page-gone");
        var list = PeopleOf(rig);
        // Ana, Ivan, Ivo and Eva are named. (A Save for a person with 20 named samples is not refused:
        // SavingAFaceForAFullPersonKeepsTheirMostDifferentSamples.)
        Named(list, Ana, 1);
        var ivan = Named(list, Ivan, 2);
        var ivo = Named(list, Ivo, 3);
        var eva = Named(list, Eva, 4);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => CardsOf(Page(rig), "unknown").Length == 4, "four strangers are drawn: " + Page(rig)?.ToJsonString(), 10);
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");
        var ids = CardsOf(Page(rig), "unknown").Select(c => (string)c["id"]!).ToArray();
        var unknown = Member(rig.Detector(), "Unknown");
        // The next check with faces waits in its first embedding until the end; its faces, uncompared, change nothing then.
        foreach (var spot in Enumerable.Range(0, spots.Length)) Uncompared(fake, spot);
        fake.HoldAt();
        Show();
        await Until(() => fake.Held, "a check waits in the fake recogniser", 10);
        int People()
        {
            list.Reload();
            return ((object[])PeopleTests.Items(list.People)).Length;
        }

        // A person deleted on another detector, whom the page no longer offers: a press that still chooses him finds the
        // page changed, and nothing is done.
        list.Delete((string)ivo.Id);
        await Until(() => OptionsOf(Page(rig)).All(o => (string)o["id"]! != (string)ivo.Id), "the page's set loses Ivo: " + Page(rig)?["options"]?.ToJsonString(), 2);
        Check(await Press(rig, "unknown", ids[0], "save", new { person = Picked((string)ivo.Id) }) == "changed", "Save of Ivo, gone from the set");
        Check(unknown.Find(ids[0]) != null && CardOf(Page(rig), "unknown", ids[0]) != null && People() == 3, "the group stays, and no one is made or given its faces");

        // A person deleted while the page still offers them: the press is refused, and the page is redrawn without them.
        Member(rig.Detector(), "People").Delete((string)eva.Id);
        Check(OptionsOf(Page(rig)).Any(o => (string)o["id"]! == (string)eva.Id), "the page still offers Eva");
        Check(await Press(rig, "unknown", ids[0], "save", new { person = Picked((string)eva.Id) }) == "refused: This person is no longer in the list.", "Save of Eva, deleted behind the page's back");
        Check(OptionsOf(Page(rig)).All(o => (string)o["id"]! != (string)eva.Id) && unknown.Find(ids[0]) != null, "the refused press redrew the set, and the group stays");
        Check(NoOne(list, Eva) && People() == 2 && rig.Shared().Keys($"samples/{(string)eva.Id}/").Count == 0, "and Eva is not made again: " + string.Join(", ", rig.Shared().Keys("samples/")));

        // Groups gone while the page still shows them: Save and Dismiss are refused, and the page drops them.
        unknown.Remove(ids[1]);
        unknown.Remove(ids[2]);
        Check(CardOf(Page(rig), "unknown", ids[1]) != null, "the page still shows the group removed behind its back");
        Check(await Press(rig, "unknown", ids[1], "save", new { person = NewPerson(Mia) }) == "refused: This face is no longer in the list.", "Save of a group that is gone");
        Check(CardOf(Page(rig), "unknown", ids[1]) == null && CardOf(Page(rig), "unknown", ids[2]) == null, "the refused press redrew the page: " + Page(rig)?.ToJsonString());
        Check(await Press(rig, "unknown", ids[1], "save", new { person = NewPerson(Mia) }) == "changed", "a press after that finds the card gone");
        Check(NoOne(list, Mia), "nobody was made");

        // A group whose crops are gone is not named, and is drawn without a picture.
        var device = rig.DeviceData();
        foreach (var key in device.Keys($"groups/{ids[3]}/").Where(key => key.EndsWith("/crop", StringComparison.Ordinal))) device.Remove(key);
        Check(await Press(rig, "unknown", ids[3], "save", new { person = NewPerson(Mia) }) == "refused: This face has no picture left; dismiss it.", "Save of a group without a picture");
        var pictureless = CardOf(Page(rig), "unknown", ids[3]);
        Check(pictureless != null && pictureless["image"] == null, "its card has no image: " + pictureless?.ToJsonString());
        Check(await Press(rig, "unknown", ids[3], "dismiss", new { person = (object?)null }) == "done", "Dismiss of it");
        Check(NoOne(list, Mia), "nobody was made");

        // A person gone while the page still shows them.
        Member(rig.Detector(), "People").Delete((string)ivan.Id);
        Check(CardOf(Page(rig), "people", (string)ivan.Id) != null, "the page still shows Ivan");
        Check(await Press(rig, "people", (string)ivan.Id, "save", new { first_name = First(Ivo), last_name = Names(Ivo).Last }) == "refused: This person is no longer in the list.", "Save of a person who is gone");
        Check(CardOf(Page(rig), "people", (string)ivan.Id) == null, "the refused press redrew the page");
        Check(NoOne(list, Ivo) && NoOne(list, Ivan), "nobody is renamed");
        NoNameIsLogged(rig);
        Check(rig.Home.Logs(PyLogLevel.Error).Length == 0, "no press failed: " + rig.Lines());
        Check(fake.Held, "the checks were held throughout");
        Hide();
        fake.Release();
        Console.WriteLine("PASS people detector: a press for a group or person that is gone and for a group without a picture is refused in a sentence, and the page is redrawn; one that chooses a person the page no longer offers finds the page changed");
    }

    static async Task AChangeOnAnotherDetectorRedrawsThePage()
    {
        using var fake = new Fake(await Spots());
        Source("camera.hall", Porch());
        await using var rig = await Rig.Start("page-two");
        var list = PeopleOf(rig);
        var ana = Named(list, Ana, 1);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await rig.Saved("hall", rig.Definition("Hall people", "Hall", Arguments("camera.hall", ("LearnNewLooks", false)), Faking("Hall")));
        await Until(() => CardOf(Page(rig, "hall"), "people", (string)ana.Id) != null && CardOf(Page(rig), "people", (string)ana.Id) != null, "both draw Ana");
        Check(await Press(rig, "people", (string)ana.Id, "save", new { first_name = First(Mia), last_name = Names(Mia).Last }, "hall") == "done", "a rename on the hall's page");
        await Until(() => Title(CardOf(Page(rig), "people", (string)ana.Id)!) == Mia && OptionsOf(Page(rig)).Select(o => (string)o["title"]!).SequenceEqual([Mia]),
            "the porch's page shows the new name, in its set too: " + Page(rig)?.ToJsonString(), 2);
        // A change by another list on the store too.
        var ivan = Named(list, Ivan, 2);
        string[] Offered(string definition) => [.. OptionsOf(Page(rig, definition)).Select(o => (string)o["title"]!)];
        await Until(() => CardsOf(Page(rig), "people").Select(Title).SequenceEqual([Ivan, Mia]) && CardsOf(Page(rig, "hall"), "people").Select(Title).SequenceEqual([Ivan, Mia])
            && Offered("porch").SequenceEqual([Ivan, Mia]) && Offered("hall").SequenceEqual([Ivan, Mia]),
            "both pages list Ivan, and offer him: " + Page(rig)?.ToJsonString(), 2);
        list.Delete((string)ivan.Id);
        await Until(() => Offered("porch").SequenceEqual([Mia]) && Offered("hall").SequenceEqual([Mia]), "both pages' sets lose Ivan: " + Page(rig)?.ToJsonString(), 2);
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: a change another detector makes to the people list is on this one's page and in its set of people within two seconds");
    }

    // ---- The Recognised sensor, with the fake recogniser and the test's clock ----

    /// <summary>The faces of the group photo a check embeds: the four largest usable ones, the largest first.</summary>
    static int[] Embeddable(Spot[] spots) => [.. Enumerable.Range(0, spots.Length).Where(i => spots[i].Usable).Take(4)];

    /// <summary>The face at <paramref name="spot"/> fits too badly to be compared: it is embedded, and neither matched nor noted.</summary>
    static void Uncompared(Fake fake, int spot) => fake.Embeds(spot, PeopleTests.Unit(1000 + spot), fit: 9);

    // The faces of the people of these tests: each recognised beyond doubt as the person of its seed (Named).
    static float[] FaceOf(int seed) => PeopleTests.At(PeopleTests.Unit(seed), 0.8, 100 + seed);

    /// <summary>
    /// The Recognised sensor as a snapshot shows it, in a line: its state, then its attributes people, first_names,
    /// unknown_faces and last_recognised, each after " | ", a list in brackets and none as "-"; "unavailable: " first while
    /// it is unavailable. last_recognised_at is read on its own (<see cref="VisitBegan"/>).
    /// </summary>
    static string Sensor(Pushed? pushed)
    {
        if (pushed == null) return "none";
        var attributes = pushed.Attributes;
        string Listed(string key) => attributes[key] is JsonArray items ? "[" + string.Join(", ", items.Select(item => (string)item!)) + "]" : "-";
        return $"{(pushed.Available ? "" : "unavailable: ")}{pushed.Text ?? "-"} | {Listed("people")} | {Listed("first_names")} | {attributes["unknown_faces"]?.ToJsonString() ?? "-"} | {(string?)attributes["last_recognised"] ?? "-"}";
    }

    /// <summary>The Recognised sensor of <paramref name="definition"/> as last published (<see cref="Sensor(Pushed?)"/>).</summary>
    static string Sensor(Rig rig, string definition = "porch") => Sensor(rig.Latest("recognised", definition));

    /// <summary>A Recognised snapshot's last_recognised_at; null when it has none.</summary>
    static DateTimeOffset? VisitBegan(Pushed? pushed) => pushed?.Attributes["last_recognised_at"] is JsonValue value && value.GetValueKind() == JsonValueKind.String
        ? DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture) : null;

    // Who is in frame, as Recognised says it: Nobody with faces that are not compared; a person's full name from the check
    // that gives them a face, and two in alphabetical order; how many faces went to no one; Unknown with only those; and
    // a hold of Stay on for seconds on the test's clock. An arrival is one publish, and one write of the visit.
    static async Task TheRecognisedSensorSaysWhoIsInFrame()
    {
        var spots = await Spots();
        var four = Embeddable(spots);
        Check(four.Length == 4 && spots.Count(spot => spot.Usable) > 4 && spots.Any(spot => !spot.Usable),
            "the group photo has more than four usable faces, and some that are not: " + string.Join(" | ", spots.Select(s => s.ToString())));
        using var fake = new Fake(spots);
        using var clock = new Clock();
        foreach (var spot in four) Uncompared(fake, spot);
        Show();
        await using var rig = await Rig.Start("recognised-sensor");
        rig.SendsEveryPublishOf("recognised");
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        Named(list, Ivan, 2);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: [("LearnNewLooks", false), ("StayOnFor", 10)]), Faking("Porch")));

        // Eight faces and none compared: the four embedded fit too badly, the others are too small, turned away or beyond the four.
        await Until(() => rig.Latest("faces")?.Number == 8 && Sensor(rig) == "Nobody | [] | [] | 0 | -", "eight faces, and nobody: " + rig.Shown("recognised"), 10);
        await Checks(clock, 5);
        Check(rig.Of("recognised").All(p => p.Text == "Nobody") && Sensor(rig) == "Nobody | [] | [] | 0 | -", "Recognised is Nobody throughout: " + rig.Shown("recognised"));

        // Ana arrives, on the second face: one publish in 20 checks, and one write of her visit.
        var writes = 0;
        using var written = rig.DeviceData().Changed(() => { Interlocked.Increment(ref writes); return Task.CompletedTask; });
        var from = rig.Mark;
        var anaArrives = clock.Now;
        fake.Embeds(four[1], FaceOf(1));
        await Until(() => Sensor(rig) == $"{Ana} | [{Ana}] | [{First(Ana)}] | 0 | {Ana}", "Recognised names Ana: " + rig.Shown("recognised", from));
        var anaBegan = VisitBegan(rig.Latest("recognised"));
        Check(anaBegan >= anaArrives && anaBegan < clock.Now, $"her visit began at the check that gave her a face: {anaBegan:O}, from {anaArrives:O}");
        await Checks(clock, 20);
        Check(rig.Of("recognised", from).Length == 1, "Ana in frame for 20 checks is one publish: " + rig.Shown("recognised", from));

        // Ivan arrives a second later, on the largest face: the two in alphabetical order, and he is the last recognised.
        clock.Advance(TimeSpan.FromSeconds(1));
        from = rig.Mark;
        var ivanArrives = clock.Now;
        fake.Embeds(four[0], FaceOf(2));
        var both = $"{Ana}, {Ivan} | [{Ana}, {Ivan}] | [{First(Ana)}, {First(Ivan)}]";
        await Until(() => Sensor(rig) == $"{both} | 0 | {Ivan}", "Recognised names both: " + rig.Shown("recognised", from));
        var ivanBegan = VisitBegan(rig.Latest("recognised"));
        Check(ivanBegan >= ivanArrives && ivanBegan < clock.Now, $"his visit began at the check that gave him a face: {ivanBegan:O}, from {ivanArrives:O}");
        await Checks(clock, 10);
        Check(rig.Of("recognised", from).Length == 1, "his arrival is one publish: " + rig.Shown("recognised", from));
        await Until(() => Volatile.Read(ref writes) >= 2, "the two visits are written");
        await Checks(clock, 10);
        Check(Volatile.Read(ref writes) == 2, $"each visit is written once, and nothing else is: {Volatile.Read(ref writes)} writes");

        // A second face of Ana and a maybe of Ivan go to no one: two unknown faces. Without the maybe, the count holds at
        // the most one check had within the hold.
        fake.Embeds(four[2], PeopleTests.At(PeopleTests.Unit(1), 0.7, 13));
        fake.Embeds(four[3], PeopleTests.At(PeopleTests.Unit(2), 0.45, 14));
        await Until(() => Sensor(rig) == $"{both} | 2 | {Ivan}", "a second face of Ana and a maybe of Ivan are two unknown faces: " + rig.Shown("recognised", from));
        Uncompared(fake, four[3]);
        await Checks(clock, 10);
        Check(Sensor(rig) == $"{both} | 2 | {Ivan}", "with one unknown face, unknown_faces holds at 2: " + Sensor(rig));

        // Everyone goes: the names and the count hold through the checks for Stay on for, 10 s on the clock, and go after it.
        from = rig.Mark;
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");
        await Checks(clock, 20);
        Check(rig.Of("recognised", from).Length == 0, "Recognised holds through 20 checks without them: " + rig.Shown("recognised", from));
        clock.Advance(TimeSpan.FromSeconds(9.9));
        await Checks(clock, 5);
        Check(rig.Of("recognised", from).Length == 0, "and 9.9 s on: " + rig.Shown("recognised", from));
        clock.Advance(TimeSpan.FromSeconds(0.2));
        await Until(() => Sensor(rig) == $"Nobody | [] | [] | 0 | {Ivan}", "Recognised is Nobody 10 s on, and keeps Ivan as the last recognised: " + rig.Shown("recognised", from));
        Check(rig.Of("recognised", from).Length == 1 && VisitBegan(rig.Latest("recognised")) == ivanBegan, "in one publish, with the time his visit began: " + rig.Shown("recognised", from));

        // Only a face that goes to no one: Unknown.
        foreach (var spot in four.Take(3)) Uncompared(fake, spot);
        fake.Embeds(four[3], PeopleTests.Unit(1000 + four[3]));
        Show();
        await Until(() => Sensor(rig) == $"Unknown | [] | [] | 1 | {Ivan}", "a stranger alone is Unknown: " + rig.Shown("recognised", from));
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: Recognised is Nobody with faces not compared, names the people given a face in alphabetical order, counts the faces given to no one, is Unknown with those alone, holds for Stay on for, and publishes and writes once an arrival");
    }

    // The face model not open (its first open failed, and the next is due ten minutes on the clock): Recognised is
    // unavailable, and never Unknown, while the faces are counted; it is available once the model opens.
    static async Task TheRecognisedSensorIsUnavailableUntilTheModelOpens()
    {
        using var fake = new Fake(await Spots());
        using var clock = new Clock();
        fake.FailsToOpen(1);
        Show();
        await using var rig = await Rig.Start("recognised-closed");
        rig.SendsEveryPublishOf("recognised");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => rig.Latest("faces")?.Number == 8 && fake.Attempts("Porch") == 1, "the faces are counted, and the open failed: " + rig.Lines(), 10);
        await Checks(clock, 20);
        Check(fake.Opened("Porch") == 0 && fake.Embedded().Length == 0, "the model is not open, and no face was compared");
        Check(rig.Of("recognised") is { Length: > 0 } closed && closed.All(p => !p.Available && p.Text != "Unknown"), "Recognised is unavailable, never Unknown: " + rig.Shown("recognised"));

        var from = rig.Mark;
        clock.Advance(TimeSpan.FromMinutes(10));
        await Until(() => fake.Opened("Porch") == 1, "the model opens at the next try: " + rig.Lines());
        await Until(() => Sensor(rig) == "Unknown | [] | [] | 4 | -", "then Recognised is available, and the four strangers unknown: " + rig.Shown("recognised", from));
        Check(rig.Of("recognised", from).All(p => p.Available && p.Text == "Unknown"), "it is never Unknown before it is available: " + rig.Shown("recognised", from));
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: Recognised is unavailable while the face model is not open, never Unknown, and available once it opens");
    }

    // A camera that stalls after Ana was recognised: the checks fail, each after a second without a frame, and Recognised
    // keeps her through them until Stay on for has passed on the clock.
    static async Task AFailedCheckClearsNothingEarly()
    {
        const string declared = "static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);";
        var source = Committed();
        Check(source.Split(declared).Length == 2, "PeopleDetector.cs declares FrameTimeout once as 10 s");
        var spots = await Spots();
        var four = Embeddable(spots);
        using var fake = new Fake(spots);
        using var clock = new Clock();
        foreach (var spot in four.Skip(1)) Uncompared(fake, spot);
        fake.Embeds(four[0], FaceOf(1));
        // The fake hangs at its 60th frame, three seconds in.
        CameraFeeds.StallLimit = TimeSpan.FromSeconds(60);
        Source("camera.porch", Porch("stall=60,"));
        Show();
        await using var rig = await Rig.Start("recognised-stalled", people: source.Replace(declared, "static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(1);"));
        rig.SendsEveryPublishOf("recognised");
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: [("LearnNewLooks", false), ("StayOnFor", 10)]), Faking("Porch")));
        var named = $"{Ana} | [{Ana}] | [{First(Ana)}] | 0 | {Ana}";
        await Until(() => Sensor(rig) == named, "Recognised names Ana before the camera stalls: " + rig.Shown("recognised"), 10);

        const string failed = "Porch people: the timer callback every 100 ms failed: ";
        await Until(() => rig.Home.Logs(PyLogLevel.Error).Any(l => l.StartsWith(failed, StringComparison.Ordinal)), "the checks fail: " + rig.Lines(), 10);
        var from = rig.Mark;
        await Checks(clock, 3);
        Check(rig.Of("recognised", from).Length == 0 && Sensor(rig) == named, "three failed checks leave Ana: " + rig.Shown("recognised", from));
        clock.Advance(TimeSpan.FromSeconds(10));
        await Until(() => Sensor(rig) == $"Nobody | [] | [] | 0 | {Ana}", "a failed check 10 s on lets her go: " + rig.Shown("recognised", from));
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: failed checks keep whom Recognised names until Stay on for has passed");
    }

    // A rename shows in the state, here and on another detector, with no face in frame; a person deleted or merged away,
    // here or on another detector, goes from the state and from last_recognised, and a merged one's visit does not become
    // the one they were merged into's.
    static async Task TheRecognisedSensorFollowsRenamesMergesAndDeletes()
    {
        var spots = await Spots();
        var four = Embeddable(spots);
        using var fake = new Fake(spots);
        using var clock = new Clock();
        foreach (var spot in four) Uncompared(fake, spot);
        Show();
        await using var rig = await Rig.Start("recognised-changes");
        var list = PeopleOf(rig);
        var ana = Named(list, Ana, 1);
        var ivan = Named(list, Ivan, 2);
        var eva = Named(list, Eva, 3);
        var mia = Named(list, Mia, 4);
        // Stay on for is a minute, and the clock moves only a millisecond a check: who arrives stays in frame here.
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: [("LearnNewLooks", false), ("StayOnFor", 60)]), Faking("Porch")));
        await Until(() => Sensor(rig) == "Nobody | [] | [] | 0 | -", "Recognised is Nobody: " + rig.Shown("recognised"), 10);
        string[] arrivals = [Ana, Ivan, Eva];
        for (var i = 0; i < arrivals.Length; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            fake.Embeds(four[i], FaceOf(i + 1));
            var name = arrivals[i];
            await Until(() => rig.Latest("recognised")?.Attributes["last_recognised"]?.GetValue<string>() == name, $"{name} arrives: " + Sensor(rig));
        }
        Check(Sensor(rig) == $"{Ana}, {Eva}, {Ivan} | [{Ana}, {Eva}, {Ivan}] | [{First(Ana)}, {First(Eva)}, {First(Ivan)}] | 0 | {Eva}", "the three in frame, Eva last: " + Sensor(rig));
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");

        // Renamed on another detector, and here: the state follows, in alphabetical order again.
        list.Rename((string)ivan.Id, First(Ivo), Names(Ivo).Last);
        await Until(() => Sensor(rig) == $"{Ana}, {Eva}, {Ivo} | [{Ana}, {Eva}, {Ivo}] | [{First(Ana)}, {First(Eva)}, {First(Ivo)}] | 0 | {Eva}", "Ivan renamed Ivo on another detector: " + Sensor(rig), 2);
        const string Zora = "Zora Horvat";
        Check(await Press(rig, "people", (string)ana.Id, "save", new { first_name = First(Zora), last_name = Names(Zora).Last }) == "done", "a rename of Ana here");
        await Until(() => Sensor(rig) == $"{Eva}, {Ivo}, {Zora} | [{Eva}, {Ivo}, {Zora}] | [{First(Eva)}, {First(Ivo)}, {First(Zora)}] | 0 | {Eva}", "Ana renamed Zora here: " + Sensor(rig), 2);

        // Eva, the last recognised, merged here into Mia, who is not in frame: Eva goes, and her visit is not Mia's.
        Check(await Press(rig, "people", (string)eva.Id, "merge", new { into = Picked((string)mia.Id) }) == "done", "Eva merged into Mia here");
        await Until(() => Sensor(rig) == $"{Ivo}, {Zora} | [{Ivo}, {Zora}] | [{First(Ivo)}, {First(Zora)}] | 0 | -", "Eva goes, and no one is the last recognised: " + Sensor(rig), 2);
        Check(VisitBegan(rig.Latest("recognised")) == null, "nor has a time: " + rig.Latest("recognised"));

        // Eva's face is Mia's now: Mia arrives, and is the last recognised; deleted on another detector, she goes.
        Show();
        await Until(() => Sensor(rig) == $"{Ivo}, {Mia}, {Zora} | [{Ivo}, {Mia}, {Zora}] | [{First(Ivo)}, {First(Mia)}, {First(Zora)}] | 0 | {Mia}", "Mia arrives with Eva's face: " + Sensor(rig));
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");
        list.Delete((string)mia.Id);
        await Until(() => Sensor(rig) == $"{Ivo}, {Zora} | [{Ivo}, {Zora}] | [{First(Ivo)}, {First(Zora)}] | 0 | -", "Mia deleted on another detector goes: " + Sensor(rig), 2);

        // Ivo merged into Zora on another detector, then Zora deleted here.
        list.Merge((string)ivan.Id, (string)ana.Id);
        await Until(() => Sensor(rig) == $"{Zora} | [{Zora}] | [{First(Zora)}] | 0 | -", "Ivo merged away on another detector goes: " + Sensor(rig), 2);
        Check(await Press(rig, "people", (string)ana.Id, "delete", new { }) == "done", "Zora deleted here");
        await Until(() => Sensor(rig) == "Nobody | [] | [] | 0 | -", "Zora goes, and Recognised is Nobody: " + Sensor(rig), 2);
        NoNameIsLogged(rig);
        Check(!rig.Home.Logs().Any(l => l.Contains(Zora, StringComparison.OrdinalIgnoreCase)), "no line names Zora: " + rig.Lines());
        Console.WriteLine("PASS people detector: Recognised follows a rename here and on another detector, and drops a person deleted or merged away here or on another detector from its state and its last recognised");
    }

    // last_recognised and its time come back after the definition restarts, from the detector's own data; a stored person
    // who is no one's any more comes back as none.
    static async Task TheLastRecognisedOutlivesARestart()
    {
        var spots = await Spots();
        var four = Embeddable(spots);
        using var fake = new Fake(spots);
        using var clock = new Clock();
        foreach (var spot in four.Skip(1)) Uncompared(fake, spot);
        fake.Embeds(four[0], FaceOf(1));
        Show();
        await using var rig = await Rig.Start("recognised-restart");
        var list = PeopleOf(rig);
        var ana = Named(list, Ana, 1);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => Sensor(rig) == $"{Ana} | [{Ana}] | [{First(Ana)}] | 0 | {Ana}", "Recognised names Ana: " + rig.Shown("recognised"), 10);
        var began = VisitBegan(rig.Latest("recognised"));
        Check(began != null, "with the time her visit began");
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");

        await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = false });
        var from = rig.Mark;
        await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = true });
        await Until(() => Sensor(rig) == $"Nobody | [] | [] | 0 | {Ana}" && rig.Of("recognised", from).Length > 0, "restarted, Recognised is Nobody, and Ana the last recognised: " + rig.Shown("recognised", from), 10);
        Check(VisitBegan(rig.Latest("recognised")) == began, $"at the time her visit began: {VisitBegan(rig.Latest("recognised")):O}, not {began:O}");
        // The turning off sends it unavailable with nothing, maybe after the mark.
        Check(rig.Of("recognised", from).Where(p => p.Value != null).All(p => (string?)p.Attributes["last_recognised"] == Ana), "from the start: " + rig.Shown("recognised", from));

        await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = false });
        list.Delete((string)ana.Id);
        from = rig.Mark;
        await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = true });
        await Until(() => Sensor(rig) == "Nobody | [] | [] | 0 | -" && rig.Of("recognised", from).Any(p => p.Available), "restarted after she was deleted, no one is the last recognised: " + rig.Shown("recognised", from), 10);
        Check(VisitBegan(rig.Latest("recognised")) == null, "nor has a time: " + rig.Latest("recognised"));
        NoNameIsLogged(rig);
        Console.WriteLine("PASS people detector: last_recognised and the time the visit began come back after a restart, and a person deleted meanwhile comes back as none");
    }

    // Three people of 100-character full names in frame: the state is cut at a whole name, at most 255 characters with
    // ", …"; the people attribute has all three.
    static async Task TheRecognisedStateIsCutAtAWholeName()
    {
        var spots = await Spots();
        var four = Embeddable(spots);
        using var fake = new Fake(spots);
        using var clock = new Clock();
        Uncompared(fake, four[3]);
        Show();
        await using var rig = await Rig.Start("recognised-long");
        var list = PeopleOf(rig);
        string[] firsts = [.. "ACE".Select(letter => letter + new string('a', 49))];
        string[] lasts = [.. "BDF".Select(letter => letter + new string('b', 48))];
        for (var i = 0; i < 3; i++)
        {
            list.Add(firsts[i], lasts[i], PeopleTests.Samples(PeopleTests.NewSample(PeopleTests.Unit(i + 1), PeopleTests.Crop(i + 1), DateTimeOffset.UtcNow, "camera.elsewhere")));
            fake.Embeds(four[i], FaceOf(i + 1));
        }
        string[] full = [.. firsts.Zip(lasts, (first, last) => $"{first} {last}")];
        Check(full.All(name => name.Length == 100), "each full name has 100 characters");
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
        await Until(() => rig.Latest("recognised")?.Attributes["people"] is JsonArray { Count: 3 }, "the three are in frame: " + Sensor(rig), 10);
        var state = rig.Latest("recognised")!.Text!;
        Check(state == $"{full[0]}, {full[1]}, …" && state.Length <= 255, $"the state is the first two and \", …\", {state.Length} characters: {state}");
        Check(Sensor(rig) == $"{state} | [{string.Join(", ", full)}] | [{string.Join(", ", firsts)}] | 0 | {full[0]}", "the attributes name all three: " + Sensor(rig));
        Console.WriteLine($"PASS people detector: with three people of 100-character names in frame, Recognised is cut at a whole name to {state.Length} characters, and its people name all three");
    }

    // ---- Recognition pictures ----

    /// <summary>A Recognised snapshot's <c>pictures</c>: a path, or null, for each person of <c>people</c>; none when it has no such list.</summary>
    static string?[] Pictures(Pushed? pushed) => pushed?.Attributes["pictures"] is JsonArray items ? [.. items.Select(item => (string?)item)] : [];

    /// <summary>A Recognised snapshot's <c>last_recognised_picture</c>; null when it has none.</summary>
    static string? LastPicture(Pushed? pushed) => (string?)pushed?.Attributes["last_recognised_picture"];

    /// <summary>A Recognised snapshot's <c>unknown_picture</c>; null when it has none.</summary>
    static string? UnknownPicture(Pushed? pushed) => (string?)pushed?.Attributes["unknown_picture"];

    /// <summary>Whether a Recognised snapshot has the three picture attributes, and they are empty: <c>pictures</c> an empty list, the two others null.</summary>
    static bool NoPictures(Pushed? pushed) => pushed != null && pushed.Attributes["pictures"] is JsonArray { Count: 0 }
        && pushed.Attributes.ContainsKey("last_recognised_picture") && LastPicture(pushed) == null
        && pushed.Attributes.ContainsKey("unknown_picture") && UnknownPicture(pushed) == null;

    /// <summary>The three picture attributes of a Recognised snapshot, in a line, for a failure's message.</summary>
    static string PicturesOf(Pushed? pushed) =>
        $"[{string.Join(", ", Pictures(pushed).Select(path => path ?? "null"))}] | {LastPicture(pushed) ?? "null"} | {UnknownPicture(pushed) ?? "null"}";

    // A picture of the device door of porch: served from the media folder's entry local, and named by the UTC time of its
    // check and 16 random hex digits, with nothing of a person.
    static readonly System.Text.RegularExpressions.Regex PicturePath = new(@"^/media/local/hass_sharp/porch/door/(?<at>\d{8}T\d{6})-[0-9a-f]{16}\.jpg\z");

    /// <summary>
    /// Checks that <paramref name="path"/> is a picture of porch's detector (<see cref="PicturePath"/>), named by a time
    /// within a minute of <paramref name="near"/>, and on disk the JPEG of a face's crop, 160 pixels on its long side.
    /// </summary>
    static void IsPicture(Rig rig, string? path, DateTimeOffset near, string what)
    {
        var match = path == null ? null : PicturePath.Match(path);
        Check(match is { Success: true }, $"{what} has a picture named by its time and random digits: {path ?? "none"}");
        var at = DateTime.ParseExact(match!.Groups["at"].Value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        Check(Math.Abs((at - near.UtcDateTime).TotalSeconds) < 60, $"{what}'s picture is named by the UTC time of its check: {path}, near {near:O}");
        var file = rig.OnDisk(path!);
        Check(File.Exists(file), $"{what}'s picture is on disk: {file}");
        using var image = SharpImage.Load(File.ReadAllBytes(file));
        Check(Math.Max(image.Width, image.Height) == 160, $"{what}'s picture is a face's crop, 160 pixels on its long side: {image.Width} × {image.Height}");
    }

    /// <summary>The detector's own index of its pictures, in its data: each picture's name and the person it shows, by id (none for an unknown face).</summary>
    static (string Name, string? PersonId)[] Index(Rig rig) =>
        rig.DeviceData().Get<JsonArray>("pictures") is { } index ? [.. index.Select(entry => ((string)entry!["name"]!, (string?)entry["personId"]))] : [];

    // Every picture a Recognised named was on disk when it was pushed, and nothing of a face or a name is logged.
    static void NothingMissingAndNoNameLogged(Rig rig)
    {
        Check(rig.Missing().Length == 0, "every picture Recognised named was on disk when it was pushed: " + string.Join(" | ", rig.Missing()));
        NoNameIsLogged(rig);
    }

    // Keep recognition pictures off, its default: the pictures kept before are cleared at the start, but not a file of the
    // node's that is no picture's, none is written for an arrival or an unknown face, and Recognised's three picture
    // attributes are there and empty. Without a media folder there
    // is nothing to clear, and no line says anything of it.
    static async Task PicturesOffKeepNothing()
    {
        var spots = await Spots();
        var four = Embeddable(spots);
        using var fake = new Fake(spots);
        using var clock = new Clock();
        foreach (var spot in four.Skip(1)) Uncompared(fake, spot);
        fake.Embeds(four[0], FaceOf(1));
        Show();
        await using (var rig = await Rig.Start("pictures-off", media: true))
        {
            var list = PeopleOf(rig);
            Named(list, Ana, 1);
            // What the detector kept while the setting was on, and a file a class deriving it keeps of its own.
            Directory.CreateDirectory(rig.MediaOf());
            File.WriteAllBytes(Path.Combine(rig.MediaOf(), "20261123T080000-0123456789abcdef.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);
            File.WriteAllText(Path.Combine(rig.MediaOf(), "own.txt"), "not a picture");
            await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
            await Until(() => Sensor(rig) == $"{Ana} | [{Ana}] | [{First(Ana)}] | 0 | {Ana}", () => "Recognised names Ana: " + rig.Shown("recognised"), 10);
            fake.Embeds(four[3], PeopleTests.Unit(1000 + four[3]));
            await Until(() => Sensor(rig) == $"{Ana} | [{Ana}] | [{First(Ana)}] | 1 | {Ana}", () => "and a stranger: " + Sensor(rig));
            await Checks(clock, 5);
            var shown = rig.Of("recognised").Where(p => p.Available).ToArray();
            Check(shown.Length > 0 && shown.All(NoPictures), "Recognised's pictures is an empty list, and its two others null: " + string.Join(" | ", shown.Select(PicturesOf)));
            Check(rig.MediaFiles().SequenceEqual(["own.txt"]), "the picture kept before is cleared, none is written, and the derived class's own file is left: " + string.Join(", ", rig.MediaFiles()));
            NothingMissingAndNoNameLogged(rig);
        }

        await using (var rig = await Rig.Start("pictures-off-no-folder"))
        {
            var list = PeopleOf(rig);
            Named(list, Ana, 1);
            await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: ("LearnNewLooks", false)), Faking("Porch")));
            await Until(() => Sensor(rig) == $"{Ana} | [{Ana}] | [{First(Ana)}] | 1 | {Ana}", () => "Recognised names Ana, and a stranger: " + rig.Shown("recognised"), 10);
            Check(NoPictures(rig.Latest("recognised")), "without a media folder, the same: " + PicturesOf(rig.Latest("recognised")));
            Check(!rig.Home.Logs().Any(l => l.Contains("picture", StringComparison.OrdinalIgnoreCase) || l.Contains("media", StringComparison.OrdinalIgnoreCase)),
                "no line says anything of pictures or the media folder: " + rig.Lines());
            NoNameIsLogged(rig);
        }
        Console.WriteLine("PASS people detector: with Keep recognition pictures off, the pictures kept before are cleared, none is written, and Recognised's picture attributes are empty; without a media folder, silently");
    }

    // Keep recognition pictures on: Ana's arrival keeps the crop of her face, on disk when the state that names it is
    // pushed, named by the time and random digits; the checks of her visit after keep nothing more, and Stay on for holds
    // her picture with her. A stranger's arrival (unknown_faces rising from 0) has a picture of its own while unknown faces
    // are in frame; once they are gone, a stranger who comes again is a new arrival with a new picture.
    static async Task AnArrivalsPictureIsOnDiskWhenRecognisedNamesIt()
    {
        var spots = await Spots();
        var four = Embeddable(spots);
        using var fake = new Fake(spots);
        using var clock = new Clock();
        foreach (var spot in four) Uncompared(fake, spot);
        Show();
        await using var rig = await Rig.Start("pictures-arrival", media: true);
        rig.SendsEveryPublishOf("recognised");
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: [("LearnNewLooks", false), ("StayOnFor", 10), ("KeepPictures", true)]), Faking("Porch")));
        await Until(() => Sensor(rig) == "Nobody | [] | [] | 0 | -", () => "Recognised is Nobody: " + rig.Shown("recognised"), 10);
        Check(NoPictures(rig.Latest("recognised")), "with nobody in frame, pictures is an empty list and the two others null: " + PicturesOf(rig.Latest("recognised")));

        // Ana arrives: one publish, with her picture as pictures' one and as the last recognised's.
        var from = rig.Mark;
        var arrives = clock.Now;
        fake.Embeds(four[1], FaceOf(1));
        await Until(() => Sensor(rig) == $"{Ana} | [{Ana}] | [{First(Ana)}] | 0 | {Ana}", () => "Recognised names Ana: " + rig.Shown("recognised", from));
        var arrival = rig.Latest("recognised");
        var picture = LastPicture(arrival);
        IsPicture(rig, picture, arrives, "Ana's arrival");
        Check(Pictures(arrival).SequenceEqual([picture]) && UnknownPicture(arrival) == null, "pictures has her picture, and unknown_picture none: " + PicturesOf(arrival));
        Check(rig.Of("recognised", from).Length == 1, "her arrival is one publish, with its picture: " + rig.Shown("recognised", from));
        Check(rig.MediaFiles().SequenceEqual([Path.GetFileName(picture!)]), "the media folder has her picture alone: " + string.Join(", ", rig.MediaFiles()));
        var bytes = File.ReadAllBytes(rig.OnDisk(picture!));
        await Checks(clock, 20);
        Check(rig.Of("recognised", from).Length == 1 && rig.MediaFiles().Length == 1 && File.ReadAllBytes(rig.OnDisk(picture!)).SequenceEqual(bytes),
            "20 checks more of her visit publish nothing and keep no other picture: " + rig.Shown("recognised", from) + " | " + string.Join(", ", rig.MediaFiles()));

        // A stranger beside her: unknown_faces rises from 0, with the stranger's picture, and its checks after keep nothing more.
        from = rig.Mark;
        var strangerArrives = clock.Now;
        fake.Embeds(four[3], PeopleTests.Unit(1000 + four[3]));
        await Until(() => Sensor(rig) == $"{Ana} | [{Ana}] | [{First(Ana)}] | 1 | {Ana}", () => "a stranger beside her: " + rig.Shown("recognised", from));
        var stranger = UnknownPicture(rig.Latest("recognised"));
        IsPicture(rig, stranger, strangerArrives, "the stranger's arrival");
        Check(stranger != picture && Pictures(rig.Latest("recognised")).SequenceEqual([picture]) && LastPicture(rig.Latest("recognised")) == picture,
            "Ana keeps hers: " + PicturesOf(rig.Latest("recognised")));
        await Checks(clock, 10);
        Check(rig.Of("recognised", from).Length == 1 && rig.MediaFiles().Length == 2, "the stranger's checks after keep nothing more: " + rig.Shown("recognised", from) + " | " + string.Join(", ", rig.MediaFiles()));

        // Both out of frame: held with their pictures for Stay on for; after it, Ana's picture stays her arrival's as the
        // last recognised's, and unknown_picture goes with the unknown faces.
        from = rig.Mark;
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");
        await Checks(clock, 5);
        Check(rig.Of("recognised", from).Length == 0, "Recognised holds, with its pictures: " + rig.Shown("recognised", from));
        clock.Advance(TimeSpan.FromSeconds(10.1));
        await Until(() => Sensor(rig) == $"Nobody | [] | [] | 0 | {Ana}", () => "both go: " + rig.Shown("recognised", from));
        var gone = rig.Latest("recognised");
        Check(Pictures(gone).Length == 0 && LastPicture(gone) == picture && UnknownPicture(gone) == null, "pictures empties, the last recognised keeps hers, and unknown_picture goes: " + PicturesOf(gone));

        // A stranger again, alone: a new unknown arrival, with a new picture.
        from = rig.Mark;
        foreach (var spot in four.Take(3)) Uncompared(fake, spot);
        var again = clock.Now;
        Show();
        await Until(() => Sensor(rig) == $"Unknown | [] | [] | 1 | {Ana}", () => "a stranger alone: " + rig.Shown("recognised", from));
        var second = UnknownPicture(rig.Latest("recognised"));
        IsPicture(rig, second, again, "the stranger's second arrival");
        Check(second != stranger && rig.MediaFiles().Length == 3, "a picture of its own: " + PicturesOf(rig.Latest("recognised")) + " | " + string.Join(", ", rig.MediaFiles()));
        NothingMissingAndNoNameLogged(rig);
        Console.WriteLine("PASS people detector: an arrival's picture is on disk when Recognised names it, one a visit, held with them; an unknown arrival has its own while unknown faces are in frame");
    }

    // Ana and Ivan arrive in one check, Ivan's face the larger: pictures has each one's in people's order, Ivan's is the
    // last recognised's, and the detector's index says which picture shows whom.
    static async Task TwoArrivalsOfOneCheckHaveTwoPictures()
    {
        var spots = await Spots();
        var four = Embeddable(spots);
        using var fake = new Fake(spots);
        using var clock = new Clock();
        foreach (var spot in four.Skip(2)) Uncompared(fake, spot);
        fake.Embeds(four[0], FaceOf(2));
        fake.Embeds(four[1], FaceOf(1));
        Show();
        await using var rig = await Rig.Start("pictures-two", media: true);
        rig.SendsEveryPublishOf("recognised");
        var list = PeopleOf(rig);
        var ana = Named(list, Ana, 1);
        var ivan = Named(list, Ivan, 2);
        var arrives = clock.Now;
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: [("LearnNewLooks", false), ("KeepPictures", true)]), Faking("Porch")));
        await Until(() => Sensor(rig) == $"{Ana}, {Ivan} | [{Ana}, {Ivan}] | [{First(Ana)}, {First(Ivan)}] | 0 | {Ivan}", () => "Recognised names both: " + rig.Shown("recognised"), 10);
        var both = rig.Of("recognised").First(p => p.Available);
        Check(both.Text == $"{Ana}, {Ivan}", "they arrive in one check, the first Recognised has both: " + rig.Shown("recognised"));
        var pictures = Pictures(both);
        Check(pictures.Length == 2 && pictures[0] != pictures[1] && LastPicture(both) == pictures[1], "two pictures, Ivan's the last recognised's: " + PicturesOf(both));
        IsPicture(rig, pictures[0], arrives, "Ana's arrival");
        IsPicture(rig, pictures[1], arrives, "Ivan's arrival");
        var index = Index(rig);
        Check(index.Length == 2 && index.Single(entry => entry.Name == Path.GetFileName(pictures[0])).PersonId == (string)ana.Id
            && index.Single(entry => entry.Name == Path.GetFileName(pictures[1])).PersonId == (string)ivan.Id,
            "the first shows Ana and the second Ivan: " + string.Join(", ", index.Select(entry => $"{entry.Name}={entry.PersonId}")));
        await Checks(clock, 10);
        Check(rig.MediaFiles().Length == 2, "and nothing more is kept: " + string.Join(", ", rig.MediaFiles()));
        NothingMissingAndNoNameLogged(rig);
        Console.WriteLine("PASS people detector: two arrivals of one check have two pictures, in people's order");
    }

    // 21 people arrive one after the other on one face, each held by Stay on for: 20 pictures are kept, and the first
    // person's goes, from the media folder and from pictures, as the 21st is kept. A day on, the check that finds them
    // older than a day takes every one, the last recognised's too, and the next arrival keeps its own.
    static async Task AtMostTwentyPicturesAreKeptAndNoneOlderThanADay()
    {
        var spots = await Spots();
        var four = Embeddable(spots);
        using var fake = new Fake(spots);
        using var clock = new Clock();
        foreach (var spot in four) Uncompared(fake, spot);
        Show();
        await using var rig = await Rig.Start("pictures-twenty", media: true);
        var list = PeopleOf(rig);
        string[] names = [.. Enumerable.Range(1, 22).Select(i => $"Vid{i:00} Kept")];
        foreach (var (name, i) in names.Select((name, i) => (name, i))) Named(list, name, 11 + i);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: [("LearnNewLooks", false), ("StayOnFor", 600), ("KeepPictures", true)]), Faking("Porch")));
        await Until(() => Sensor(rig) == "Nobody | [] | [] | 0 | -", () => "Recognised is Nobody: " + rig.Shown("recognised"), 10);
        var kept = new List<string>();
        for (var i = 0; i < 21; i++)
        {
            var name = names[i];
            fake.Embeds(four[0], FaceOf(11 + i));
            await Until(() => (string?)rig.Latest("recognised")?.Attributes["last_recognised"] == name, () => $"{name} arrives: " + Sensor(rig));
            kept.Add(LastPicture(rig.Latest("recognised")) ?? throw new Exception($"{name}'s arrival has no picture: " + PicturesOf(rig.Latest("recognised"))));
        }
        var latest = rig.Latest("recognised");
        var shown = Pictures(latest);
        Check(shown.Length == 21 && shown[0] == null && shown.Skip(1).SequenceEqual(kept.Skip(1)),
            "the 21 are held, by name, and the first has no picture left: " + PicturesOf(latest));
        Check(rig.MediaFiles().SequenceEqual(kept.Skip(1).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)) && !File.Exists(rig.OnDisk(kept[0])),
            "20 pictures are kept, the first one's gone: " + string.Join(", ", rig.MediaFiles()));

        // Out of frame, and a day on: they have all gone, and so have their pictures; the next arrival keeps its own.
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");
        clock.Advance(TimeSpan.FromDays(1));
        await Until(() => Sensor(rig) == $"Nobody | [] | [] | 0 | {names[20]}", () => "a day on, they have gone: " + Sensor(rig));
        await Until(() => rig.MediaFiles().Length == 0, () => "with their pictures: " + string.Join(", ", rig.MediaFiles()));
        Check(NoPictures(rig.Latest("recognised")), "Recognised names none, the last recognised's neither: " + PicturesOf(rig.Latest("recognised")));
        var arrives = clock.Now;
        fake.Embeds(four[0], FaceOf(11 + 21));
        Show();
        await Until(() => (string?)rig.Latest("recognised")?.Attributes["last_recognised"] == names[21], () => $"{names[21]} arrives: " + Sensor(rig));
        var last = LastPicture(rig.Latest("recognised"));
        IsPicture(rig, last, arrives, "the arrival a day on");
        Check(Pictures(rig.Latest("recognised")).SequenceEqual([last]) && rig.MediaFiles().SequenceEqual([Path.GetFileName(last!)]) && Index(rig).Length == 1,
            "the pictures older than a day are gone: " + PicturesOf(rig.Latest("recognised")) + " | " + string.Join(", ", rig.MediaFiles()));
        NothingMissingAndNoNameLogged(rig);
        Check(!rig.Home.Logs().Any(l => names.Any(name => l.Contains(name, StringComparison.OrdinalIgnoreCase))), "no line names anyone: " + rig.Lines());
        Console.WriteLine("PASS people detector: 21 arrivals keep 20 pictures, the first one's gone from pictures too, and a day on every one goes, from Recognised too");
    }

    // Four people arrive in one check, Ivan's face the largest, and are held by Stay on for with nobody in frame. Ivan merged
    // into Ana on another detector, Eva deleted here and Mia deleted on another detector each lose their picture, from the
    // media folder and from Recognised (Ivan's, the last recognised's, becomes null); Ana keeps hers. Ana, come back, has a
    // second picture: deleted here, she loses both.
    static async Task APersonDeletedOrMergedAwayLosesTheirPictures()
    {
        var spots = await Spots();
        var four = Embeddable(spots);
        using var fake = new Fake(spots);
        using var clock = new Clock();
        fake.Embeds(four[0], FaceOf(2));
        fake.Embeds(four[1], FaceOf(1));
        fake.Embeds(four[2], FaceOf(3));
        fake.Embeds(four[3], FaceOf(4));
        Show();
        await using var rig = await Rig.Start("pictures-deleted", media: true);
        var list = PeopleOf(rig);
        var ana = Named(list, Ana, 1);
        var ivan = Named(list, Ivan, 2);
        var eva = Named(list, Eva, 3);
        var mia = Named(list, Mia, 4);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: [("LearnNewLooks", false), ("StayOnFor", 60), ("KeepPictures", true)]), Faking("Porch")));
        await Until(() => Sensor(rig) == $"{Ana}, {Eva}, {Ivan}, {Mia} | [{Ana}, {Eva}, {Ivan}, {Mia}] | [{First(Ana)}, {First(Eva)}, {First(Ivan)}, {First(Mia)}] | 0 | {Ivan}",
            () => "the four arrive: " + rig.Shown("recognised"), 10);
        var arrived = Pictures(rig.Latest("recognised"));
        Check(arrived.Length == 4 && arrived.All(path => path != null) && arrived.Distinct().Count() == 4 && LastPicture(rig.Latest("recognised")) == arrived[2],
            "each has a picture, Ivan's the last recognised's: " + PicturesOf(rig.Latest("recognised")));
        var (anas, evas, ivans, mias) = (arrived[0]!, arrived[1]!, arrived[2]!, arrived[3]!);
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");

        list.Merge((string)ivan.Id, (string)ana.Id);
        await Until(() => Sensor(rig) == $"{Ana}, {Eva}, {Mia} | [{Ana}, {Eva}, {Mia}] | [{First(Ana)}, {First(Eva)}, {First(Mia)}] | 0 | -", () => "Ivan merged away on another detector: " + Sensor(rig), 2);
        await Until(() => !File.Exists(rig.OnDisk(ivans)), () => "Ivan's picture goes: " + string.Join(", ", rig.MediaFiles()), 2);
        Check(Pictures(rig.Latest("recognised")).SequenceEqual([anas, evas, mias]) && LastPicture(rig.Latest("recognised")) == null,
            "his picture goes from Recognised, the last recognised's with it, and Ana, whom he was merged into, keeps hers: " + PicturesOf(rig.Latest("recognised")));

        Check(await Press(rig, "people", (string)eva.Id, "delete", new { }) == "done", "Eva deleted here");
        await Until(() => Sensor(rig) == $"{Ana}, {Mia} | [{Ana}, {Mia}] | [{First(Ana)}, {First(Mia)}] | 0 | -", () => "Eva goes: " + Sensor(rig), 2);
        await Until(() => !File.Exists(rig.OnDisk(evas)), () => "Eva's picture goes: " + string.Join(", ", rig.MediaFiles()), 2);
        Check(Pictures(rig.Latest("recognised")).SequenceEqual([anas, mias]), "and from Recognised: " + PicturesOf(rig.Latest("recognised")));

        list.Delete((string)mia.Id);
        await Until(() => Sensor(rig) == $"{Ana} | [{Ana}] | [{First(Ana)}] | 0 | -", () => "Mia deleted on another detector goes: " + Sensor(rig), 2);
        await Until(() => !File.Exists(rig.OnDisk(mias)), () => "Mia's picture goes: " + string.Join(", ", rig.MediaFiles()), 2);
        Check(Pictures(rig.Latest("recognised")).SequenceEqual([anas]) && rig.MediaFiles().SequenceEqual([Path.GetFileName(anas)]),
            "Ana's picture alone is left: " + PicturesOf(rig.Latest("recognised")) + " | " + string.Join(", ", rig.MediaFiles()));

        // Ana leaves and comes back, on her face alone: a second picture, hers as the last recognised's.
        clock.Advance(TimeSpan.FromSeconds(61));
        await Until(() => Sensor(rig) == "Nobody | [] | [] | 0 | -", () => "Ana goes: " + Sensor(rig));
        foreach (var spot in four.Where(spot => spot != four[1])) Uncompared(fake, spot);
        Show();
        await Until(() => Sensor(rig) == $"{Ana} | [{Ana}] | [{First(Ana)}] | 0 | {Ana}", () => "Ana comes back: " + Sensor(rig));
        var back = LastPicture(rig.Latest("recognised"));
        Check(back != null && back != anas && rig.MediaFiles().Length == 2, "with a second picture: " + PicturesOf(rig.Latest("recognised")) + " | " + string.Join(", ", rig.MediaFiles()));
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");
        Check(await Press(rig, "people", (string)ana.Id, "delete", new { }) == "done", "Ana deleted here");
        await Until(() => Sensor(rig) == "Nobody | [] | [] | 0 | -", () => "Ana goes: " + Sensor(rig), 2);
        await Until(() => rig.MediaFiles().Length == 0, () => "both her pictures go: " + string.Join(", ", rig.MediaFiles()), 2);
        Check(NoPictures(rig.Latest("recognised")) && Index(rig).Length == 0, "Recognised names none, and the index has none: " + PicturesOf(rig.Latest("recognised")));
        NothingMissingAndNoNameLogged(rig);
        Console.WriteLine("PASS people detector: a person deleted or merged away, here or on another detector, loses their pictures from the media folder and from Recognised; the one merged into keeps theirs");
    }

    // Review focus 3. Without a media folder, and with one that cannot be written, recognition goes on: each arrival is
    // published with no picture, and the failure is logged once as a warning, with no name, until a save succeeds again.
    static async Task APictureThatCannotBeSavedLeavesRecognitionGoing()
    {
        var spots = await Spots();
        var four = Embeddable(spots);
        using var fake = new Fake(spots);
        using var clock = new Clock();
        const string unsaved = "Porch people: A recognition picture could not be saved";
        await using (var rig = await Rig.Start("pictures-no-folder"))
        {
            foreach (var spot in four) Uncompared(fake, spot);
            Show();
            var list = PeopleOf(rig);
            Named(list, Ana, 1);
            Named(list, Ivan, 2);
            await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: [("LearnNewLooks", false), ("StayOnFor", 60), ("KeepPictures", true)]), Faking("Porch")));
            await Until(() => Sensor(rig) == "Nobody | [] | [] | 0 | -", () => "Recognised is Nobody: " + rig.Shown("recognised"), 10);
            fake.Embeds(four[1], FaceOf(1));
            await Until(() => Sensor(rig) == $"{Ana} | [{Ana}] | [{First(Ana)}] | 0 | {Ana}", () => "Ana arrives: " + Sensor(rig));
            Check(Pictures(rig.Latest("recognised")) is [null] && LastPicture(rig.Latest("recognised")) == null, "with no picture: " + PicturesOf(rig.Latest("recognised")));
            fake.Embeds(four[0], FaceOf(2));
            await Until(() => Sensor(rig) == $"{Ana}, {Ivan} | [{Ana}, {Ivan}] | [{First(Ana)}, {First(Ivan)}] | 0 | {Ivan}", () => "Ivan arrives: " + Sensor(rig));
            Check(Pictures(rig.Latest("recognised")) is [null, null] && LastPicture(rig.Latest("recognised")) == null, "with no picture: " + PicturesOf(rig.Latest("recognised")));
            var warned = rig.Home.Logs(PyLogLevel.Warn).Where(l => l.StartsWith(unsaved, StringComparison.Ordinal)).ToArray();
            Check(warned is [var line] && line.Contains("no media folder", StringComparison.Ordinal), "the failure is a warning once, saying there is no media folder: " + rig.Lines());
            NoNameIsLogged(rig);
        }

        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            Console.WriteLine("SKIP people detector: a media folder that cannot be written (Windows or root writes it all the same)");
            return;
        }
        await using (var rig = await Rig.Start("pictures-read-only", media: true))
        {
            foreach (var spot in four) Uncompared(fake, spot);
            var list = PeopleOf(rig);
            Named(list, Ana, 1);
            Named(list, Ivan, 2);
            Named(list, Mia, 3);
            Named(list, Eva, 4);
            var root = rig.MediaRoot!;
            Directory.CreateDirectory(root);
            void ReadOnly(bool on) => File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute | (on ? 0 : UnixFileMode.UserWrite));
            try
            {
                ReadOnly(true);
                await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: [("LearnNewLooks", false), ("StayOnFor", 60), ("KeepPictures", true)]), Faking("Porch")));
                await Until(() => Sensor(rig) == "Nobody | [] | [] | 0 | -", () => "Recognised is Nobody: " + rig.Shown("recognised"), 10);
                string[] Warned() => [.. rig.Home.Logs(PyLogLevel.Warn).Where(l => l.StartsWith(unsaved, StringComparison.Ordinal))];
                fake.Embeds(four[1], FaceOf(1));
                await Until(() => Sensor(rig) == $"{Ana} | [{Ana}] | [{First(Ana)}] | 0 | {Ana}", () => "Ana arrives: " + Sensor(rig));
                fake.Embeds(four[0], FaceOf(2));
                await Until(() => Sensor(rig) == $"{Ana}, {Ivan} | [{Ana}, {Ivan}] | [{First(Ana)}, {First(Ivan)}] | 0 | {Ivan}", () => "Ivan arrives: " + Sensor(rig));
                Check(Pictures(rig.Latest("recognised")) is [null, null] && LastPicture(rig.Latest("recognised")) == null, "with no picture: " + PicturesOf(rig.Latest("recognised")));
                Check(Warned() is [var denied] && denied.Contains(nameof(UnauthorizedAccessException), StringComparison.Ordinal), "the failure is a warning once: " + rig.Lines());

                // Writable again: Mia's picture is kept. Read-only once more: Eva's is not, and that is a warning again.
                ReadOnly(false);
                fake.Embeds(four[2], FaceOf(3));
                await Until(() => (string?)rig.Latest("recognised")?.Attributes["last_recognised"] == Mia, () => "Mia arrives: " + Sensor(rig));
                var mias = LastPicture(rig.Latest("recognised"));
                Check(mias != null && File.Exists(rig.OnDisk(mias)) && Pictures(rig.Latest("recognised")).Count(path => path != null) == 1, "with her picture: " + PicturesOf(rig.Latest("recognised")));
                ReadOnly(true);
                File.SetUnixFileMode(rig.MediaOf(), UnixFileMode.UserRead | UnixFileMode.UserExecute);
                try
                {
                    fake.Embeds(four[3], FaceOf(4));
                    await Until(() => (string?)rig.Latest("recognised")?.Attributes["last_recognised"] == Eva, () => "Eva arrives: " + Sensor(rig));
                    Check(LastPicture(rig.Latest("recognised")) == null && Pictures(rig.Latest("recognised")).Count(path => path != null) == 1, "with no picture: " + PicturesOf(rig.Latest("recognised")));
                    Check(Warned().Length == 2, "after a save succeeded, the next failure is a warning again: " + rig.Lines());
                }
                finally { File.SetUnixFileMode(rig.MediaOf(), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            }
            finally { ReadOnly(false); }
            NothingMissingAndNoNameLogged(rig);
        }
        Console.WriteLine("PASS people detector: without a media folder, or with one that cannot be written, an arrival is published with no picture and the failure is logged once until a save succeeds");
    }

    // Review focus 5. last_recognised_picture comes back after a restart with last_recognised while its file is there, and
    // is null when it is not: removed by hand, or older than a day at the start, which takes it. A file the detector keeps
    // no record of goes at the start.
    static async Task TheLastPictureOutlivesARestartWhileItsFileIsThere()
    {
        var spots = await Spots();
        var four = Embeddable(spots);
        using var fake = new Fake(spots);
        using var clock = new Clock();
        foreach (var spot in four.Skip(1)) Uncompared(fake, spot);
        fake.Embeds(four[0], FaceOf(1));
        Show();
        await using var rig = await Rig.Start("pictures-restart", media: true);
        var list = PeopleOf(rig);
        Named(list, Ana, 1);
        await rig.Saved("porch", rig.Definition("Porch people", "Porch", Arguments(more: [("LearnNewLooks", false), ("KeepPictures", true)]), Faking("Porch")));
        await Until(() => Sensor(rig) == $"{Ana} | [{Ana}] | [{First(Ana)}] | 0 | {Ana}", () => "Recognised names Ana: " + rig.Shown("recognised"), 10);
        var picture = LastPicture(rig.Latest("recognised"));
        Check(picture != null, "with her picture: " + PicturesOf(rig.Latest("recognised")));
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");

        async Task<Pushed> Restart(Action? meanwhile = null)
        {
            await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = false });
            meanwhile?.Invoke();
            var from = rig.Mark;
            await rig.Ok(rig.Api.EntityRequest, new { operation = "set_enabled", id = "porch", enabled = true });
            await Until(() => rig.Of("recognised", from).Any(p => p.Available) && Sensor(rig) == $"Nobody | [] | [] | 0 | {Ana}", () => "restarted, Ana is the last recognised: " + rig.Shown("recognised", from), 10);
            return rig.Latest("recognised")!;
        }

        var restarted = await Restart();
        Check(LastPicture(restarted) == picture && Pictures(restarted).Length == 0 && File.Exists(rig.OnDisk(picture!)), "her picture comes back with her: " + PicturesOf(restarted));

        // Her picture removed by hand while the detector was off, and a file it has no record of put there.
        const string stray = "20261123T080000-0123456789abcdef.jpg";
        restarted = await Restart(() =>
        {
            File.Delete(rig.OnDisk(picture!));
            File.WriteAllBytes(Path.Combine(rig.MediaOf(), stray), [0xFF, 0xD8, 0xFF, 0xD9]);
        });
        Check(LastPicture(restarted) == null, "her picture removed, last_recognised_picture is null: " + PicturesOf(restarted));
        Check(rig.MediaFiles().Length == 0 && Index(rig).Length == 0, "the file it had no record of goes, and the index forgets the picture removed: " + string.Join(", ", rig.MediaFiles()));

        // She comes back, with a new picture; a day on, the start takes it.
        clock.Advance(TimeSpan.FromSeconds(11));
        var from = rig.Mark;
        Show();
        await Until(() => Sensor(rig) == $"{Ana} | [{Ana}] | [{First(Ana)}] | 0 | {Ana}" && rig.Of("recognised", from).Any(p => p.Text == Ana), () => "Ana comes back: " + rig.Shown("recognised", from), 10);
        var back = LastPicture(rig.Latest("recognised"));
        Check(back != null && back != picture, "with a new picture: " + PicturesOf(rig.Latest("recognised")));
        Hide();
        await Until(() => rig.Latest("faces")?.Number == 0, "the checks see nobody");
        restarted = await Restart(() => clock.Advance(TimeSpan.FromDays(1)));
        Check(LastPicture(restarted) == null && rig.MediaFiles().Length == 0, "a day on, the start takes her picture, and Recognised names none: " + PicturesOf(restarted) + " | " + string.Join(", ", rig.MediaFiles()));
        NothingMissingAndNoNameLogged(rig);
        Console.WriteLine("PASS people detector: last_recognised_picture comes back after a restart while its file is there, and is null when it is not; a stray file and a picture older than a day go at the start");
    }

    // ---- The class's own node ----

    // The People detector's own node, with no code: the class itself runs, with the real recogniser and the SFace model
    // downloaded from the loopback server. Each face of the group photo embeds the same in every check, so an unknown face
    // is shown after three; named on the page, it is recognised from then on and its name is drawn.
    static async Task TheClassNodeDetectsAndRecognises()
    {
        if (VisionTests.LocalModel == null)
        {
            if (Environment.GetEnvironmentVariable("HS_VISION_REQUIRE_LIBRARY") == "1")
                throw new Exception("the SFace model is required with the library (HS_VISION_REQUIRE_LIBRARY=1) and there is none: run native/fetch-model.sh, or set HS_VISION_MODEL");
            Console.WriteLine("SKIP people detector: no SFace model for the class's own node (native/fetch-model.sh, or HS_VISION_MODEL)");
            return;
        }
        Show();
        await using var rig = await Rig.Start("class-node", VisionTests.LocalRecognizerSource);
        var saved = await rig.Saved("porch", Rig.ClassNodeDefinition("Porch people", Arguments(more: ("LearnNewLooks", false))));
        Check(saved["active"]!.GetValue<bool>(), "the definition of the class's node runs: " + saved.ToJsonString() + " | " + rig.Lines());
        Check(rig.Detector().GetType().Name == "PeopleDetector", "the node's device is the class itself: " + rig.Detector().GetType());
        await Until(() => rig.Latest("faces")?.Number == 8 && rig.Latest("people")?.Number == 7 && rig.Latest("face_seen")?.IsOn == true && rig.Latest("person_seen")?.IsOn == true,
            "the group photo publishes 8 faces, 7 people and both seen: " + string.Join(" | ", Keys.Skip(1).Select(k => rig.Shown(k))), 10);
        await Until(() => CardsOf(Page(rig), "unknown").Length > 0, "an unknown face is on the page: " + rig.Lines(), 30);
        Check(rig.Server.Requests().Count(r => r.Path == "/" + VisionTests.ModelName) == 1, "the model is downloaded once: " + string.Join(", ", rig.Server.Requests().Select(r => r.Path)));

        // Four faces are embedded, and the largest and the smallest of them are 0.52 to 0.56 alike as SFace has them: they
        // are two people still, on two cards.
        await Until(() => CardsOf(Page(rig), "unknown").Length == 4, "the four faces embedded are four cards: " + CardsOf(Page(rig), "unknown").Length, 10);
        var unknown = Member(rig.Detector(), "Unknown");
        float[][] Faces(string id) => [.. VisionTests.Items((object)unknown.Samples(id)).Select(sample => (float[])((dynamic)sample).Embedding.Values)];
        var ids = CardsOf(Page(rig), "unknown").Select(card => (string)card["id"]!).ToArray();
        var alike = (from one in ids
                     from other in ids
                     where string.CompareOrdinal(one, other) < 0
                     select (One: one, Other: other, Score: Faces(one).Max(x => Faces(other).Max(y => PeopleTests.Cosine(x, y))))).MaxBy(pair => pair.Score);
        Check(alike.Score >= (double)PeopleTests.DefaultRules.MatchScore, string.Create(CultureInfo.InvariantCulture, $"two of the cards are Match score alike, the look-alikes: {alike.Score:0.000}"));

        // One of the look-alikes named: the other's card stays and counts, and he is not taken for her.
        Check(await Press(rig, "unknown", alike.One, "save", new { person = NewPerson(Ana) }) == "done", "Save of a new person for one look-alike");
        await Until(() => rig.Labels().Contains(First(Ana)), "the camera names the face: " + string.Join(", ", rig.Labels()), 10);
        var seen = (int)unknown.Find(alike.Other).Sightings;
        await Task.Delay(1500);
        Check(rig.Labels().Count(label => label == First(Ana)) == 1, "one face is named: " + string.Join(", ", rig.Labels()));
        var card = CardOf(Page(rig), "unknown", alike.Other);
        Check(CardsOf(Page(rig), "unknown").Length == 3 && card != null && Detail(card) == null, "the other look-alike's card stays, and does not suggest her: " + Page(rig)?.ToJsonString());
        Check((int)unknown.Find(alike.Other).Sightings > seen, "and counts his checks");
        var list = PeopleOf(rig);
        await Until(() => Reread(list, Ana).LastSeen != null, "Ana is seen", 10);
        Check(Reread(list, Ana).LastSeenAt == "camera.porch", "at camera.porch");

        // The other named too: each is recognised by their own name.
        Check(await Press(rig, "unknown", alike.Other, "save", new { person = NewPerson(Ivan) }) == "done", "Save of a new person for the other look-alike");
        await Until(() => rig.Labels().Contains(First(Ivan)) && rig.Labels().Contains(First(Ana)), "the camera names both: " + string.Join(", ", rig.Labels()), 10);
        await Task.Delay(1000);
        Check(rig.Labels().Count(label => label == First(Ana)) == 1 && rig.Labels().Count(label => label == First(Ivan)) == 1, "each once: " + string.Join(", ", rig.Labels()));
        Check(CardsOf(Page(rig), "unknown").Length == 2, "the two strangers' cards are left");
        NoNameIsLogged(rig);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"PASS people detector: the class's own node detects, groups unknown faces with the real model, and recognises two look-alikes {alike.Score:0.00} alike by their own names once each is named"));
    }
}

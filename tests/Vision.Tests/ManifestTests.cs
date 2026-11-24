using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;

/// <summary>
/// What <c>hass_sharp.toml</c> offers, as HassSharp's Add external imports it: <c>Vision.cs</c>, <c>People.cs</c> and
/// <c>PeopleDetector.cs</c> in one library <c>import</c>, then the definition <c>nodes/front_door_people.json</c>, each on
/// a whole manager of its own (<see cref="EntityTestHome"/>) with the entity descriptions, so that <c>CameraEntity</c>
/// exists. No native library is needed: nothing here starts a definition.
/// </summary>
static class ManifestTests
{
    const string DetectFacesKind = "library:vision:global%3A%3ANodeLib.FaceDetection.DetectFaces";
    /// <summary>The People detector's own node, of the library stored under <c>people_detector</c>.</summary>
    const string PeopleDetectorKind = "library:people_detector:global%3A%3ANodeLib.PeopleDetector";

    /// <summary>The libraries in <c>hass_sharp.toml</c>'s order: the id each is stored under, and its file name.</summary>
    static readonly (string Id, string Name)[] Libraries = [("vision", "Vision.cs"), ("people", "People.cs"), ("people_detector", "PeopleDetector.cs")];

    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    static string Node(string file) => File.ReadAllText(Path.Combine(VisionTests.Root, "nodes", file));

    /// <summary><c>nodes/front_door_people.json</c> as committed.</summary>
    static JsonObject FrontDoorPeople() => JsonNode.Parse(Node("front_door_people.json"))!.AsObject();

    static object Library(string id, string name) => new { id, name, source = Node(name), expectedRevision = 0 };

    static async Task<JsonObject> Answer(Func<string, Task<string>> request, object body) => JsonNode.Parse(await request(JsonSerializer.Serialize(body)))!.AsObject();

    static async Task<JsonObject> Ok(Func<string, Task<string>> request, object body)
    {
        var answer = await Answer(request, body);
        Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString());
        return answer["result"]!.AsObject();
    }

    /// <summary>A manager on a home of its own, with the entity descriptions; unloaded and removed afterwards.</summary>
    static async Task Fresh(string name, Func<HassSharpManager, Task> body)
    {
        await using var home = new EntityTestHome("vision-manifest-" + name);
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            api.SetEntityDescriptions(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ha-entity-components.json")), started: true);
            await body(api);
        }
        finally { await api.UnloadAsync(); }
    }

    /// <summary>The repository's three libraries, imported together at revision 0, as Add external sends them.</summary>
    static async Task<JsonObject> ImportAll(HassSharpManager api) =>
        await Ok(api.NodeLibraryRequest, new { operation = "import", libraries = Libraries.Select(l => Library(l.Id, l.Name)).ToArray() });

    /// <summary>The one node of the definition's graph.</summary>
    static JsonObject TheNode(JsonObject definition) => definition["logic"]!["graph"]!["nodes"]!.AsArray().Single()!.AsObject();

    [RuntimeSuite("--vision-manifest", Order = 43)]
    public static async Task Run()
    {
        var tests = new (string, Func<Task>)[]
        {
            (nameof(TheRepositoryLibrariesImportTogether), TheRepositoryLibrariesImportTogether),
            (nameof(PeopleDetectorNeedsVisionAndPeople), PeopleDetectorNeedsVisionAndPeople),
            (nameof(ThePeopleDetectorIsANodeOfItsOwn), ThePeopleDetectorIsANodeOfItsOwn),
            (nameof(TheRepositoryFrontDoorPeoplePreviews), TheRepositoryFrontDoorPeoplePreviews),
            (nameof(AnUpgradeFromOneZeroKeepsTheDefinitions), AnUpgradeFromOneZeroKeepsTheDefinitions),
        };
        var failures = new List<string>();
        foreach (var (name, test) in tests)
            try { await test(); }
            catch (Exception e) { failures.Add($"{name}: {e}"); }
        if (failures.Count > 0) throw new Exception($"Vision manifest: {failures.Count} failing:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS vision manifest: Vision.cs, People.cs and PeopleDetector.cs import together, PeopleDetector.cs needs the other two, the People detector is a node in Vision with its fourteen settings, Front door people previews with that node, and they import over 1.0.x's, whose C# device nodes take up the new settings");
    }

    static async Task TheRepositoryLibrariesImportTogether() => await Fresh("all", async api =>
    {
        var imported = await ImportAll(api);
        Check(imported["saved"]!.GetValue<bool>(), "the import saves: " + imported.ToJsonString());
        var documents = imported["documents"]!.AsArray();
        Check(documents.Select(d => (d!["id"]!.GetValue<string>(), d["name"]!.GetValue<string>(), d["revision"]!.GetValue<long>()))
            .SequenceEqual(Libraries.Select(l => (l.Id, l.Name, 1L))), "the three libraries are saved at revision 1, in order: " + documents.ToJsonString());
        Check(imported["catalog"]!["nodes"]!.AsObject().ContainsKey(DetectFacesKind), "the import's catalog has Detect faces, " + DetectFacesKind);
        var catalog = await Ok(api.NodeLibraryRequest, new { operation = "catalog" });
        Check(catalog["valid"]!.GetValue<bool>() && catalog["catalog"]!["nodes"]!.AsObject().ContainsKey(DetectFacesKind), "the saved catalog is valid and has Detect faces: " + catalog["diagnostics"]?.ToJsonString());
        var diagnostics = catalog["diagnostics"]?.AsArray() ?? [];
        Check(diagnostics.Count == 0, "the three compile without a diagnostic: " + diagnostics.ToJsonString());
    });

    static async Task PeopleDetectorNeedsVisionAndPeople() => await Fresh("alone", async api =>
    {
        var alone = new { operation = "import", libraries = new[] { Library("people_detector", "PeopleDetector.cs") } };
        var refused = await Answer(api.NodeLibraryRequest, alone);
        Check(refused["ok"]!.GetValue<bool>() == false && refused["error"]!["code"]!.GetValue<string>() == "validation", "PeopleDetector.cs alone is refused: " + refused.ToJsonString());
        var diagnostics = refused["error"]!["detail"]!["diagnostics"]!.AsArray();
        Check(diagnostics.Any(d => d!["sourceId"]?.GetValue<string>() == "people_detector" && d["severity"]!.GetValue<int>() == 3), "an error is reported in PeopleDetector.cs: " + diagnostics.ToJsonString());
        var listed = await Ok(api.NodeLibraryRequest, new { operation = "list" });
        Check(listed["documents"]!.AsArray().Count == 0, "nothing is stored after the refusal: " + listed.ToJsonString());

        // Beside Vision.cs alone it is refused too: it uses People.cs's list.
        var vision = await Ok(api.NodeLibraryRequest, new { operation = "save", id = "vision", name = "Vision.cs", expectedRevision = 0, source = Node("Vision.cs") });
        Check(vision["saved"]!.GetValue<bool>(), "Vision.cs saves: " + vision.ToJsonString());
        var withoutPeople = await Answer(api.NodeLibraryRequest, alone);
        Check(withoutPeople["ok"]!.GetValue<bool>() == false && withoutPeople["error"]!["code"]!.GetValue<string>() == "validation", "PeopleDetector.cs beside Vision.cs alone is refused: " + withoutPeople.ToJsonString());

        var people = await Ok(api.NodeLibraryRequest, new { operation = "save", id = "people", name = "People.cs", expectedRevision = 0, source = Node("People.cs") });
        Check(people["saved"]!.GetValue<bool>(), "People.cs saves beside Vision.cs: " + people.ToJsonString());
        var imported = await Ok(api.NodeLibraryRequest, alone);
        Check(imported["documents"]!.AsArray().Single()!["id"]!.GetValue<string>() == "people_detector", "PeopleDetector.cs imports beside the stored Vision.cs and People.cs: " + imported.ToJsonString());
    });

    static async Task ThePeopleDetectorIsANodeOfItsOwn() => await Fresh("node", async api =>
    {
        await ImportAll(api);
        var catalog = await Ok(api.NodeLibraryRequest, new { operation = "catalog" });
        var entry = catalog["catalog"]!["nodes"]![PeopleDetectorKind]?.AsObject() ?? throw new Exception("the catalog has the People detector's node, " + PeopleDetectorKind);
        static string? Text(JsonNode? node, string key) => node?[key]?.GetValue<string>();
        Check(Text(entry, "label") == "People detector" && Text(entry, "category") == "Vision" && Text(entry, "declares") == "device" && Text(entry, "sourceId") == "people_detector"
            && entry["documents"]?.ToJsonString() == """["entity"]""" && Text(entry, "description") is { Length: > 0 },
            "a device node of entity definitions, titled People detector in Vision, from people_detector, with the class's summary: " + entry.ToJsonString());
        var settings = entry["classSignature"]!["settings"]!.AsArray().Select(s => s!.AsObject()).ToArray();
        string[] keys = ["Camera", "CheckEvery", "StayOnFor", "FaceScore", "PersonScore", "DetectPeople", "RecogniseFaces", "MatchScore", "LearnNewLooks", "KeepUnknownFacesFor", "KeepPictures", "BoxesOnTheCamera", "FacePointsOnTheCamera", "NamesOnTheCamera"];
        Check(settings.Select(s => Text(s, "key")).SequenceEqual(keys), "its fourteen settings, in order: " + string.Join(",", settings.Select(s => Text(s, "key"))));
        Check(settings.Where(s => s["required"]?.GetValue<bool>() == true).Select(s => Text(s, "key")).SequenceEqual(["Camera"]) && Text(settings[0], "domain") == "camera",
            "only Camera, a camera, is required: " + entry["classSignature"]!.ToJsonString());

        // The step list shows the class's summary: a sentence or two for someone choosing a step, with nothing dropped.
        Check(Text(entry, "description") == PeopleDetectorDescription, "the description is the class's short summary: " + Text(entry, "description"));
        Plain(Text(entry, "description"), 300, "the People detector's description");
        foreach (var setting in settings) Plain(Text(setting, "description"), 300, $"the description of the setting {Text(setting, "key")}");

        // Detect faces and its inputs, the same.
        var detect = catalog["catalog"]!["nodes"]![DetectFacesKind]!.AsObject();
        Plain(Text(detect, "description"), 300, "Detect faces' description");
        var inputs = detect["inputs"]?.AsObject() ?? throw new Exception("Detect faces has its inputs: " + detect.ToJsonString());
        Check(inputs.Count == 3, "Detect faces has three inputs: " + inputs.ToJsonString());
        foreach (var (key, input) in inputs) Plain(Text(input, "description"), 300, $"the description of Detect faces' input {key}");
    });

    const string PeopleDetectorDescription = "Watches a Home Assistant camera and reports the faces and people it sees, and recognises the faces of the people you name. Its page, on the Device tab, lists the faces it does not know, for you to name.";

    /// <summary>
    /// A description the panel shows: there, not empty, at most <paramref name="longest"/> characters, and with nothing
    /// left out of it, as a dropped reference leaves "()" or two spaces.
    /// </summary>
    static void Plain(string? text, int longest, string what) =>
        Check(text is { Length: > 0 } && text.Length <= longest && !text.Contains("()", StringComparison.Ordinal) && !text.Contains("  ", StringComparison.Ordinal),
            $"{what} is plain text of at most {longest} characters: {text?.Length} characters, {text}");

    static async Task TheRepositoryFrontDoorPeoplePreviews() => await Fresh("front-door", async api =>
    {
        await ImportAll(api);
        var stored = FrontDoorPeople();
        var node = TheNode(stored);
        var config = node["config"]!.AsObject();
        Check(node["id"]?.GetValue<string>() == "front_door" && node["kind"]?.GetValue<string>() == PeopleDetectorKind,
            "the definition's one node is front_door, the People detector's own node of the library people_detector: " + node.ToJsonString());
        Check(string.Join(",", config.Select(p => p.Key)) == "key,name,arguments" && config["key"]?.GetValue<string>() == "front_door" && config["name"]?.GetValue<string>() == "Front door",
            "its config is key front_door, name Front door and its arguments, with no code and no signature: " + config.ToJsonString());
        Check(JsonNode.DeepEquals(config["arguments"], new JsonObject { ["Camera"] = new JsonObject { ["kind"] = "literal", ["type"] = "entity_id:camera", ["value"] = "camera.front_door" } }),
            "its one argument is the Camera, camera.front_door: " + config["arguments"]!.ToJsonString());

        // As stored: valid, with no error. The only advice allowed is that camera.front_door is not a camera this Home
        // Assistant has.
        var asStored = await Ok(api.EntityRequest, new { operation = "preview", definition = stored });
        var diagnostics = asStored["diagnostics"]!.AsArray();
        Check(asStored["valid"]!.GetValue<bool>() && diagnostics.All(d => d!["severity"]!.GetValue<int>() < 3 && d["message"]!.GetValue<string>().Contains("camera.front_door", StringComparison.Ordinal)),
            "the definition previews as stored with no error: " + asStored.ToJsonString());
    });

    /// <summary>
    /// 1.0.x's <c>PeopleDetector.cs</c> as far as an import of 1.1.0 over it can tell: an abstract device class with the
    /// five settings 1.0.x had, of the same keys, types and bounds. (1.0.x's checks and entities are left out: nothing
    /// here starts a definition.)
    /// </summary>
    const string OneZeroPeopleDetector = """
        // hass-sharp-vision 1.0.0, from https://github.com/dompagoj/hass-sharp-vision
        /// <summary>A device that watches one of Home Assistant's cameras and reports the faces and people it sees.</summary>
        public abstract class PeopleDetector : SharpDevice
        {
            /// <summary>The device.</summary>
            protected PeopleDetector() { Manufacturer = "HassSharp"; Model = "People detector"; }

            /// <summary>The camera to watch.</summary>
            [Setting("Camera")]
            public CameraEntity Camera { get; set; } = null!;

            /// <summary>How many seconds pass between checks of the camera's newest frame, from 0.1 to 60.</summary>
            [Setting("Check every", Minimum = 0.1, Maximum = 60)]
            public double CheckEvery { get; set; } = 0.5;

            /// <summary>How many seconds Face seen and Person seen stay on after the last frame with one, from 0 to 3600.</summary>
            [Setting("Stay on for", Minimum = 0, Maximum = 3600)]
            public double StayOnFor { get; set; } = 10;

            /// <summary>The lowest score a face counts with, from 0.1 to 1: lower finds more, and more that are not faces.</summary>
            [Setting("Face score", Minimum = 0.1, Maximum = 1)]
            public double FaceScore { get; set; } = 0.7;

            /// <summary>The lowest score a person counts with, from 0.1 to 1: lower finds more, and more that are not people.</summary>
            [Setting("Person score", Minimum = 0.1, Maximum = 1)]
            public double PersonScore { get; set; } = 0.45;
        }
        """;

    static readonly string[] OneZeroSettings = ["Camera", "CheckEvery", "StayOnFor", "FaceScore", "PersonScore"];
    static readonly string[] AllSettings = [.. OneZeroSettings, "DetectPeople", "RecogniseFaces", "MatchScore", "LearnNewLooks", "KeepUnknownFacesFor", "KeepPictures", "BoxesOnTheCamera", "FacePointsOnTheCamera", "NamesOnTheCamera"];

    static string[] SettingKeys(JsonObject node) => [.. node["config"]!["signature"]!["settings"]!.AsArray().Select(s => s!["key"]!.GetValue<string>())];

    static JsonObject Literal(string type, JsonNode value) => new() { ["kind"] = "literal", ["type"] = type, ["value"] = value };

    /// <summary>
    /// A definition of one C# device node deriving <c>PeopleDetector</c>, of the id and key, with the arguments, its
    /// signature derived from its code against the libraries stored now; saved turned off at revision 0.
    /// </summary>
    static async Task<JsonObject> SavedOff(HassSharpManager api, string id, string name, string nodeId, string code, JsonObject arguments)
    {
        var definition = FrontDoorPeople();
        definition["name"] = name;
        definition["logic"]!["graph"]!["name"] = name;
        var node = TheNode(definition);
        node["id"] = nodeId;
        node["kind"] = "csharp_device";
        node["config"] = new JsonObject { ["key"] = nodeId, ["name"] = name, ["source"] = code, ["arguments"] = arguments };
        var derived = await Ok(api.EntityRequest, new { operation = "preview", definition, deriveSignatures = true });
        Check(derived["valid"]!.GetValue<bool>(), $"{name} previews valid against the stored libraries: " + derived.ToJsonString());
        node["config"]!["signature"] = derived["graph"]!["nodes"]!.AsArray().Single()!["config"]!["signature"]!.DeepClone();
        var saved = await Ok(api.EntityRequest, new { operation = "save", id, expectedRevision = 0, definition, enabled = false });
        Check(saved["saved"]!.GetValue<bool>(), $"{name} saves: " + saved.ToJsonString());
        return TheNode(saved["document"]!.AsObject());
    }

    static async Task<JsonObject> Read(HassSharpManager api, string id) => await Ok(api.EntityRequest, new { operation = "read", id });

    // An install of 1.0.x: its two libraries, its Front door people (the C# device node front_door deriving
    // PeopleDetector, its camera chosen) and a definition of the user's own deriving the class, with a score typed in.
    // 1.1.0's three libraries import over them, as Add external sends them: the save gives each C# device node the fourteen
    // settings with its code, values and wires as they were. Front door people is then overwritten with the file's, the
    // People detector's own node under the same node id and key, with the camera kept as the overwrite keeps it.
    static async Task AnUpgradeFromOneZeroKeepsTheDefinitions() => await Fresh("upgrade", async api =>
    {
        var oneZero = await Answer(api.NodeLibraryRequest, new
        {
            operation = "import",
            libraries = new object[] { Library("vision", "Vision.cs"), new { id = "people_detector", name = "PeopleDetector.cs", source = OneZeroPeopleDetector, expectedRevision = 0 } },
        });
        Check(oneZero["ok"]!.GetValue<bool>(), "1.0.x's libraries are stored: " + oneZero.ToJsonString());
        const string frontDoorCode = "/// <summary>A People detector on the front door's camera.</summary>\npublic class FrontDoorPeople : PeopleDetector { }\n";
        const string porchCode = "public class Porch : PeopleDetector { }";
        var porch = Literal("entity_id:camera", "camera.porch");
        var frontDoor = await SavedOff(api, "front_door_people", "Front door people", "front_door", frontDoorCode, new JsonObject { ["Camera"] = porch.DeepClone() });
        var own = await SavedOff(api, "porch_people", "Porch people", "porch", porchCode,
            new JsonObject { ["Camera"] = Literal("entity_id:camera", "camera.back"), ["FaceScore"] = Literal("number", 0.8) });
        Check(SettingKeys(frontDoor).SequenceEqual(OneZeroSettings) && SettingKeys(own).SequenceEqual(OneZeroSettings), "both C# device nodes have 1.0.x's five settings: " + frontDoor.ToJsonString());

        // 1.1.0's libraries over them: accepted, and both definitions rebuilt.
        var imported = await Ok(api.NodeLibraryRequest, new
        {
            operation = "import",
            libraries = Libraries.Select(l => new { id = l.Id, name = l.Name, source = Node(l.Name), expectedRevision = l.Id == "people" ? 0 : 1 }).ToArray(),
        });
        Check(imported["saved"]!.GetValue<bool>(), "1.1.0's libraries import over 1.0.x's: " + imported.ToJsonString());
        var rebuilt = imported["definitions"]!.AsArray().Select(d => d!["id"]!.GetValue<string>()).Order().ToArray();
        Check(rebuilt.SequenceEqual(["front_door_people", "porch_people"]) && imported["invalidDefinitions"]!.AsArray().Count == 0, "both definitions are rebuilt: " + imported.ToJsonString());
        foreach (var (id, before) in new[] { ("front_door_people", frontDoor), ("porch_people", own) })
        {
            var node = TheNode(await Read(api, id));
            Check(SettingKeys(node).SequenceEqual(AllSettings), $"{id}'s C# device node has the fourteen settings: " + string.Join(",", SettingKeys(node)));
            Check(node["kind"]!.GetValue<string>() == "csharp_device" && node["config"]!["source"]!.GetValue<string>() == before["config"]!["source"]!.GetValue<string>()
                && JsonNode.DeepEquals(node["config"]!["arguments"], before["config"]!["arguments"]),
                $"{id}'s code and values are as they were, so the new settings are at their defaults: " + node.ToJsonString());
        }

        // Front door people overwritten with the file's: the People detector's node, of the same node id and key. The
        // overwrite keeps the camera (external.py's keep_device_inputs; tests/test_manifest.py): it is a literal of the pin
        // type of the class's Camera.
        var stored = await Read(api, "front_door_people");
        var catalog = await Ok(api.NodeLibraryRequest, new { operation = "catalog" });
        var camera = catalog["catalog"]!["nodes"]![PeopleDetectorKind]!["classSignature"]!["settings"]!.AsArray().Single(s => s!["key"]!.GetValue<string>() == "Camera")!;
        Check(camera["type"]!.GetValue<string>() == "entity" && camera["domain"]!.GetValue<string>() == "camera", "the class's Camera takes an entity_id:camera literal: " + camera.ToJsonString());
        var file = FrontDoorPeople();
        TheNode(file)["config"]!["arguments"]!["Camera"] = TheNode(stored)["config"]!["arguments"]!["Camera"]!.DeepClone();
        var overwritten = await Ok(api.EntityRequest, new { operation = "save", id = "front_door_people", expectedRevision = stored["revision"]!.GetValue<long>(), definition = file });
        var now = TheNode(overwritten["document"]!.AsObject());
        Check(now["id"]!.GetValue<string>() == "front_door" && now["kind"]!.GetValue<string>() == PeopleDetectorKind && now["config"]!["key"]!.GetValue<string>() == "front_door"
            && JsonNode.DeepEquals(now["config"]!["arguments"]!["Camera"], porch),
            "the overwrite is the People detector's node front_door, keyed front_door, on camera.porch: " + now.ToJsonString());
        var devices = overwritten["document"]!["devices"]!.AsArray().Select(d => d!["key"]!.GetValue<string>()).ToArray();
        Check(devices.SequenceEqual(["front_door"]), "its device is still keyed front_door, so it keeps its entities and data: " + string.Join(",", devices));
    });
}

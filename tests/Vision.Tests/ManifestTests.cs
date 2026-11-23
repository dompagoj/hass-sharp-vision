using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;

/// <summary>
/// What <c>hass_sharp.toml</c> offers, as HassSharp's Add external imports it: <c>Vision.cs</c> and
/// <c>PeopleDetector.cs</c> in one library <c>import</c>, then the definition <c>nodes/front_door_people.json</c>, each on
/// a whole manager of its own (<see cref="EntityTestHome"/>) with the entity descriptions, so that <c>CameraEntity</c>
/// exists. No native library is needed: nothing here starts a definition.
/// </summary>
static class ManifestTests
{
    const string DetectFacesKind = "library:vision:global%3A%3ANodeLib.FaceDetection.DetectFaces";

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

    /// <summary>The repository's two libraries, imported together at revision 0, as Add external sends them.</summary>
    static async Task<JsonObject> ImportBoth(HassSharpManager api) =>
        await Ok(api.NodeLibraryRequest, new { operation = "import", libraries = new[] { Library("vision", "Vision.cs"), Library("people_detector", "PeopleDetector.cs") } });

    [RuntimeSuite("--vision-manifest", Order = 43)]
    public static async Task Run()
    {
        var tests = new (string, Func<Task>)[]
        {
            (nameof(TheRepositoryLibrariesImportTogether), TheRepositoryLibrariesImportTogether),
            (nameof(PeopleDetectorNeedsVision), PeopleDetectorNeedsVision),
            (nameof(TheRepositoryFrontDoorPeoplePreviews), TheRepositoryFrontDoorPeoplePreviews),
        };
        var failures = new List<string>();
        foreach (var (name, test) in tests)
            try { await test(); }
            catch (Exception e) { failures.Add($"{name}: {e}"); }
        if (failures.Count > 0) throw new Exception($"Vision manifest: {failures.Count} failing:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS vision manifest: Vision.cs and PeopleDetector.cs import together, PeopleDetector.cs needs Vision.cs, and Front door people previews with the signature PeopleDetector.cs derives");
    }

    static async Task TheRepositoryLibrariesImportTogether() => await Fresh("pair", async api =>
    {
        var imported = await ImportBoth(api);
        Check(imported["saved"]!.GetValue<bool>(), "the import saves: " + imported.ToJsonString());
        var documents = imported["documents"]!.AsArray();
        Check(documents.Select(d => (d!["id"]!.GetValue<string>(), d["name"]!.GetValue<string>(), d["revision"]!.GetValue<long>()))
            .SequenceEqual([("vision", "Vision.cs", 1L), ("people_detector", "PeopleDetector.cs", 1L)]), "both libraries are saved at revision 1: " + documents.ToJsonString());
        Check(imported["catalog"]!["nodes"]!.AsObject().ContainsKey(DetectFacesKind), "the import's catalog has Detect faces, " + DetectFacesKind);
        var catalog = await Ok(api.NodeLibraryRequest, new { operation = "catalog" });
        Check(catalog["valid"]!.GetValue<bool>() && catalog["catalog"]!["nodes"]!.AsObject().ContainsKey(DetectFacesKind), "the saved catalog is valid and has Detect faces: " + catalog["diagnostics"]?.ToJsonString());
    });

    static async Task PeopleDetectorNeedsVision() => await Fresh("alone", async api =>
    {
        var alone = new { operation = "import", libraries = new[] { Library("people_detector", "PeopleDetector.cs") } };
        var refused = await Answer(api.NodeLibraryRequest, alone);
        Check(refused["ok"]!.GetValue<bool>() == false && refused["error"]!["code"]!.GetValue<string>() == "validation", "PeopleDetector.cs alone is refused: " + refused.ToJsonString());
        var diagnostics = refused["error"]!["detail"]!["diagnostics"]!.AsArray();
        Check(diagnostics.Any(d => d!["sourceId"]?.GetValue<string>() == "people_detector" && d["severity"]!.GetValue<int>() == 3), "an error is reported in PeopleDetector.cs: " + diagnostics.ToJsonString());
        var listed = await Ok(api.NodeLibraryRequest, new { operation = "list" });
        Check(listed["documents"]!.AsArray().Count == 0, "nothing is stored after the refusal: " + listed.ToJsonString());

        var vision = await Ok(api.NodeLibraryRequest, new { operation = "save", id = "vision", name = "Vision.cs", expectedRevision = 0, source = Node("Vision.cs") });
        Check(vision["saved"]!.GetValue<bool>(), "Vision.cs saves: " + vision.ToJsonString());
        var imported = await Ok(api.NodeLibraryRequest, alone);
        Check(imported["documents"]!.AsArray().Single()!["id"]!.GetValue<string>() == "people_detector", "PeopleDetector.cs imports beside the stored Vision.cs: " + imported.ToJsonString());
    });

    static async Task TheRepositoryFrontDoorPeoplePreviews() => await Fresh("front-door", async api =>
    {
        await ImportBoth(api);
        var stored = FrontDoorPeople();
        var nodes = stored["logic"]!["graph"]!["nodes"]!.AsArray();
        Check(nodes.All(n => !n!["kind"]!.GetValue<string>().StartsWith("library:", StringComparison.Ordinal)), "the definition names no library node kind, which import would rewrite: " + nodes.ToJsonString());
        var device = nodes.Single(n => n!["kind"]!.GetValue<string>() == "csharp_device")!;

        // Its signature left out and derived: the stored one is what PeopleDetector.cs derives now.
        var sent = (JsonObject)stored.DeepClone();
        sent["logic"]!["graph"]!["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == device["id"]!.GetValue<string>())!["config"]!.AsObject().Remove("signature");
        var derived = await Ok(api.EntityRequest, new { operation = "preview", definition = sent, deriveSignatures = true });
        var signature = derived["graph"]?["nodes"]?.AsArray().Single(n => n!["id"]!.GetValue<string>() == device["id"]!.GetValue<string>())!["config"]!["signature"];
        Check(derived["valid"]!.GetValue<bool>() && signature != null, "the definition previews with its signature derived: " + derived.ToJsonString());
        Check(JsonNode.DeepEquals(device["config"]!["signature"], signature),
            "the stored signature is the one PeopleDetector.cs derives; put this in nodes/front_door_people.json: " + signature!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // As stored, without deriving: valid, with no error. The only advice allowed is that camera.front_door is not a
        // camera this Home Assistant has.
        var asStored = await Ok(api.EntityRequest, new { operation = "preview", definition = stored });
        var diagnostics = asStored["diagnostics"]!.AsArray();
        Check(asStored["valid"]!.GetValue<bool>() && diagnostics.All(d => d!["severity"]!.GetValue<int>() < 3 && d["message"]!.GetValue<string>().Contains("camera.front_door", StringComparison.Ordinal)),
            "the definition previews as stored, without deriving, with no error: " + asStored.ToJsonString());
    });
}

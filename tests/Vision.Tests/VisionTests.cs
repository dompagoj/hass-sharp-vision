using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HassSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// <c>nodes/Vision.cs</c>, compiled as the library compiler builds it: directly, into a collectible load context with a
/// hand-made <see cref="NodeSetupContext"/>, and end to end, saved as library <c>vision</c> and used by an entity
/// definition. Its download URL and checksum table are rewritten to a loopback server that serves a local
/// <c>hs_vision</c> build (<see cref="LocalLibrary"/>); without one, the tests that need it are skipped, unless
/// <c>HS_VISION_REQUIRE_LIBRARY=1</c>. The recogniser's tests serve a local SFace model the same way
/// (<see cref="LocalModel"/>), and are skipped without one, unless <c>HS_VISION_REQUIRE_LIBRARY=1</c> too.
/// </summary>
static class VisionTests
{
    const string NotSetUp = "Detect faces is not set up: it works in an entity definition, once the definition has started";
    const string Stopped = "Detect faces has stopped with its definition";
    const string Closed = "The vision detector has been closed";
    const string ScoreMessage = "Minimum score is from 0 to 1";
    const string SizeMessage = "Input size is a multiple of 32 from 64 to 1280";
    const string DetectFacesKind = "library:vision:global%3A%3ANodeLib.FaceDetection.DetectFaces";

    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static string Rid => RuntimeInformation.RuntimeIdentifier;

    static string Metadata(string key) => typeof(VisionTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value
        ?? throw new InvalidOperationException($"The test assembly has no {key}");

    /// <summary>The hass-sharp-vision checkout these tests were built from.</summary>
    internal static string Root { get; } = Path.GetFullPath(Metadata("VisionRoot"));

    /// <summary>A picture under <c>native/tests/fixtures/</c>.</summary>
    internal static string Fixture(string name) => Path.Combine(Root, "native", "tests", "fixtures", name);

    static readonly Lazy<string?> local = new(FindLibrary);

    /// <summary>
    /// The <c>hs_vision</c> build these tests load: <c>HS_VISION_LIBRARY</c> when set (it must exist); else
    /// <c>native/dist/libhs_vision-&lt;rid&gt;.so</c>, from a <c>build.sh</c> run on Linux; else a host <c>cargo build</c>'s
    /// <c>native/target/release/libhs_vision.dylib</c> or <c>.so</c>; null when there is none.
    /// </summary>
    internal static string? LocalLibrary => local.Value;

    static string? FindLibrary()
    {
        if (Environment.GetEnvironmentVariable("HS_VISION_LIBRARY") is { Length: > 0 } named)
            return File.Exists(named) ? Path.GetFullPath(named) : throw new Exception($"HS_VISION_LIBRARY is {named}, which does not exist");
        var native = Path.Combine(Root, "native");
        return new[]
        {
            Path.Combine(native, "dist", $"libhs_vision-{Rid}.so"),
            Path.Combine(native, "target", "release", "libhs_vision.dylib"),
            Path.Combine(native, "target", "release", "libhs_vision.so"),
        }.FirstOrDefault(File.Exists);
    }

    /// <summary>The file name of SFace, the face recognition model, on the release and on disk.</summary>
    internal const string ModelName = "face_recognition_sface_2021dec.onnx";

    static readonly Lazy<string?> model = new(FindModel);

    /// <summary>
    /// The SFace model these tests serve: <c>HS_VISION_MODEL</c> when set (it must exist); else
    /// <c>native/model/face_recognition_sface_2021dec.onnx</c>, from <c>native/fetch-model.sh</c>; null when there is none.
    /// </summary>
    internal static string? LocalModel => model.Value;

    static string? FindModel()
    {
        if (Environment.GetEnvironmentVariable("HS_VISION_MODEL") is { Length: > 0 } named)
            return File.Exists(named) ? Path.GetFullPath(named) : throw new Exception($"HS_VISION_MODEL is {named}, which does not exist");
        var path = Path.Combine(Root, "native", "model", ModelName);
        return File.Exists(path) ? path : null;
    }

    /// <summary><c>nodes/Vision.cs</c> as committed.</summary>
    internal static string Committed() => File.ReadAllText(Path.Combine(Root, "nodes", "Vision.cs"));

    /// <summary><paramref name="text"/>, a <c>Vision.cs</c>, with its model's pinned SHA-256 set to <paramref name="sha256"/>.</summary>
    internal static string WithModel(string text, string sha256)
    {
        var pin = new Regex("^(\\s*)const string ModelSha256 = \"[^\"]*\";$", RegexOptions.Multiline);
        Check(pin.Matches(text).Count == 1, "Vision.cs has one ModelSha256 line");
        return pin.Replace(text, match => $"{match.Groups[1].Value}const string ModelSha256 = \"{sha256}\";");
    }

    /// <summary>
    /// <see cref="LocalSource"/>, with <see cref="LocalModel"/> served at <c>/face_recognition_sface_2021dec.onnx</c> too
    /// and pinned by its checksum.
    /// </summary>
    internal static string LocalRecognizerSource(LoopbackServer server)
    {
        var bytes = File.ReadAllBytes(LocalModel ?? throw new InvalidOperationException("There is no SFace model"));
        server.Respond("/" + ModelName, new() { Body = bytes });
        return WithModel(LocalSource(server), Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>
    /// <c>Vision.cs</c> with its <c>Source</c> set to <paramref name="source"/> and its pinned table to
    /// <paramref name="table"/>, in place of the release's.
    /// </summary>
    internal static string Rewritten(string source, params (string Rid, string Sha256)[] table)
    {
        var text = Committed().Replace("\r\n", "\n");
        var url = new Regex("^(\\s*)const string Source = \"[^\"]*\";$", RegexOptions.Multiline);
        Check(url.Matches(text).Count == 1, "Vision.cs has one Source line");
        text = url.Replace(text, match => $"{match.Groups[1].Value}const string Source = \"{source}\";");
        var lines = text.Split('\n').ToList();
        var begin = lines.FindIndex(l => l.Trim() == "// pinned:begin");
        var end = lines.FindIndex(l => l.Trim() == "// pinned:end");
        Check(begin >= 0 && end > begin, "Vision.cs has // pinned:begin and // pinned:end");
        var indent = lines[begin][..(lines[begin].Length - lines[begin].TrimStart().Length)];
        lines.RemoveRange(begin + 1, end - begin - 1);
        lines.InsertRange(begin + 1, table.Select(entry => $"{indent}[\"{entry.Rid}\"] = \"{entry.Sha256}\","));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Serves <see cref="LocalLibrary"/> at <c>/libhs_vision-&lt;rid&gt;.so</c> on <paramref name="server"/> and gives
    /// <c>Vision.cs</c> rewritten to download it from there, pinned by its checksum, for this process's RID.
    /// </summary>
    internal static string LocalSource(LoopbackServer server)
    {
        var library = LocalLibrary ?? throw new InvalidOperationException($"There is no hs_vision library for {Rid}");
        var bytes = File.ReadAllBytes(library);
        server.Respond($"/libhs_vision-{Rid}.so", new() { Body = bytes });
        return Rewritten(server.Url("/"), (Rid, Convert.ToHexStringLower(SHA256.HashData(bytes))));
    }

    /// <summary>
    /// Whether <c>HS_VISION_REQUIRE_LIBRARY=1</c>: a missing library then fails the tests that need it instead of skipping
    /// them, and so does a missing model.
    /// </summary>
    static bool Required => Environment.GetEnvironmentVariable("HS_VISION_REQUIRE_LIBRARY") == "1";

    [RuntimeSuite("--vision", Order = 41)]
    public static async Task Run()
    {
        var tests = new List<(string, Func<Task>)>
        {
            (nameof(UnsupportedPlatformFailsClearly), UnsupportedPlatformFailsClearly),
            (nameof(AWrongChecksumNeverLoads), AWrongChecksumNeverLoads),
            (nameof(NotSetUpThrows), NotSetUpThrows),
            (nameof(CompilesAsLibraryVision), CompilesAsLibraryVision),
            (nameof(AWrongModelChecksumNeverLoads), AWrongModelChecksumNeverLoads),
            (nameof(ALibraryOfAbi1IsRefused), ALibraryOfAbi1IsRefused),
            (nameof(SimilarityIsTheDotProduct), SimilarityIsTheDotProduct),
            (nameof(FrontalNeedsTheEyesApartAndTheNoseBetweenThem), FrontalNeedsTheEyesApartAndTheNoseBetweenThem),
            (nameof(TheTurnIsHowFarTheNoseLiesAlongTheEyes), TheTurnIsHowFarTheNoseLiesAlongTheEyes),
            (nameof(DrawsTheBoxesAndThePointsEachOnlyWhenAsked), DrawsTheBoxesAndThePointsEachOnlyWhenAsked),
        };
        var failures = new List<string>();
        if (LocalLibrary != null)
        {
            tests.AddRange([
                (nameof(FindsTheFacesOfTheFixture), FindsTheFacesOfTheFixture),
                (nameof(FindsThePeopleOfTheFixtures), FindsThePeopleOfTheFixtures),
                (nameof(AYuvFrameFindsTheSame), AYuvFrameFindsTheSame),
                (nameof(DrawsOnlyWhatFitsUnderTheDrawingLimit), DrawsOnlyWhatFitsUnderTheDrawingLimit),
                (nameof(DisposeWaitsForADetectionAndLaterCallsFail), DisposeWaitsForADetectionAndLaterCallsFail),
                (nameof(DetectFacesRunsInAnEntityDefinition), DetectFacesRunsInAnEntityDefinition),
                (nameof(TheProfileOfTheFixtureIsNotFrontal), TheProfileOfTheFixtureIsNotFrontal),
                (nameof(DrawsANameInPlaceOfTheScore), DrawsANameInPlaceOfTheScore),
            ]);
            if (LocalModel != null)
                tests.AddRange([
                    (nameof(EmbedsAFaceOfTheFixture), EmbedsAFaceOfTheFixture),
                    (nameof(EmbedAfterDisposeFails), EmbedAfterDisposeFails),
                ]);
            else if (Required)
                failures.Add("the SFace model is required with the library (HS_VISION_REQUIRE_LIBRARY=1) and there is none: run native/fetch-model.sh, or set HS_VISION_MODEL");
            else
                Console.WriteLine("SKIP vision: no SFace model (native/fetch-model.sh, or HS_VISION_MODEL), so no recogniser is opened");
        }
        else if (Required)
            failures.Add($"an hs_vision library is required (HS_VISION_REQUIRE_LIBRARY=1) and there is none for {Rid}");
        else
            Console.WriteLine($"SKIP vision: no hs_vision library for {Rid}");
        foreach (var (name, test) in tests)
            try { await test(); }
            catch (Exception e) { failures.Add($"{name}: {e}"); }
        if (failures.Count > 0) throw new Exception($"Vision: {failures.Count} failing:\n" + string.Join("\n", failures));
        Console.WriteLine(LocalLibrary != null
            ? $"PASS vision: Vision.cs downloads hs_vision pinned by checksum, finds and draws faces and people, Detect faces runs in an entity definition{(LocalModel != null ? ", and the recogniser embeds a face" : "")} ({LocalLibrary})"
            : "PASS vision: Vision.cs refuses an unsupported platform, a wrong checksum and a library of ABI 1, and Detect faces refuses to run unset up");
    }

    // ---- Directly: Vision.cs in a load context of its own ----

    sealed class NoReceiving : IDisposable { public void Dispose() { } }

    static object? Invoke(MethodInfo method, object? target, params object?[] arguments) =>
        method.Invoke(target, BindingFlags.DoNotWrapExceptions, null, arguments, null);

    /// <summary>
    /// <c>Vision.cs</c> of <paramref name="source"/> compiled as the library compiler builds it (wrapped in <c>NodeLib</c>,
    /// with the global usings, the framework's references and unsafe code) and loaded into a collectible context, with
    /// files under a temporary directory and a setup context whose <c>Own</c> collects into <see cref="Owned"/>.
    /// </summary>
    internal sealed class Loaded : IAsyncDisposable
    {
        readonly ScriptLoadContext context = new();
        readonly Type vision, detection;
        readonly string directory = Path.Combine(Path.GetTempPath(), "vision-tests-" + Guid.NewGuid().ToString("N"));
        readonly List<string> logs = [];
        readonly List<IDisposable> owned = [];
        // Guards logs and owned, which the setup's callbacks write from any thread.
        readonly Lock gate = new();

        public Loaded(string source)
        {
            var trees = new[]
            {
                CSharpSyntaxTree.ParseText(NodeLibraryCompiler.WrapSource(source), new CSharpParseOptions(LanguageVersion.Latest), path: "Vision.cs"),
                CSharpSyntaxTree.ParseText(DiagnosticsProvider.GlobalUsings),
            };
            var compilation = CSharpCompilation.Create("Vision" + Guid.NewGuid().ToString("N"), trees, NodeLibraryCompiler.FrameworkReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image);
            var problems = emitted.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).ToArray();
            Check(emitted.Success && problems.Length == 0, "Vision.cs compiles without a warning: " + string.Join(" | ", problems.Select(d => d.ToString())));
            image.Position = 0;
            var assembly = context.LoadFromStream(image);
            vision = assembly.GetType("NodeLib.Vision", true)!;
            detection = assembly.GetType("NodeLib.FaceDetection", true)!;
            Files = new SharpFiles(directory, line => { lock (gate) logs.Add(line); }, () => new NoReceiving());
            Setups = new NodeSetupContext(Files, (line, _) => { lock (gate) logs.Add(line); }, CancellationToken.None, disposable => { lock (gate) owned.Add(disposable); });
        }

        public SharpFiles Files { get; }
        public NodeSetupContext Setups { get; }
        /// <summary>The directory the files are downloaded into.</summary>
        public string Directory => directory;
        public string[] Logs() { lock (gate) return [.. logs]; }
        public IDisposable[] Owned() { lock (gate) return [.. owned]; }
        /// <summary>Whether the context has loaded the native library.</summary>
        public bool LoadedLibrary => vision.GetField("loaded", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) != null;

        public async Task<IDisposable> Open(string? rid = null)
        {
            var task = (Task)Invoke(vision.GetMethod("OpenAsync")!, null, Files, rid ?? Rid, CancellationToken.None)!;
            await task;
            return (IDisposable)task.GetType().GetProperty("Result")!.GetValue(task)!;
        }

        public async Task<IDisposable> OpenRecognizer(string? rid = null)
        {
            var task = (Task)Invoke(vision.GetMethod("OpenRecognizerAsync")!, null, Files, rid ?? Rid, CancellationToken.None)!;
            await task;
            return (IDisposable)task.GetType().GetProperty("Result")!.GetValue(task)!;
        }

        /// <summary>The recogniser's embedding of <paramref name="face"/>, a face its detector found in <paramref name="image"/>.</summary>
        public static object Embed(IDisposable recognizer, SharpImage image, object face) =>
            Invoke(recognizer.GetType().GetMethod("Embed")!, recognizer, image, face)!;

        /// <summary>A <c>FaceEmbedding</c> of this context.</summary>
        public object Embedding(float[] values, float fit = 0) => Activator.CreateInstance(vision.Assembly.GetType("NodeLib.FaceEmbedding", true)!, values, fit)!;

        public static float Similarity(object embedding, object other) => (float)Invoke(embedding.GetType().GetMethod("Similarity")!, embedding, other)!;

        /// <summary>A <c>DetectedFace</c> of this context, with a score of 0.9 and both mouth corners below the nose.</summary>
        public object Face(float x, float y, float width, float height, (float X, float Y) rightEye, (float X, float Y) leftEye, (float X, float Y) noseTip)
        {
            var point = vision.Assembly.GetType("NodeLib.VisionPoint", true)!;
            object Point(float px, float py) => Activator.CreateInstance(point, px, py)!;
            return Activator.CreateInstance(vision.Assembly.GetType("NodeLib.DetectedFace", true)!, x, y, width, height, 0.9f,
                Point(rightEye.X, rightEye.Y), Point(leftEye.X, leftEye.Y), Point(noseTip.X, noseTip.Y), Point(rightEye.X, noseTip.Y + 20), Point(leftEye.X, noseTip.Y + 20))!;
        }

        public float EyeDistance(object face) => (float)Invoke(vision.GetMethod("EyeDistance")!, null, face)!;
        public bool Frontal(object face) => (bool)Invoke(vision.GetMethod("Frontal")!, null, face)!;
        public float Turn(object face) => (float)Invoke(vision.GetMethod("Turn") ?? throw new Exception("Vision has no Turn"), null, face)!;

        public Task Setup() => (Task)Invoke(detection.GetMethod("Setup")!, null, Setups)!;

        public SharpImage DetectFaces(SharpImage image, double minScore = 0.7, int inputSize = 320) =>
            (SharpImage)Invoke(detection.GetMethod("DetectFaces")!, null, image, minScore, inputSize)!;

        /// <summary>The detector's faces: the list it gives, to pass on to <see cref="DrawFaces"/>.</summary>
        public static object Faces(IDisposable detector, SharpImage image, int inputSize = 320, double minScore = 0.7) =>
            Invoke(detector.GetType().GetMethod("Faces")!, detector, image, inputSize, minScore)!;

        public static object People(IDisposable detector, SharpImage image, int inputSize = 416, double minScore = 0.45) =>
            Invoke(detector.GetType().GetMethod("People")!, detector, image, inputSize, minScore)!;

        MethodInfo Overload(string name, int parameters) => vision.GetMethods().Single(m => m.Name == name && m.GetParameters().Length == parameters);

        public SharpImage DrawFaces(SharpImage image, object faces) => (SharpImage)Invoke(Overload("DrawFaces", 2), null, image, faces)!;
        public SharpImage DrawFaces(SharpImage image, object faces, string?[] names) => (SharpImage)Invoke(Overload("DrawFaces", 3), null, image, faces, names)!;
        public SharpImage DrawFaces(SharpImage image, object faces, string?[]? names, bool boxes, bool points) => (SharpImage)Invoke(Overload("DrawFaces", 5), null, image, faces, names, boxes, points)!;
        public SharpImage DrawPeople(SharpImage image, object people) => (SharpImage)Invoke(vision.GetMethod("DrawPeople")!, null, image, people)!;

        public ValueTask DisposeAsync()
        {
            foreach (var disposable in Owned().Reverse()) disposable.Dispose();
            context.Unload();
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
            return ValueTask.CompletedTask;
        }
    }

    internal static object[] Items(object list) => [.. (IEnumerable<object>)list];
    internal static float Get(object item, string property) => (float)item.GetType().GetProperty(property)!.GetValue(item)!;

    static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T expected) { return expected; }
        catch (Exception other) { throw new Exception($"{what}: threw {other.GetType().Name} ({other.Message}), not {typeof(T).Name}", other); }
        throw new Exception($"{what}: did not throw");
    }

    static async Task<T> ThrowsAsync<T>(Func<Task> action, string what) where T : Exception
    {
        try { await action(); }
        catch (T expected) { return expected; }
        catch (Exception other) { throw new Exception($"{what}: threw {other.GetType().Name} ({other.Message}), not {typeof(T).Name}", other); }
        throw new Exception($"{what}: did not throw");
    }

    /// <summary>A call after the detector closed: the closed sentence, naming the detector.</summary>
    static void IsClosed(ObjectDisposedException closed, string what) =>
        Check(closed.ObjectName == "VisionDetector" && closed.Message.StartsWith(Closed, StringComparison.Ordinal), $"{what} gives the closed sentence: {closed.Message}");

    static async Task UnsupportedPlatformFailsClearly()
    {
        await using var server = LoopbackServer.Start();
        await using var vision = new Loaded(Rewritten(server.Url("/"), ("linux-riscv64", new string('a', 64))));
        var refused = await ThrowsAsync<PlatformNotSupportedException>(() => vision.Open(), "a RID without a build");
        Check(refused.Message == $"The vision library has no native build for {Rid}; it runs on linux-riscv64", "the refusal names both: " + refused.Message);
        Check(server.Requests().Length == 0 && !vision.LoadedLibrary, "nothing was requested or loaded");

        // The list of RIDs reads as a sentence, and the release's table is the four Linux ones.
        await using var four = new Loaded(Committed());
        var released = await ThrowsAsync<PlatformNotSupportedException>(() => four.Open("linux-riscv64"), "the committed table on riscv64");
        Check(released.Message == "The vision library has no native build for linux-riscv64; it runs on linux-arm64, linux-musl-arm64, linux-musl-x64 and linux-x64",
            "the committed table's RIDs: " + released.Message);
        Console.WriteLine("PASS vision: a RID without a build is refused naming it and those there are, before any request");
    }

    static async Task AWrongChecksumNeverLoads()
    {
        await using var server = LoopbackServer.Start();
        var expected = new byte[4096];
        new Random(41).NextBytes(expected);
        var sha = Convert.ToHexStringLower(SHA256.HashData(expected));
        server.Respond($"/libhs_vision-{Rid}.so", new() { Body = [.. expected.Select(b => (byte)~b)] });
        await using var vision = new Loaded(Rewritten(server.Url("/"), (Rid, sha)));
        await ThrowsAsync<InvalidDataException>(() => vision.Open(), "bytes that do not match the checksum");
        var directory = Path.Combine(vision.Directory, sha);
        Check(server.Count($"/libhs_vision-{Rid}.so") == 1, "the library was requested once");
        Check(!vision.LoadedLibrary, "nothing was loaded");
        Check(!File.Exists(Path.Combine(directory, $"libhs_vision-{Rid}.so")) && (!System.IO.Directory.Exists(directory) || System.IO.Directory.GetFileSystemEntries(directory).Length == 0),
            "no file is left: " + string.Join(", ", System.IO.Directory.Exists(directory) ? System.IO.Directory.GetFileSystemEntries(directory) : []));
        Console.WriteLine("PASS vision: a download that does not match its checksum fails with InvalidDataException, loads nothing and leaves no file");
    }

    static async Task NotSetUpThrows()
    {
        await using var vision = new Loaded(Committed());
        var image = SharpImage.Blank(64, 48);
        var refused = Throws<InvalidOperationException>(() => vision.DetectFaces(image), "Detect faces before its setup");
        Check(refused.Message == NotSetUp, "the not-set-up sentence: " + refused.Message);
        foreach (var score in new[] { 1.5, -0.1, double.NaN })
            Check(Throws<ArgumentException>(() => vision.DetectFaces(image, minScore: score), $"minimum score {score}").Message == ScoreMessage, $"minimum score {score} gives its sentence");
        foreach (var size in new[] { 100, 32, 1312, 0 })
            Check(Throws<ArgumentException>(() => vision.DetectFaces(image, inputSize: size), $"input size {size}").Message == SizeMessage, $"input size {size} gives its sentence");
        Check(image.Drawings == 0, "nothing was drawn");
        Console.WriteLine("PASS vision: Detect faces before its setup says it is not set up, and a bad minimum score or input size gives its sentence first");
    }

    static Task CompilesAsLibraryVision()
    {
        var preview = new NodeLibraryCompiler(VisualExecutionTests.Diagnostics).Preview([new NodeLibrarySource("vision", Committed(), 1)]);
        Check(preview.Valid && preview.Diagnostics.IsEmpty, "the library compiler takes Vision.cs without a diagnostic: " + string.Join(" | ", preview.Diagnostics.Select(d => $"{d.Severity} {d.StartLine}:{d.StartColumn} {d.Message}")));
        Check(preview.Snapshot.Methods.TryGetValue(DetectFacesKind, out var detect) && detect.Setup?.Class == "FaceDetection",
            $"saved as vision, Detect faces is {DetectFacesKind} with FaceDetection's setup: " + string.Join(", ", preview.Snapshot.Methods.Keys));
        Check(preview.Snapshot.Methods.Count == 1, "Detect faces is the library's one node: " + string.Join(", ", preview.Snapshot.Methods.Keys));
        Console.WriteLine($"PASS vision: the library compiler takes Vision.cs as library vision without a diagnostic, and its one node is {DetectFacesKind}, set up by FaceDetection");
        return Task.CompletedTask;
    }

    static async Task AWrongModelChecksumNeverLoads()
    {
        await using var server = LoopbackServer.Start();
        // The library matches its checksum, so only the model, pinned as committed, is wrong.
        var library = new byte[4096];
        new Random(43).NextBytes(library);
        server.Respond($"/libhs_vision-{Rid}.so", new() { Body = library });
        var bytes = new byte[4096];
        new Random(44).NextBytes(bytes);
        server.Respond("/" + ModelName, new() { Body = bytes });
        var pinned = Regex.Match(Committed(), "const string ModelSha256 = \"([0-9a-f]{64})\";").Groups[1].Value;
        Check(pinned == "0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79", "Vision.cs pins SFace's SHA-256: " + pinned);
        await using var vision = new Loaded(Rewritten(server.Url("/"), (Rid, Convert.ToHexStringLower(SHA256.HashData(library)))));
        await ThrowsAsync<InvalidDataException>(() => vision.OpenRecognizer(), "a model that does not match its checksum");
        Check(server.Count("/" + ModelName) == 1, "the model was requested once");
        Check(!vision.LoadedLibrary, "nothing was loaded");
        var directory = Path.Combine(vision.Directory, pinned);
        Check(!File.Exists(Path.Combine(directory, ModelName)) && (!System.IO.Directory.Exists(directory) || System.IO.Directory.GetFileSystemEntries(directory).Length == 0),
            "no model file is left: " + string.Join(", ", System.IO.Directory.Exists(directory) ? System.IO.Directory.GetFileSystemEntries(directory) : []));
        Console.WriteLine("PASS vision: a model download that does not match SFace's pinned checksum fails with InvalidDataException, loads nothing and leaves no file");
    }

    /// <summary>A C compiler on the PATH, or null.</summary>
    static string? Compiler() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(directory => Path.Combine(directory, "cc")).FirstOrDefault(File.Exists);

    static async Task ALibraryOfAbi1IsRefused()
    {
        if (Compiler() is not { } cc)
        {
            Console.WriteLine("SKIP vision: no C compiler (cc) to build a library of ABI 1");
            return;
        }
        // A library whose one export says it is ABI 1, as hs_vision 1.0.0's does.
        var build = Path.Combine(Path.GetTempPath(), "vision-abi1-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(build);
        try
        {
            File.WriteAllText(Path.Combine(build, "abi1.c"), "unsigned int hs_vision_abi_version(void) { return 1; }\n");
            var output = Path.Combine(build, OperatingSystem.IsMacOS() ? "libabi1.dylib" : "libabi1.so");
            var start = new System.Diagnostics.ProcessStartInfo(cc) { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "-shared", "-fPIC", "-o", output, Path.Combine(build, "abi1.c") }) start.ArgumentList.Add(argument);
            using (var compile = System.Diagnostics.Process.Start(start)!)
            {
                var errors = await compile.StandardError.ReadToEndAsync();
                await compile.WaitForExitAsync();
                Check(compile.ExitCode == 0, "cc builds the ABI 1 library: " + errors);
            }
            var library = File.ReadAllBytes(output);
            var model = new byte[64];
            await using var server = LoopbackServer.Start();
            server.Respond($"/libhs_vision-{Rid}.so", new() { Body = library });
            server.Respond("/" + ModelName, new() { Body = model });
            var source = WithModel(Rewritten(server.Url("/"), (Rid, Convert.ToHexStringLower(SHA256.HashData(library)))), Convert.ToHexStringLower(SHA256.HashData(model)));
            await using var vision = new Loaded(source);
            const string Refused = "The vision library is version 1, not 2: the download does not match this code";
            var detector = await ThrowsAsync<InvalidOperationException>(() => vision.Open(), "a detector from a library of ABI 1");
            Check(detector.Message == Refused, "the detector's refusal: " + detector.Message);
            var recognizer = await ThrowsAsync<InvalidOperationException>(() => vision.OpenRecognizer(), "a recogniser from a library of ABI 1");
            Check(recognizer.Message == Refused, "the recogniser's refusal: " + recognizer.Message);
            Check(!vision.LoadedLibrary, "the library is not taken as loaded");
        }
        finally { System.IO.Directory.Delete(build, true); }
        Console.WriteLine("PASS vision: a library of ABI 1 is refused, for a detector and for a recogniser, with the version sentence");
    }

    static async Task SimilarityIsTheDotProduct()
    {
        await using var vision = new Loaded(Committed());
        var random = new Random(45);
        float[] Unit()
        {
            var values = Enumerable.Range(0, 128).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
            var length = MathF.Sqrt(values.Sum(v => v * v));
            return [.. values.Select(v => v / length)];
        }
        var a = Unit();
        var b = Unit();
        var embedding = vision.Embedding(a, 0.5f);
        var self = Loaded.Similarity(embedding, vision.Embedding([.. a]));
        Check(MathF.Abs(self - 1) < 1e-5f, $"an embedding is 1 like itself: {self}");
        var opposite = Loaded.Similarity(embedding, vision.Embedding([.. a.Select(v => -v)]));
        Check(MathF.Abs(opposite + 1) < 1e-5f, $"an embedding is -1 like its opposite: {opposite}");
        var dot = a.Zip(b).Sum(pair => (double)pair.First * pair.Second);
        var other = Loaded.Similarity(embedding, vision.Embedding(b));
        Check(Math.Abs(other - dot) < 1e-5, $"another embedding's similarity is the dot product: {other}, not {dot}");
        Check(Throws<ArgumentException>(() => Loaded.Similarity(embedding, vision.Embedding(new float[127])), "embeddings of 128 and 127 values").Message
            == "The two embeddings do not have as many values", "embeddings of different lengths are refused");

        // A caller compares embeddings a great many times: n comparisons, called without reflection, allocate nothing.
        var type = embedding.GetType();
        ParameterExpression first = Expression.Parameter(typeof(object)), second = Expression.Parameter(typeof(object)), times = Expression.Parameter(typeof(int));
        ParameterExpression x = Expression.Variable(type), y = Expression.Variable(type), sum = Expression.Variable(typeof(float)), i = Expression.Variable(typeof(int));
        var done = Expression.Label(typeof(float));
        var compare = Expression.Lambda<Func<object, object, int, float>>(Expression.Block([x, y, sum, i],
            Expression.Assign(x, Expression.Unbox(first, type)), Expression.Assign(y, Expression.Unbox(second, type)),
            Expression.Assign(sum, Expression.Constant(0f)), Expression.Assign(i, Expression.Constant(0)),
            Expression.Loop(Expression.IfThenElse(Expression.LessThan(i, times),
                Expression.Block(Expression.AddAssign(sum, Expression.Call(x, type.GetMethod("Similarity")!, y)), Expression.PreIncrementAssign(i)),
                Expression.Break(done, sum)), done)), first, second, times).Compile();
        var them = vision.Embedding(b);
        compare(embedding, them, 10);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var total = compare(embedding, them, 100_000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0 && float.IsFinite(total), $"100,000 comparisons allocate nothing: {allocated} bytes, summing to {total}");
        Console.WriteLine($"PASS vision: Similarity is the dot product, 1 for an embedding and itself and -1 for its opposite ({other:0.000} for two random ones)");
    }

    static async Task FrontalNeedsTheEyesApartAndTheNoseBetweenThem()
    {
        await using var vision = new Loaded(Committed());
        // A 100-pixel box: its eyes must be at least 20 pixels apart.
        var facing = vision.Face(0, 0, 100, 100, (30, 40), (70, 40), (50, 60));
        Check(vision.EyeDistance(facing) == 40 && vision.Frontal(facing), "eyes 40 apart with the nose between them are frontal");
        var tilted = vision.Face(0, 0, 100, 100, (30, 30), (60, 70), (45, 60));
        Check(vision.EyeDistance(tilted) == 50 && vision.Frontal(tilted), $"the eye distance is the straight line between them: {vision.EyeDistance(tilted)}");
        Check(vision.Frontal(vision.Face(0, 0, 100, 100, (40, 40), (60, 40), (50, 60))), "eyes a fifth of the box apart are frontal");
        Check(!vision.Frontal(vision.Face(0, 0, 100, 100, (41, 40), (60, 40), (50, 60))), "eyes closer than a fifth of the box are not");
        Check(!vision.Frontal(vision.Face(0, 0, 100, 100, (30, 40), (70, 40), (75, 60))), "a nose right of both eyes is not");
        Check(!vision.Frontal(vision.Face(0, 0, 100, 100, (30, 40), (70, 40), (25, 60))), "a nose left of both eyes is not");
        Check(vision.Frontal(vision.Face(0, 0, 100, 100, (70, 40), (30, 40), (50, 60))), "the eyes' order across the picture does not matter");
        Console.WriteLine("PASS vision: a face is frontal when its eyes are at least a fifth of its box apart and its nose tip is between them across the picture");
    }

    static async Task TheTurnIsHowFarTheNoseLiesAlongTheEyes()
    {
        await using var vision = new Loaded(Committed());
        object Face((float X, float Y) right, (float X, float Y) left, (float X, float Y) nose) => vision.Face(0, 0, 100, 100, right, left, nose);
        void Turned(object face, float turn, bool frontal, string what)
        {
            var (measured, rule) = (vision.Turn(face), vision.Frontal(face));
            Check(MathF.Abs(measured - turn) < 1e-6f && rule == frontal, $"{what}: turned {turn}, {(frontal ? "" : "not ")}frontal; measured {measured}, {(rule ? "" : "not ")}frontal");
        }
        // Eyes 40 apart, level: the nose's offset from their midpoint, along them, in eye distances.
        Turned(Face((30, 40), (70, 40), (50, 60)), 0, true, "straight on");
        Turned(Face((30, 40), (70, 40), (60, 60)), 0.25f, true, "a quarter of the eye distance towards the left eye");
        Turned(Face((30, 40), (70, 40), (40, 60)), 0.25f, true, "and towards the right eye");
        Turned(Face((30, 40), (70, 40), (72, 60)), 0.55f, false, "a three-quarter view past the left eye");
        Turned(Face((30, 40), (70, 40), (28, 60)), 0.55f, false, "and past the right eye");
        Turned(Face((48, 40), (52, 40), (75, 60)), 6.25f, false, "a profile: the eyes close together, the nose far beside them");
        // A head tilted 45°: along the line between the eyes, not across the picture.
        Turned(Face((30, 30), (60, 60), (35, 55)), 0, true, "a tilted head looking straight on");
        Turned(Face((30, 30), (60, 60), (25, 60)), 1f / 12, false, "a tilted head the frontal rule turns away, hardly turned");
        Turned(Face((30, 30), (60, 60), (47, 67)), 0.4f, true, "a tilted head turned 0.4");
        // The eyes in the other order across the picture, and upside down.
        Turned(Face((70, 40), (30, 40), (60, 60)), 0.25f, true, "the eyes' order across the picture");
        Turned(Face((70, 60), (30, 60), (60, 40)), 0.25f, true, "an upside-down face");
        Check(vision.Turn(Face((50, 40), (50, 40), (50, 60))) == float.PositiveInfinity, "eyes in one place: infinitely turned");
        Console.WriteLine("PASS vision: a face's turn is how far its nose tip lies from the midpoint of its eyes along the line between them, in eye distances, whichever way it is turned, tilted or upside down");
    }

    /// <summary>A <see cref="Loaded"/> of <see cref="LocalSource"/> on a server of its own, set up: its detector is the one it owns.</summary>
    internal sealed class SetUp : IAsyncDisposable
    {
        public required LoopbackServer Server { get; init; }
        public required Loaded Vision { get; init; }
        public IDisposable Detector => Vision.Owned().Single();

        public static async Task<SetUp> Start()
        {
            var server = LoopbackServer.Start();
            var vision = new Loaded(LocalSource(server));
            await vision.Setup();
            Check(vision.Owned().Length == 1, "the setup owns one detector");
            Check(vision.Logs().Contains($"Face detection ready ({Rid})"), "the setup says it is ready: " + string.Join(" | ", vision.Logs()));
            return new SetUp { Server = server, Vision = vision };
        }

        public async ValueTask DisposeAsync()
        {
            await Vision.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    static SharpImage Photo(string name) => SharpImage.Load(File.ReadAllBytes(Fixture(name)));

    internal static bool Greenish(ReadOnlySpan<byte> rgb, int at) => rgb[at + 1] >= 120 && rgb[at + 1] >= rgb[at] + 60 && rgb[at + 1] >= rgb[at + 2] + 60;

    static async Task FindsTheFacesOfTheFixture()
    {
        await using var rig = await SetUp.Start();
        var group = Photo("group.jpg");
        var faces = Items(Loaded.Faces(rig.Detector, group, 320));
        Check(faces.Length == 7, $"group.jpg has 7 faces at 320, not {faces.Length}");
        Check(faces.Zip(faces.Skip(1)).All(pair => Get(pair.First, "Score") >= Get(pair.Second, "Score")), "the faces come best first");

        var drawn = rig.Vision.DetectFaces(group);
        Check(drawn.Drawings == 7 * 7 && group.Drawings == 0, $"Detect faces draws 7 a face on a copy: {drawn.Drawings} drawn, {group.Drawings} on the input");
        // The first box's top edge, across the box: green on the result's JPEG, and nowhere along it on the input.
        var (width, height) = (group.Width, group.Height);
        var before = group.ToPixels();
        var after = SharpImage.Load(drawn.ToJpeg()).ToPixels();
        var first = faces[0];
        int x = (int)MathF.Round(Get(first, "X")), y = (int)MathF.Round(Get(first, "Y")), w = (int)MathF.Round(Get(first, "Width"));
        var row = Math.Clamp(y + 1, 0, height - 1);
        var along = Enumerable.Range(Math.Max(0, x + 2), Math.Max(0, Math.Min(width, x + w - 2) - Math.Max(0, x + 2))).Select(column => (row * width + column) * 3).ToArray();
        int green = along.Count(at => Greenish(after, at)), already = along.Count(at => Greenish(before, at));
        Check(along.Length > 10 && green * 2 >= along.Length && already == 0, $"the first box's top edge is green: {green} of {along.Length} pixels on the result, {already} on the input");
        Console.WriteLine($"PASS vision: group.jpg has 7 faces at 320, best first, and Detect faces draws them on a copy ({green} of {along.Length} pixels of the first top edge green)");
    }

    static async Task FindsThePeopleOfTheFixtures()
    {
        await using var rig = await SetUp.Start();
        foreach (var (name, count) in new[] { ("street.jpg", 4), ("group.jpg", 7) })
        {
            var picture = Photo(name);
            var people = Loaded.People(rig.Detector, picture, 416, 0.45);
            Check(Items(people).Length == count, $"{name} has {count} people at 416, not {Items(people).Length}");
            var copy = picture.Copy();
            Check(ReferenceEquals(rig.Vision.DrawPeople(copy, people), copy) && copy.Drawings == 2 * count && picture.Drawings == 0,
                $"DrawPeople draws 2 a person on the image it is given: {copy.Drawings}");
        }
        Console.WriteLine("PASS vision: street.jpg has 4 people and group.jpg 7 at 416, and DrawPeople draws 2 a person");
    }

    static async Task AYuvFrameFindsTheSame()
    {
        await using var rig = await SetUp.Start();
        foreach (var name in new[] { "group.jpg", "street.jpg" })
        {
            var picture = Photo(name);
            var planes = new byte[picture.PixelsLength(SharpPixelFormat.Yuv420)];
            picture.CopyPixels(planes, SharpPixelFormat.Yuv420);
            using var frame = SharpImage.FromPixels(picture.Width, picture.Height, SharpPixelFormat.Yuv420, planes);
            foreach (var size in new[] { 320, 640 })
            {
                int jpeg = Items(Loaded.Faces(rig.Detector, picture, size)).Length, yuv = Items(Loaded.Faces(rig.Detector, frame, size)).Length;
                Check(jpeg == yuv, $"{name} at {size}: {jpeg} faces in the JPEG, {yuv} in its YUV frame");
            }
            int people = Items(Loaded.People(rig.Detector, picture)).Length, framePeople = Items(Loaded.People(rig.Detector, frame)).Length;
            Check(people == framePeople, $"{name} at 416: {people} people in the JPEG, {framePeople} in its YUV frame");
        }
        Console.WriteLine("PASS vision: a YUV 4:2:0 frame gives the faces at 320 and 640 and the people at 416 its JPEG gives");
    }

    static async Task DrawsOnlyWhatFitsUnderTheDrawingLimit()
    {
        await using var rig = await SetUp.Start();
        var group = Photo("group.jpg");
        var faces = Loaded.Faces(rig.Detector, group);
        var people = Loaded.People(rig.Detector, group);
        SharpImage Crowded()
        {
            var crowded = Photo("group.jpg");
            for (var i = 0; i < 495; i++) crowded.DrawRect(i % 600, 0, 4, 4, SharpColor.White, 1);
            return crowded;
        }
        var detected = rig.Vision.DetectFaces(Crowded());
        Check(detected.Drawings == 495, $"Detect faces on 495 drawings has room for no face of 7 drawings: {detected.Drawings}");
        var drawnFaces = rig.Vision.DrawFaces(Crowded(), faces);
        Check(drawnFaces.Drawings == 495, $"DrawFaces has room for no face of 7 drawings: {drawnFaces.Drawings}");
        var drawnPeople = rig.Vision.DrawPeople(Crowded(), people);
        Check(drawnPeople.Drawings == 499, $"DrawPeople has room for 2 people of 2 drawings: {drawnPeople.Drawings}");
        // With room for some: the best first.
        var roomy = Photo("group.jpg");
        for (var i = 0; i < 480; i++) roomy.DrawRect(0, 0, 4, 4, SharpColor.White, 1);
        Check(rig.Vision.DrawFaces(roomy, faces).Drawings == 480 + 2 * 7, "DrawFaces with room for 2 faces draws 2");
        Console.WriteLine("PASS vision: Detect faces, DrawFaces and DrawPeople draw only what fits under 500 drawings");
    }

    static async Task DisposeWaitsForADetectionAndLaterCallsFail()
    {
        await using var rig = await SetUp.Start();
        var detector = rig.Detector;
        var group = Photo("group.jpg");
        var street = Photo("street.jpg");
        var unexpected = new ConcurrentQueue<Exception>();
        var calls = new int[4];
        var ended = new int[4];
        var threads = Enumerable.Range(0, 4).Select(index => new Thread(() =>
        {
            try
            {
                for (var round = 0; ; round++)
                {
                    if ((index + round) % 2 == 0) Loaded.Faces(detector, index < 2 ? group : street);
                    else Loaded.People(detector, index < 2 ? street : group);
                    Interlocked.Increment(ref calls[index]);
                }
            }
            catch (ObjectDisposedException closed) when (closed.ObjectName == "VisionDetector") { Interlocked.Exchange(ref ended[index], 1); }
            catch (Exception other) { unexpected.Enqueue(other); }
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        await EntityTestHome.Until(() => Enumerable.Range(0, 4).All(i => Volatile.Read(ref calls[i]) >= 3), "every thread has detected three times");
        detector.Dispose();
        foreach (var thread in threads) Check(thread.Join(TimeSpan.FromSeconds(30)), "a thread ends after the detector closed");
        Check(unexpected.IsEmpty, "nothing but the closed sentence: " + string.Join(" | ", unexpected));
        Check(ended.All(e => e == 1), "every thread ended on the closed sentence");

        IsClosed(Throws<ObjectDisposedException>(() => Loaded.Faces(detector, group), "Faces after Dispose"), "Faces after Dispose");
        IsClosed(Throws<ObjectDisposedException>(() => Loaded.People(detector, group), "People after Dispose"), "People after Dispose");
        Check(Throws<InvalidOperationException>(() => rig.Vision.DetectFaces(group), "Detect faces after Dispose").Message == Stopped, "Detect faces says it has stopped");
        detector.Dispose();
        IsClosed(Throws<ObjectDisposedException>(() => Loaded.Faces(detector, group), "Faces after a second Dispose"), "Faces after a second Dispose");
        Check(Throws<InvalidOperationException>(() => rig.Vision.DetectFaces(group), "Detect faces after a second Dispose").Message == Stopped, "Detect faces still says it has stopped");
        Console.WriteLine($"PASS vision: the detector closed under 4 detecting threads ({calls.Sum()} calls) ends each on the closed sentence, Detect faces then says it has stopped, and a second Dispose does nothing");
    }

    static async Task TheProfileOfTheFixtureIsNotFrontal()
    {
        await using var rig = await SetUp.Start();
        var faces = Items(Loaded.Faces(rig.Detector, Photo("group.jpg"), 640));
        Check(faces.Length == 8, $"group.jpg has 8 faces at 640, the profile among them, not {faces.Length}");
        static (float X, float Y) Point(object face, string landmark)
        {
            var point = face.GetType().GetProperty(landmark)!.GetValue(face)!;
            return (Get(point, "X"), Get(point, "Y"));
        }
        foreach (var face in faces)
        {
            var (right, left) = (Point(face, "RightEye"), Point(face, "LeftEye"));
            var distance = MathF.Sqrt((left.X - right.X) * (left.X - right.X) + (left.Y - right.Y) * (left.Y - right.Y));
            Check(MathF.Abs(rig.Vision.EyeDistance(face) - distance) < 1e-4f, $"the eye distance is the eyes' distance: {rig.Vision.EyeDistance(face)}, not {distance}");
        }
        // The profile, looking to the right of the picture, is the leftmost face.
        var profile = faces.MinBy(face => Get(face, "X"))!;
        var notFrontal = faces.Where(face => !rig.Vision.Frontal(face)).ToArray();
        string Described(object face)
        {
            var (right, left, nose) = (Point(face, "RightEye"), Point(face, "LeftEye"), Point(face, "NoseTip"));
            var (low, high) = (MathF.Min(right.X, left.X), MathF.Max(right.X, left.X));
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"({Get(face, "X"):0},{Get(face, "Y"):0}) {Get(face, "Width"):0} wide: eyes {rig.Vision.EyeDistance(face):0.0} apart ({rig.Vision.EyeDistance(face) / Get(face, "Width"):0.00} of the width), nose {(high > low ? (nose.X - low) / (high - low) : float.NaN):0.00} of the way between them, turned {rig.Vision.Turn(face):0.00}, {(rig.Vision.Frontal(face) ? "frontal" : "not frontal")}");
        }
        Check(notFrontal.Length == 1 && ReferenceEquals(notFrontal[0], profile), "only the profile is not frontal: " + string.Join(" | ", faces.Select(Described)));
        Check(rig.Vision.Turn(profile) > 1 && faces.Where(face => !ReferenceEquals(face, profile)).All(face => rig.Vision.Turn(face) < 0.5f),
            "the profile is turned more than 1, the others less than 0.5: " + string.Join(" | ", faces.Select(Described)));
        Console.WriteLine("PASS vision: of group.jpg's 8 faces at 640, the profile is not frontal and the other 7 are: " + string.Join(" | ", faces.Select(Described)));
    }

    static async Task DrawsANameInPlaceOfTheScore()
    {
        await using var rig = await SetUp.Start();
        var group = Photo("group.jpg");
        var found = Loaded.Faces(rig.Detector, group);
        var faces = Items(found);
        Check(faces.Length == 7, $"group.jpg has 7 faces at 320, not {faces.Length}");
        var plain = rig.Vision.DrawFaces(group.Copy(), found).ToPixels();

        // Without a name, a face's score is drawn, as DrawFaces draws it; a blank name is none.
        var unnamed = rig.Vision.DrawFaces(group.Copy(), found, [null, "", " ", null, null, null, null]);
        Check(unnamed.Drawings == 7 * 7 && unnamed.ToPixels().AsSpan().SequenceEqual(plain), $"without names, DrawFaces draws what it draws without them: {unnamed.Drawings} drawings");

        // With names, each in place of its face's score: the picture changes only where those two labels are.
        string?[] names = ["Ana", null, null, "Marko", null, null, null];
        var named = rig.Vision.DrawFaces(group.Copy(), found, names);
        Check(named.Drawings == 7 * 7, $"a named face is drawn with 7 drawings too: {named.Drawings}");
        var pixels = named.ToPixels();
        var width = group.Width;
        bool InLabel(int at, int face)
        {
            int column = at / 3 % width, row = at / 3 / width;
            int x = (int)MathF.Round(Get(faces[face], "X")), y = (int)MathF.Round(Get(faces[face], "Y"));
            return column >= x - 2 && row >= Math.Max(0, y - 20) - 2 && row <= y + 4;
        }
        var changed = Enumerable.Range(0, pixels.Length).Where(at => pixels[at] != plain[at]).ToArray();
        Check(changed.Any(at => InLabel(at, 0)) && changed.Any(at => InLabel(at, 3)), "both names change their labels");
        Check(changed.All(at => InLabel(at, 0) || InLabel(at, 3)), $"nothing but the two labels changes: {changed.Count(at => !InLabel(at, 0) && !InLabel(at, 3))} bytes elsewhere");

        // A name goes with its face, in whatever order the faces are given.
        var reversed = Array.CreateInstance(faces[0].GetType(), faces.Length);
        for (var i = 0; i < faces.Length; i++) reversed.SetValue(faces[faces.Length - 1 - i], i);
        Check(rig.Vision.DrawFaces(group.Copy(), reversed, [.. names.Reverse()]).ToPixels().AsSpan().SequenceEqual(pixels), "the faces and names reversed draw the same");

        var refused = Throws<ArgumentException>(() => rig.Vision.DrawFaces(group.Copy(), found, ["Ana"]), "one name for seven faces");
        Check(refused.Message == "There is one name, or null, for each face", "the names' sentence: " + refused.Message);
        Console.WriteLine($"PASS vision: DrawFaces with names draws a face's name in place of its score, and its score without one ({changed.Length} bytes of two labels changed)");
    }

    // The People detector's camera options: a face's box, with its score above it, and its five points are each drawn only
    // when asked, and a name is drawn where it is above the box, also without the box.
    static async Task DrawsTheBoxesAndThePointsEachOnlyWhenAsked()
    {
        await using var vision = new Loaded(Committed());
        // Three faces on the group photo, side by side and far enough apart that no drawing of one is on another's.
        var group = Photo("group.jpg");
        object[] faces = [.. new[] { 40, 270, 500 }.Select(x => vision.Face(x, 150, 100, 120, (x + 30, 190), (x + 70, 190), (x + 50, 215)))];
        var found = Array.CreateInstance(faces[0].GetType(), faces.Length);
        for (var i = 0; i < faces.Length; i++) found.SetValue(faces[i], i);
        var width = group.Width;
        var frame = group.ToPixels();
        var full = vision.DrawFaces(group.Copy(), found).ToPixels();
        byte[] Drawn(string?[]? names, bool boxes, bool points, int drawings)
        {
            var image = vision.DrawFaces(group.Copy(), found, names, boxes, points);
            Check(image.Drawings == drawings, $"boxes {boxes}, points {points}, {(names == null ? "no names" : "names")}: {drawings} drawings, not {image.Drawings}");
            return image.ToPixels();
        }
        static bool Same(byte[] one, byte[] other, int at) => one[at] == other[at] && one[at + 1] == other[at + 1] && one[at + 2] == other[at + 2];
        int[] Pixels() => [.. Enumerable.Range(0, frame.Length / 3).Select(pixel => pixel * 3)];

        // Each face's five dots, as far as their radius and a pixel more for their edge, and their centres.
        var dots = faces.SelectMany(face => new[] { "RightEye", "LeftEye", "NoseTip", "RightMouthCorner", "LeftMouthCorner" }.Select(landmark =>
        {
            var point = face.GetType().GetProperty(landmark)!.GetValue(face)!;
            return (X: (int)MathF.Round(Get(point, "X")), Y: (int)MathF.Round(Get(point, "Y")), Radius: Math.Clamp((int)MathF.Round(Get(face, "Width") / 40), 2, 4096) + 1);
        })).ToArray();
        bool InDot(int at)
        {
            int column = at / 3 % width, row = at / 3 / width;
            return dots.Any(dot => (column - dot.X) * (column - dot.X) + (row - dot.Y) * (row - dot.Y) <= dot.Radius * dot.Radius);
        }
        int[] centres = [.. dots.Select(dot => (dot.Y * width + dot.X) * 3)];
        // Each box's top edge, inside its corners.
        int[] edges = [.. faces.SelectMany(face =>
        {
            int x = (int)MathF.Round(Get(face, "X")), y = (int)MathF.Round(Get(face, "Y")), w = (int)MathF.Round(Get(face, "Width"));
            return Enumerable.Range(Math.Max(0, x + 2), Math.Max(0, Math.Min(width, x + w - 2) - Math.Max(0, x + 2))).Select(column => (Math.Max(0, y + 1) * width + column) * 3);
        })];
        Check(centres.All(at => !Same(full, frame, at)) && edges.Count(at => Greenish(full, at)) * 2 >= edges.Length,
            "drawn whole, every dot's centre is coloured and the boxes' top edges are green");

        // Both on: as DrawFaces draws them, with names or without.
        Check(Drawn(null, boxes: true, points: true, 3 * 7).AsSpan().SequenceEqual(full), "with boxes and points, DrawFaces draws what it draws without the options");
        string?[] names = ["Ana", null, "Marko"];
        var namedFull = vision.DrawFaces(group.Copy(), found, names).ToPixels();
        Check(Drawn(names, boxes: true, points: true, 3 * 7).AsSpan().SequenceEqual(namedFull), "with boxes, points and names, DrawFaces draws what it draws with the names alone");

        // Points off: the boxes and their scores, and no dot.
        var boxes = Drawn(null, boxes: true, points: false, 3 * 2);
        Check(centres.All(at => Same(boxes, frame, at)), "with points off, no dot's centre is coloured");
        Check(edges.All(at => Same(boxes, full, at)), "with points off, the boxes' top edges are as drawn whole");
        Check(Pixels().All(at => Same(boxes, InDot(at) ? frame : full, at)), "with points off, the picture is the one drawn whole but where the dots are, which are the frame's");

        // Boxes off: the dots, and no box nor score.
        var points = Drawn(null, boxes: false, points: true, 3 * 5);
        Check(centres.All(at => Same(points, full, at)), "with boxes off, each dot's centre is as drawn whole");
        Check(edges.All(at => Same(points, frame, at)), "with boxes off, the boxes' top edges are the frame's");
        Check(Pixels().All(at => Same(points, InDot(at) ? full : frame, at)), "with boxes off, the picture is the frame but where the dots are, which are as drawn whole");

        // Both off: the frame as it was.
        Check(Drawn(null, boxes: false, points: false, 0).AsSpan().SequenceEqual(frame), "with both off, the picture is the frame");

        // Both off with names: the frame with each name where it is drawn above its box, and nothing else.
        var labelled = group.Copy();
        foreach (var index in new[] { 0, 2 })
            labelled.DrawText(names[index]!, (int)MathF.Round(Get(faces[index], "X")), Math.Max(0, (int)MathF.Round(Get(faces[index], "Y")) - 20), SharpColor.White, 16, background: true);
        var bare = Drawn(names, boxes: false, points: false, 2);
        Check(bare.AsSpan().SequenceEqual(labelled.ToPixels()) && !bare.AsSpan().SequenceEqual(frame), "with both off, the frame has the two names, each where it is above its box, and nothing else");
        var dotted = Drawn(names, boxes: false, points: true, 3 * 5 + 2);
        Check(Pixels().All(at => Same(dotted, InDot(at) ? full : bare, at)), "with boxes off, the names are drawn with the dots");

        // As many as fit under the 500 drawings, the best first: a face is 2 drawings without its dots, 5 without its box.
        SharpImage Crowded()
        {
            var crowded = Photo("group.jpg");
            for (var i = 0; i < 495; i++) crowded.DrawRect(i % 600, 0, 4, 4, SharpColor.White, 1);
            return crowded;
        }
        Check(vision.DrawFaces(Crowded(), found, null, true, false).Drawings == 499, "points off, 495 drawings have room for 2 faces");
        Check(vision.DrawFaces(Crowded(), found, null, false, true).Drawings == 500, "boxes off, 495 drawings have room for 1 face");
        Check(vision.DrawFaces(Crowded(), found, names, false, false).Drawings == 497, "both off, 495 drawings have room for the 2 names");
        var refused = Throws<ArgumentException>(() => vision.DrawFaces(group.Copy(), found, ["Ana"], false, false), "one name for three faces");
        Check(refused.Message == "There is one name, or null, for each face", "the names' sentence: " + refused.Message);
        Console.WriteLine("PASS vision: DrawFaces draws each face's box, with its score, and its five points each only when asked, and a name above where the box is or would be");
    }

    static async Task EmbedsAFaceOfTheFixture()
    {
        await using var server = LoopbackServer.Start();
        await using var vision = new Loaded(LocalRecognizerSource(server));
        using var detector = await vision.Open();
        var group = Photo("group.jpg");
        var faces = Items(Loaded.Faces(detector, group, 640));
        using var recognizer = await vision.OpenRecognizer();
        Check(recognizer.GetType().GetInterfaces().Any(i => i.Name == "IFaceRecognizer"), "the recogniser is an IFaceRecognizer");
        static (float[] Values, float Fit) Parts(object embedding) =>
            ((float[])embedding.GetType().GetProperty("Values")!.GetValue(embedding)!, (float)embedding.GetType().GetProperty("Fit")!.GetValue(embedding)!);

        var first = Loaded.Embed(recognizer, group, faces[0]);
        var (values, fit) = Parts(first);
        var length = MathF.Sqrt(values.Sum(v => v * v));
        Check(values.Length == 128 && MathF.Abs(length - 1) < 1e-4f, $"128 values of length 1: {values.Length} of length {length}");
        Check(float.IsFinite(fit) && fit >= 0, $"the fit is a distance: {fit}");
        var again = Loaded.Embed(recognizer, group, faces[0]);
        Check(Parts(again).Values.AsSpan().SequenceEqual(values) && Parts(again).Fit == fit, "the same face gives the same embedding twice");
        Check(MathF.Abs(Loaded.Similarity(first, again) - 1) < 1e-5f, "an embedding is 1 like itself");
        var other = Loaded.Similarity(first, Loaded.Embed(recognizer, group, faces[1]));
        Check(other < 0.5f, $"another person of group.jpg is less alike than a match: {other}");

        // A second recogniser uses the model already downloaded, and gives the same.
        using var second = await vision.OpenRecognizer();
        Check(server.Count("/" + ModelName) == 1, $"the model was requested once: {server.Count("/" + ModelName)}");
        Check(Parts(Loaded.Embed(second, group, faces[0])).Values.AsSpan().SequenceEqual(values), "a second recogniser gives the same embedding");
        Console.WriteLine($"PASS vision: the recogniser turns a face of group.jpg into 128 values of length 1 (fit {fit:0.00}), the same twice and from a second recogniser, and another person's is {other:0.00} like it");
    }

    static async Task EmbedAfterDisposeFails()
    {
        await using var server = LoopbackServer.Start();
        await using var vision = new Loaded(LocalRecognizerSource(server));
        var recognizer = await vision.OpenRecognizer();
        var image = SharpImage.Blank(64, 48);
        var face = vision.Face(10, 10, 40, 30, (20, 20), (40, 20), (30, 28));
        recognizer.Dispose();
        void IsRecogniserClosed(ObjectDisposedException closed, string what) =>
            Check(closed.ObjectName == "VisionRecognizer" && closed.Message.StartsWith("The vision recogniser has been closed", StringComparison.Ordinal), $"{what} gives the closed sentence: {closed.Message}");
        IsRecogniserClosed(Throws<ObjectDisposedException>(() => Loaded.Embed(recognizer, image, face), "Embed after Dispose"), "Embed after Dispose");
        recognizer.Dispose();
        IsRecogniserClosed(Throws<ObjectDisposedException>(() => Loaded.Embed(recognizer, image, face), "Embed after a second Dispose"), "Embed after a second Dispose");
        Console.WriteLine("PASS vision: Embed after the recogniser closed gives the closed sentence, and a second Dispose does nothing");
    }

    // ---- End to end: saved as library vision, run by an entity definition ----

    static async Task DetectFacesRunsInAnEntityDefinition()
    {
        await using var server = LoopbackServer.Start();
        await using var home = new EntityTestHome("vision");
        var api = new HassSharpManager(home.Root);
        try
        {
            await api.Init();
            async Task<JsonObject> Request(Func<string, Task<string>> request, object body)
            {
                var answer = JsonNode.Parse(await request(JsonSerializer.Serialize(body)))!;
                Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString());
                return answer["result"]!.AsObject();
            }
            var vision = LocalSource(server);
            var fixture = """
                public static class FixturePictures
                {
                    /// <summary>The group photo.</summary>
                    [Node("Fixture photo", Pure = true)] public static SharpImage Photo() => SharpImage.Load(System.IO.File.ReadAllBytes(PATH));
                    /// <summary>How many drawings a picture carries.</summary>
                    [Node("Drawings", Pure = true)] public static int Drawings(SharpImage image) => image.Drawings;
                }
                """.Replace("PATH", JsonSerializer.Serialize(Fixture("group.jpg")));
            foreach (var (id, name, source) in new[] { ("vision", "Vision.cs", vision), ("fixture", "Fixture.cs", fixture) })
            {
                var saved = await Request(api.NodeLibraryRequest, new { operation = "save", id, name, expectedRevision = 0, source });
                Check(saved["saved"]!.GetValue<bool>(), $"library {id} saves: " + saved.ToJsonString());
            }
            var snapshot = new NodeLibraryCompiler(VisualExecutionTests.Diagnostics).Preview([new NodeLibrarySource("vision", vision, 1), new NodeLibrarySource("fixture", fixture, 1)]).Snapshot;
            Check(snapshot.Methods.ContainsKey(DetectFacesKind), "Detect faces is " + DetectFacesKind + ": " + string.Join(", ", snapshot.Methods.Keys));
            string Kind(string method) => "library:fixture:" + Uri.EscapeDataString("global::NodeLib.FixturePictures." + method);
            JsonObject Data(string source, string target, string targetPort, string sourcePort = "result") =>
                new() { ["id"] = $"{source}.{sourcePort}:{target}.{targetPort}", ["kind"] = "data", ["source"] = source, ["target"] = target, ["sourcePort"] = sourcePort, ["targetPort"] = targetPort };
            JsonObject[] nodes =
            [
                VisualExecutionTests.Hub("entity_drawn", "drawn", "sensor"), VisualExecutionTests.N("setup", "entity_setup"),
                VisualGraphContract.CreateNode(Kind("Photo"), "photo", snapshot), VisualGraphContract.CreateNode(DetectFacesKind, "detect", snapshot),
                VisualGraphContract.CreateNode(Kind("Drawings"), "drawings", snapshot), VisualExecutionTests.N("set", "set_entity_state", new JsonObject { ["entity"] = "drawn" }),
            ];
            JsonObject[] edges =
            [
                new() { ["id"] = "setup:next:set", ["source"] = "setup", ["target"] = "set", ["port"] = "next" },
                Data("photo", "detect", "image"), Data("detect", "drawings", "image"), Data("drawings", "set", "value"),
            ];
            JsonObject Definition() => new()
            {
                ["name"] = "Faces", ["settings"] = new JsonArray(),
                ["logic"] = new JsonObject { ["kind"] = "graph", ["graph"] = new JsonObject { ["version"] = 5, ["name"] = "Faces", ["nodes"] = new JsonArray([.. nodes.Select(n => n.DeepClone())]), ["edges"] = new JsonArray([.. edges.Select(e => e.DeepClone())]) } },
            };
            string? Drawn(int from) => home.Snapshots(from).LastOrDefault(s => s["uniqueId"]!.GetValue<string>() == "faces:drawn" && s["available"]!.GetValue<bool>())?["values"]?["value"]?.ToJsonString();
            string Lines() => string.Join(" | ", home.Logs());
            int Count(string line) => home.Logs(PyLogLevel.Info).Count(l => l == line);
            var downloading = $"Faces: Downloading libhs_vision-{Rid}.so from 127.0.0.1";
            var ready = $"Faces: Face detection ready ({Rid})";

            var first = await Request(api.EntityRequest, new { operation = "save", id = "faces", expectedRevision = 0, definition = Definition() });
            Check(first["active"]!.GetValue<bool>() && first["document"]!["status"]!.GetValue<string>() == "running", "the definition runs: " + first.ToJsonString() + " | " + Lines());
            await EntityTestHome.Until(() => Drawn(0) != null, "the When started flow sets sensor drawn");
            Check(Drawn(0) == (7 * 7).ToString(), $"sensor drawn is 49: {Drawn(0)}");
            Check(Count(downloading) == 1 && Count(ready) == 1 && server.Count($"/libhs_vision-{Rid}.so") == 1, "downloaded once, then ready: " + Lines());

            // Saved again: the new revision sets up from the file it has, without a request.
            var pushed = home.Pushes().Length;
            var second = await Request(api.EntityRequest, new { operation = "save", id = "faces", expectedRevision = 1, definition = Definition() });
            Check(second["active"]!.GetValue<bool>(), "the new revision runs: " + second.ToJsonString());
            await EntityTestHome.Until(() => Count(ready) == 2 && Drawn(pushed) != null, "the new revision sets up and sets sensor drawn");
            Check(Drawn(pushed) == (7 * 7).ToString() && Count(downloading) == 1 && server.Count($"/libhs_vision-{Rid}.so") == 1,
                $"the new revision drew 49 again from the file it had: {Drawn(pushed)} | " + Lines());
        }
        finally { await api.UnloadAsync(); }
        Console.WriteLine("PASS vision: saved as library vision, Detect faces in an entity definition draws 7 faces of group.jpg; a resave sets up again without downloading");
    }
}

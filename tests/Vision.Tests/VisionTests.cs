using System.Collections.Concurrent;
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
/// <c>HS_VISION_REQUIRE_LIBRARY=1</c>.
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

    /// <summary><c>nodes/Vision.cs</c> as committed.</summary>
    static string Committed() => File.ReadAllText(Path.Combine(Root, "nodes", "Vision.cs"));

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

    [RuntimeSuite("--vision", Order = 41)]
    public static async Task Run()
    {
        var tests = new List<(string, Func<Task>)>
        {
            (nameof(UnsupportedPlatformFailsClearly), UnsupportedPlatformFailsClearly),
            (nameof(AWrongChecksumNeverLoads), AWrongChecksumNeverLoads),
            (nameof(NotSetUpThrows), NotSetUpThrows),
            (nameof(CompilesAsLibraryVision), CompilesAsLibraryVision),
        };
        var failures = new List<string>();
        if (LocalLibrary != null)
            tests.AddRange([
                (nameof(FindsTheFacesOfTheFixture), FindsTheFacesOfTheFixture),
                (nameof(FindsThePeopleOfTheFixtures), FindsThePeopleOfTheFixtures),
                (nameof(AYuvFrameFindsTheSame), AYuvFrameFindsTheSame),
                (nameof(DrawsOnlyWhatFitsUnderTheDrawingLimit), DrawsOnlyWhatFitsUnderTheDrawingLimit),
                (nameof(DisposeWaitsForADetectionAndLaterCallsFail), DisposeWaitsForADetectionAndLaterCallsFail),
                (nameof(DetectFacesRunsInAnEntityDefinition), DetectFacesRunsInAnEntityDefinition),
            ]);
        else if (Environment.GetEnvironmentVariable("HS_VISION_REQUIRE_LIBRARY") == "1")
            failures.Add($"an hs_vision library is required (HS_VISION_REQUIRE_LIBRARY=1) and there is none for {Rid}");
        else
            Console.WriteLine($"SKIP vision: no hs_vision library for {Rid}");
        foreach (var (name, test) in tests)
            try { await test(); }
            catch (Exception e) { failures.Add($"{name}: {e}"); }
        if (failures.Count > 0) throw new Exception($"Vision: {failures.Count} failing:\n" + string.Join("\n", failures));
        Console.WriteLine(LocalLibrary != null
            ? $"PASS vision: Vision.cs downloads hs_vision pinned by checksum, finds and draws faces and people, and Detect faces runs in an entity definition ({LocalLibrary})"
            : "PASS vision: Vision.cs refuses an unsupported platform and a wrong checksum, and Detect faces refuses to run unset up");
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
    sealed class Loaded : IAsyncDisposable
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

        public Task Setup() => (Task)Invoke(detection.GetMethod("Setup")!, null, Setups)!;

        public SharpImage DetectFaces(SharpImage image, double minScore = 0.7, int inputSize = 320) =>
            (SharpImage)Invoke(detection.GetMethod("DetectFaces")!, null, image, minScore, inputSize)!;

        /// <summary>The detector's faces: the list it gives, to pass on to <see cref="DrawFaces"/>.</summary>
        public static object Faces(IDisposable detector, SharpImage image, int inputSize = 320, double minScore = 0.7) =>
            Invoke(detector.GetType().GetMethod("Faces")!, detector, image, inputSize, minScore)!;

        public static object People(IDisposable detector, SharpImage image, int inputSize = 416, double minScore = 0.45) =>
            Invoke(detector.GetType().GetMethod("People")!, detector, image, inputSize, minScore)!;

        public SharpImage DrawFaces(SharpImage image, object faces) => (SharpImage)Invoke(vision.GetMethod("DrawFaces")!, null, image, faces)!;
        public SharpImage DrawPeople(SharpImage image, object people) => (SharpImage)Invoke(vision.GetMethod("DrawPeople")!, null, image, people)!;

        public ValueTask DisposeAsync()
        {
            foreach (var disposable in Owned().Reverse()) disposable.Dispose();
            context.Unload();
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
            return ValueTask.CompletedTask;
        }
    }

    static object[] Items(object list) => [.. (IEnumerable<object>)list];
    static float Get(object item, string property) => (float)item.GetType().GetProperty(property)!.GetValue(item)!;

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

    /// <summary>A <see cref="Loaded"/> of <see cref="LocalSource"/> on a server of its own, set up: its detector is the one it owns.</summary>
    sealed class SetUp : IAsyncDisposable
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

    static bool Greenish(ReadOnlySpan<byte> rgb, int at) => rgb[at + 1] >= 120 && rgb[at + 1] >= rgb[at] + 60 && rgb[at + 1] >= rgb[at + 2] + 60;

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

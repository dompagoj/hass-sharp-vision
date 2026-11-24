using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using HassSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// <c>nodes/People.cs</c>, compiled with <c>nodes/Vision.cs</c> as the library compiler builds them (one assembly, each
/// wrapped in <c>NodeLib</c>), over real device data: shared stores under a temporary directory, opened as two detectors
/// in two definitions open <c>Shared("hass-sharp-vision.people")</c>. The embeddings are made up: unit vectors built from
/// a seed, and vectors at a chosen cosine from another. No native library is needed.
/// </summary>
static class PeopleTests
{
    const string StoreName = "hass-sharp-vision.people";
    const string Camera = "camera.front_door";
    const string LastNamed = "The last sample a person was named with cannot be removed; delete the person instead.";

    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    // ---- People.cs and Vision.cs, compiled together ----

    static readonly Lazy<Assembly> library = new(Compile);

    /// <summary>
    /// <c>Vision.cs</c> and <c>People.cs</c> compiled into one assembly as the library compiler does (each wrapped in
    /// <c>NodeLib</c>, with the global usings, the framework's references and unsafe code), without a warning, and loaded
    /// into a load context of their own.
    /// </summary>
    static Assembly Compile()
    {
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = new[] { "Vision.cs", "People.cs" }
            .Select(file => CSharpSyntaxTree.ParseText(NodeLibraryCompiler.WrapSource(File.ReadAllText(Path.Combine(VisionTests.Root, "nodes", file))), parse, path: file))
            .Append(CSharpSyntaxTree.ParseText(DiagnosticsProvider.GlobalUsings, parse));
        var compilation = CSharpCompilation.Create("People" + Guid.NewGuid().ToString("N"), trees, NodeLibraryCompiler.FrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        var problems = emitted.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).ToArray();
        Check(emitted.Success && problems.Length == 0, "People.cs compiles with Vision.cs without a warning: " + string.Join(" | ", problems.Select(d => d.ToString())));
        image.Position = 0;
        return new AssemblyLoadContext("people-tests").LoadFromStream(image);
    }

    internal static Type Type(string name) => library.Value.GetType("NodeLib." + name, true)!;

    static object? Invoke(MethodInfo method, object? target, params object?[] arguments) =>
        method.Invoke(target, BindingFlags.DoNotWrapExceptions, null, arguments, null);

    internal static dynamic Embedding(float[] values, float fit = 1.5f) => Activator.CreateInstance(Type("FaceEmbedding"), values, fit)!;

    internal static dynamic NewSample(float[] values, byte[] crop, DateTimeOffset at, string camera = Camera, float fit = 1.5f) =>
        Activator.CreateInstance(Type("NewSample"), (object)Embedding(values, fit), crop, at, camera)!;

    /// <summary>An array of <c>NewSample</c>s, which a <c>PeopleList</c> takes as its <c>IEnumerable&lt;NewSample&gt;</c>.</summary>
    internal static dynamic Samples(params object[] samples)
    {
        var array = Array.CreateInstance(Type("NewSample"), samples.Length);
        for (var i = 0; i < samples.Length; i++) array.SetValue(samples[i], i);
        return array;
    }

    /// <summary>A person of made-up samples, all named, for the matcher.</summary>
    static dynamic Person(string id, params float[][] samples)
    {
        var array = Array.CreateInstance(Type("FaceSample"), samples.Length);
        for (var i = 0; i < samples.Length; i++)
            array.SetValue(Activator.CreateInstance(Type("FaceSample"), $"{id}s{i}", (object)Embedding(samples[i]), DateTimeOffset.UnixEpoch, Camera, false), i);
        return Activator.CreateInstance(Type("Person"), id, id, DateTimeOffset.UnixEpoch, array, null, null)!;
    }

    static dynamic People(params object[] people)
    {
        var array = Array.CreateInstance(Type("Person"), people.Length);
        for (var i = 0; i < people.Length; i++) array.SetValue(people[i], i);
        return array;
    }

    internal static dynamic DefaultRules => Type("RecognitionRules").GetProperty("Default")!.GetValue(null)!;

    static dynamic Rules(double matchScore, double matchMargin = 0.10, double suggestScore = 0.40) =>
        Activator.CreateInstance(Type("RecognitionRules"), matchScore, matchMargin, suggestScore, 0.60, 0.15, 16.0, 24.0, 7.0)!;

    static dynamic Match(float[] face, object people, object? rules = null) =>
        Invoke(Type("FaceMatcher").GetMethod("Match")!, null, (object)Embedding(face), people, rules ?? DefaultRules)!;

    /// <summary>A <c>PeopleList</c> over <paramref name="data"/>; <paramref name="changed"/> runs after it reloads for another's change.</summary>
    internal static dynamic List(SharpData data, Func<Task>? changed = null) => Activator.CreateInstance(Type("PeopleList"), data, changed)!;

    /// <summary>
    /// The items of a list of the compiled library's. Called with a dynamic value, as it always is here, it gives a dynamic
    /// one, so each call is cast back to <c>object[]</c>: a lambda cannot go into a call that is bound at run time.
    /// </summary>
    internal static object[] Items(dynamic list) => ((IEnumerable<object>)list).ToArray();

    static dynamic Only(dynamic list, string name) => ((object[])Items(list.People)).Single(p => ((dynamic)p).Name == name);

    static string Refused(Action action, string what)
    {
        try { action(); }
        catch (Exception e) when (e.GetType().Name == "PeopleRefusal") { return e.Message; }
        catch (Exception e) { throw new Exception($"{what}: threw {e.GetType().Name} ({e.Message}), not a PeopleRefusal", e); }
        throw new Exception($"{what}: was not refused");
    }

    static T Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); }
        catch (T expected) { return expected; }
        catch (Exception other) { throw new Exception($"{what}: threw {other.GetType().Name} ({other.Message}), not {typeof(T).Name}", other); }
        throw new Exception($"{what}: did not throw");
    }

    static async Task Until(Func<bool> condition, string what)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Check(clock.Elapsed < TimeSpan.FromSeconds(10), "timed out waiting: " + what);
            await Task.Delay(10);
        }
    }

    // ---- Made-up embeddings and crops ----

    /// <summary>A unit vector of 128 values made from a seed.</summary>
    internal static float[] Unit(int seed)
    {
        var random = new Random(seed);
        var values = new double[128];
        for (var i = 0; i < values.Length; i++) values[i] = random.NextDouble() * 2 - 1;
        return Normalised(values);
    }

    static float[] Normalised(double[] values)
    {
        var length = Math.Sqrt(values.Sum(v => v * v));
        return values.Select(v => (float)(v / length)).ToArray();
    }

    /// <summary>A unit vector whose cosine to <paramref name="from"/>, a unit vector, is <paramref name="cosine"/>.</summary>
    internal static float[] At(float[] from, double cosine, int seed)
    {
        var other = Unit(seed);
        var along = other.Zip(from).Sum(pair => (double)pair.First * pair.Second);
        var across = Normalised(other.Zip(from).Select(pair => pair.First - along * pair.Second).ToArray());
        var sine = Math.Sqrt(1 - cosine * cosine);
        return Normalised(from.Zip(across).Select(pair => cosine * pair.First + sine * pair.Second).ToArray());
    }

    internal static double Cosine(float[] a, float[] b) => a.Zip(b).Sum(pair => pair.First * pair.Second);

    // Each crop carries this marker, which no log line may.
    const string CropMarker = "crop-of-a-face-";

    /// <summary>The bytes of a made-up crop: a JPEG's first bytes, the marker and a number, then bytes from a seed.</summary>
    internal static byte[] Crop(int seed, int length = 600)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        var head = new byte[] { 0xFF, 0xD8, 0xFF }.Concat(Encoding.ASCII.GetBytes(CropMarker + seed)).ToArray();
        head.CopyTo(bytes, 0);
        return bytes;
    }

    static readonly DateTimeOffset T0 = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    /// <summary>A named sample made from a seed: its vector, a crop and a time <paramref name="seed"/> minutes after <see cref="T0"/>.</summary>
    static dynamic Seeded(int seed) => NewSample(Unit(seed), Crop(seed), T0.AddMinutes(seed));

    // ---- The stores ----

    /// <summary>
    /// A temporary directory for the stores' root, removed afterwards, with the one <see cref="DataStores"/> of a running
    /// HassSharp on it: each <see cref="Open"/> is the shared store as another definition's detector opens it.
    /// </summary>
    sealed class Home : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "people-tests-" + Guid.NewGuid().ToString("N"));
        public DataStores Stores { get; }
        public Home() { Stores = new DataStores(Root); }

        /// <summary>The shared store, as the detector of definition <paramref name="definition"/> opens it; its <c>Changed</c> actions run off the writer's thread.</summary>
        public SharpData Open(string definition) => Stores.OpenShared(StoreName, new DataUser(definition, "front_door", action => Task.Run(action)));

        /// <summary>The shared store after a restart: a new <see cref="DataStores"/> on the same root, reading from disk.</summary>
        public SharpData Restarted(string definition = "after") => new DataStores(Root).OpenShared(StoreName, new DataUser(definition, "front_door", action => Task.Run(action)));

        /// <summary>The private store of the detector <c>front_door</c> of definition <c>def-a</c>, as its <c>Data</c>.</summary>
        public SharpData Device() => Stores.OpenDevice(new DataUser("def-a", "front_door", action => Task.Run(action)));

        /// <summary>That detector's private store after a restart, or as its next revision opens it.</summary>
        public SharpData RestartedDevice() => new DataStores(Root).OpenDevice(new DataUser("def-a", "front_door", action => Task.Run(action)));

        /// <summary>The directory of that detector's private store: a file per key.</summary>
        public string DeviceDirectory => Path.Combine(Root, "devices", "def-a", "front_door");

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    // ---- The suite ----

    // Every line logged while the suite runs, which no name and no crop may be in.
    static readonly List<string> logged = [];
    static readonly Lock logging = new();
    // Every name the tests give, which no log line may contain.
    static readonly HashSet<string> names = new(StringComparer.Ordinal);

    static string Named(string name)
    {
        lock (logging) names.Add(name.Trim());
        return name;
    }

    [RuntimeSuite("--people", Order = 43)]
    public static async Task Run()
    {
        var tests = new List<(string, Func<Task>)>
        {
            (nameof(TheRulesAreTheCalibratedDefaults), Sync(TheRulesAreTheCalibratedDefaults)),
            (nameof(TheBestPersonIsRecognisedByScoreAndMargin), Sync(TheBestPersonIsRecognisedByScoreAndMargin)),
            (nameof(AtTheMatchScoreExactlyIsRecognised), Sync(AtTheMatchScoreExactlyIsRecognised)),
            (nameof(TwoPeopleWithinTheMarginAreAMaybe), Sync(TwoPeopleWithinTheMarginAreAMaybe)),
            (nameof(UnderTheSuggestScoreIsUnknown), Sync(UnderTheSuggestScoreIsUnknown)),
            (nameof(APersonScoresByTheirBestSample), Sync(APersonScoresByTheirBestSample)),
            (nameof(AnEmptyListIsUnknown), Sync(AnEmptyListIsUnknown)),
            (nameof(APersonComesBackFromTheStore), Sync(APersonComesBackFromTheStore)),
            (nameof(NamesAreTrimmedAndCheckedIgnoringCase), Sync(NamesAreTrimmedAndCheckedIgnoringCase)),
            (nameof(RenameRefusesATakenName), Sync(RenameRefusesATakenName)),
            (nameof(SamplesAreCappedAndLearnedOnesReplaced), Sync(SamplesAreCappedAndLearnedOnesReplaced)),
            (nameof(ACropOverOneMebibyteIsRefused), Sync(ACropOverOneMebibyteIsRefused)),
            (nameof(The201stPersonIsRefused), Sync(The201stPersonIsRefused)),
            (nameof(MergeMovesSamplesUpToTheCaps), Sync(MergeMovesSamplesUpToTheCaps)),
            (nameof(AnInterruptedMergeLeavesBothReadable), Sync(AnInterruptedMergeLeavesBothReadable)),
            (nameof(TheLastNamedSampleIsKept), Sync(TheLastNamedSampleIsKept)),
            (nameof(DeleteRemovesEveryKey), Sync(DeleteRemovesEveryKey)),
            (nameof(SeenIsWrittenAtMostOnceAMinute), Sync(SeenIsWrittenAtMostOnceAMinute)),
            (nameof(TwoListsAddAtOnceAndLoseNoName), TwoListsAddAtOnceAndLoseNoName),
            (nameof(ChangedReachesTheOtherList), ChangedReachesTheOtherList),
            (nameof(AListReloadsToEmptyAfterAClear), AListReloadsToEmptyAfterAClear),
            (nameof(ANameWithoutItsPersonIsListedWithoutSamples), Sync(ANameWithoutItsPersonIsListedWithoutSamples)),
            (nameof(SamplesNoOneListsAreIgnored), Sync(SamplesNoOneListsAreIgnored)),
            (nameof(AnEmbeddingOfTheWrongLengthIsLeftOut), Sync(AnEmbeddingOfTheWrongLengthIsLeftOut)),
            (nameof(ANewerFormatIsNeitherReadNorWritten), Sync(ANewerFormatIsNeitherReadNorWritten)),
            (nameof(UnreadableNamesAreNeitherReadNorWritten), Sync(UnreadableNamesAreNeitherReadNorWritten)),
            (nameof(AnInterruptedAddLeavesNoFaceData), Sync(AnInterruptedAddLeavesNoFaceData)),
            (nameof(AnInterruptedDeleteLeavesNoFaceData), Sync(AnInterruptedDeleteLeavesNoFaceData)),
            (nameof(AnInterruptedMergeLeavesNoFaceData), Sync(AnInterruptedMergeLeavesNoFaceData)),
            (nameof(AFaceStartsAGroupAndACloseOneJoinsIt), Sync(AFaceStartsAGroupAndACloseOneJoinsIt)),
            (nameof(TwoGroupsAFaceIsCloseToAreJoinedIntoTheOlder), Sync(TwoGroupsAFaceIsCloseToAreJoinedIntoTheOlder)),
            (nameof(AGroupIsShownAtItsThirdSightingAndAFrameCountsOnce), Sync(AGroupIsShownAtItsThirdSightingAndAFrameCountsOnce)),
            (nameof(GroupsExpire), Sync(GroupsExpire)),
            (nameof(The51stGroupDropsTheOneSeenLongestAgo), Sync(The51stGroupDropsTheOneSeenLongestAgo)),
            (nameof(AGroupKeepsFiveSamplesPreferringLargerAndUnlikeFaces), Sync(AGroupKeepsFiveSamplesPreferringLargerAndUnlikeFaces)),
            (nameof(RematchRemovesRecognisedGroupsAndUpdatesSuggestions), Sync(RematchRemovesRecognisedGroupsAndUpdatesSuggestions)),
            (nameof(TheNextRevisionReadsTheSameGroups), Sync(TheNextRevisionReadsTheSameGroups)),
            (nameof(LastSeenAndCountAreWrittenAtMostOnceAMinute), Sync(LastSeenAndCountAreWrittenAtMostOnceAMinute)),
            (nameof(ARemovedGroupLeavesNoFaceData), Sync(ARemovedGroupLeavesNoFaceData)),
            (nameof(WhatAnInterruptionLeavesOfGroupsIsCleanedUp), Sync(WhatAnInterruptionLeavesOfGroupsIsCleanedUp)),
            (nameof(ANewerFormatOfGroupsIsNeitherReadNorWritten), Sync(ANewerFormatOfGroupsIsNeitherReadNorWritten)),
            (nameof(AClearedStoreGetsNoGroupBack), Sync(AClearedStoreGetsNoGroupBack)),
            (nameof(AFaceIsLearnedWhenAllFiveHold), Sync(AFaceIsLearnedWhenAllFiveHold)),
            (nameof(AFaceUnderTheLearnScoreIsNotLearned), Sync(AFaceUnderTheLearnScoreIsNotLearned)),
            (nameof(AFaceUnderTheLearnMarginIsNotLearned), Sync(AFaceUnderTheLearnMarginIsNotLearned)),
            (nameof(AFaceAnchoredOnlyOnALearnedSampleIsNotLearned), Sync(AFaceAnchoredOnlyOnALearnedSampleIsNotLearned)),
            (nameof(ASmallFaceIsNotLearned), Sync(ASmallFaceIsNotLearned)),
            (nameof(AFaceThatAddsNothingIsNotLearned), Sync(AFaceThatAddsNothingIsNotLearned)),
            (nameof(OneLearnedSampleInTenMinutes), Sync(OneLearnedSampleInTenMinutes)),
        };
        var failures = new List<string>();
        var log = PyInterop.Log;
        PyInterop.Log = (level, line) =>
        {
            lock (logging) logged.Add(line);
            log?.Invoke(level, line);
        };
        try
        {
            foreach (var (name, test) in tests)
                try { await test(); }
                catch (Exception e) { failures.Add($"{name}: {e}"); }
        }
        finally { PyInterop.Log = log; }
        try { NoNameOrCropIsLogged(); }
        catch (Exception e) { failures.Add($"{nameof(NoNameOrCropIsLogged)}: {e}"); }
        if (failures.Count > 0) throw new Exception($"People: {failures.Count} failing:\n" + string.Join("\n", failures));
        Console.WriteLine("PASS people: People.cs keeps one shared list of named people with their face samples, matches a face by best sample and margin, groups the faces nobody matches and learns a face it is sure of");
    }

    static Func<Task> Sync(Action test) => () => { test(); return Task.CompletedTask; };

    // ---- The rules and the matcher ----

    static void TheRulesAreTheCalibratedDefaults()
    {
        var rules = DefaultRules;
        Check(rules.MatchScore == 0.50 && rules.MatchMargin == 0.10 && rules.SuggestScore == 0.40 && rules.LearnScore == 0.60 && rules.LearnMargin == 0.15
            && rules.UsableEyes == 16.0 && rules.LearnEyes == 24.0 && rules.FitLimit == 7.0, "the defaults are docs/calibration.md's: " + rules);
        Check(rules.WithMatchScore(0.50).Equals(rules), "a match score of 0.50 gives the defaults back: " + rules.WithMatchScore(0.50));
        var moved = rules.WithMatchScore(0.70);
        Check(moved.SuggestScore == 0.60 && moved.MatchScore == 0.70 && moved.LearnScore == 0.80, "0.70 moves the suggest and learn scores with it: " + moved);
        Check(moved.MatchMargin == 0.10 && moved.LearnMargin == 0.15 && moved.UsableEyes == 16.0 && moved.LearnEyes == 24.0 && moved.FitLimit == 7.0,
            "nothing else moves: " + moved);
        Console.WriteLine("PASS people: the rules are the calibrated defaults, and a match score moves the suggest and learn scores a tenth either side");
    }

    static void TheBestPersonIsRecognisedByScoreAndMargin()
    {
        var face = Unit(1);
        var match = Match(face, People(Person("ana", At(face, 0.8, 2)), Person("ivan", At(face, 0.3, 3)), Person("mia", At(face, 0.3, 4))));
        Check(match.Kind.ToString() == "Recognised" && match.Person.Id == "ana", $"recognised as ana: {match}");
        Check(Math.Abs(match.Score - 0.8) < 1e-5 && Math.Abs(match.Margin - 0.5) < 1e-5, $"score 0.8 and margin 0.5: {match.Score}, {match.Margin}");
        Console.WriteLine("PASS people: a face at 0.8 from one person and 0.3 from the others is recognised, with a score of 0.8 and a margin of 0.5");
    }

    static void AtTheMatchScoreExactlyIsRecognised()
    {
        var face = Unit(5);
        var sample = At(face, 0.55, 6);
        var people = People(Person("ana", sample));
        double score = (float)Embedding(sample).Similarity(Embedding(face));
        var at = Match(face, people, Rules(score));
        Check(at.Kind.ToString() == "Recognised" && at.Person.Id == "ana" && at.Score == score, $"at the match score exactly: {at}");
        var under = Match(face, people, Rules(Math.BitIncrement(score)));
        Check(under.Kind.ToString() == "Maybe" && under.Person.Id == "ana", $"just under the match score: {under}");
        Check(Math.Abs(at.Margin - score) < 1e-9, $"alone, the margin is the whole score: {at.Margin}");
        Console.WriteLine("PASS people: a face at the match score exactly is recognised, and just under it is a maybe");
    }

    static void TwoPeopleWithinTheMarginAreAMaybe()
    {
        var face = Unit(7);
        var match = Match(face, People(Person("ivan", At(face, 0.62, 8)), Person("ana", At(face, 0.68, 9))));
        Check(match.Kind.ToString() == "Maybe" && match.Person.Id == "ana", $"a maybe for the better: {match}");
        Check(Math.Abs(match.Score - 0.68) < 1e-5 && Math.Abs(match.Margin - 0.06) < 1e-5, $"score 0.68, margin 0.06: {match}");
        Console.WriteLine("PASS people: a face over the match score for two people within the margin is a maybe for the better");
    }

    static void UnderTheSuggestScoreIsUnknown()
    {
        var face = Unit(10);
        var match = Match(face, People(Person("ana", At(face, 0.35, 11))));
        Check(match.Kind.ToString() == "Unknown" && match.Person == null, $"unknown, with no one: {match}");
        Check(Math.Abs(match.Score - 0.35) < 1e-5, $"its best score is still given: {match.Score}");
        Console.WriteLine("PASS people: a face under the suggest score is unknown");
    }

    static void APersonScoresByTheirBestSample()
    {
        var face = Unit(12);
        var match = Match(face, People(Person("ana", At(face, 0.2, 13), At(face, 0.75, 14), At(face, 0.4, 15)), Person("ivan", At(face, 0.5, 16))));
        Check(match.Person.Id == "ana" && Math.Abs(match.Score - 0.75) < 1e-5 && Math.Abs(match.Margin - 0.25) < 1e-5, $"ana by her best sample: {match}");
        Console.WriteLine("PASS people: a person's score is their best sample's");
    }

    static void AnEmptyListIsUnknown()
    {
        var match = Match(Unit(17), People());
        Check(match.Kind.ToString() == "Unknown" && match.Person == null, $"an empty list: {match}");
        var withoutSamples = Match(Unit(17), People(Person("ana")));
        Check(withoutSamples.Kind.ToString() == "Unknown" && withoutSamples.Person == null, $"a person without samples: {withoutSamples}");
        Console.WriteLine("PASS people: an empty list, or one whose people have no samples, gives unknown");
    }

    // ---- The list ----

    static void APersonComesBackFromTheStore()
    {
        using var home = new Home();
        var list = List(home.Open("def-a"));
        Check(((object[])Items(list.People)).Length == 0, "a new store has no one");
        var first = NewSample(Unit(20), Crop(20), T0, "camera.porch", fit: 2.25f);
        var second = NewSample(Unit(21), Crop(21, 5000), T0.AddMinutes(3), Camera, fit: 0.5f);
        var added = list.Add(Named("  Ana Kovač "), Samples(first, second));
        Check(added.Name == "Ana Kovač" && ((object[])Items(added.Samples)).Length == 2, $"the name is trimmed and both samples are kept: {added.Name}");

        var again = List(home.Restarted());
        var ana = ((object[])Items(again.People)).Single();
        dynamic person = ana;
        Check(person.Id == added.Id && person.Name == "Ana Kovač" && person.Added == added.Added && person.LastSeen == null && person.LastSeenAt == null,
            $"the same person: {person.Id} {person.Added}");
        var samples = ((object[])Items(person.Samples));
        object[] given = [first, second];
        Check(samples.Length == 2, $"two samples: {samples.Length}");
        for (var i = 0; i < 2; i++)
        {
            dynamic stored = samples[i], sent = given[i];
            float[] values = stored.Embedding.Values, original = sent.Embedding.Values;
            Check(values.Select(BitConverter.SingleToInt32Bits).SequenceEqual(original.Select(BitConverter.SingleToInt32Bits)), $"sample {i}'s embedding comes back bit for bit");
            Check(stored.Embedding.Fit == sent.Embedding.Fit && stored.At == sent.At && stored.Camera == sent.Camera && stored.Learned == false,
                $"sample {i}'s fit, time, camera and kind: {stored}");
            byte[] crop = again.Crop((string)stored.Id);
            Check(crop.SequenceEqual((byte[])sent.CropJpeg), $"sample {i}'s crop comes back byte for byte");
        }
        Check(again.Crop("nosuchsample") == null, "a sample no one has has no crop");
        Check(again.Named("ana kovač ").Id == added.Id && again.Named(" ANA KOVAČ") != null && again.Named("Ana") == null, "Named ignores case and surrounding spaces");
        Console.WriteLine("PASS people: a person added comes back after a restart, embeddings bit for bit and crops byte for byte");
    }

    static void NamesAreTrimmedAndCheckedIgnoringCase()
    {
        using var home = new Home();
        var list = List(home.Open("def-a"));
        list.Add(Named("Ana"), Samples(Seeded(30)));
        var taken = Refused(() => list.Add(Named("  aNA "), Samples(Seeded(31))), "a name taken but for case and spaces");
        Check(taken == "aNA is already someone else's name.", "the refusal: " + taken);
        Check(Refused(() => list.Add("   ", Samples(Seeded(32))), "a blank name") == "A name cannot be empty.", "a blank name is refused");
        var longest = new string('x', 64);
        Check(Refused(() => list.Add(longest + "y", Samples(Seeded(33))), "65 characters") == "A name is at most 64 characters.", "65 characters are refused");
        list.Add(Named(" " + longest + " "), Samples(Seeded(34)));
        Check(Refused(() => list.Add(Named("Mia"), Samples()), "no samples") == "Mia needs a face to be named with.", "a person needs a sample");
        Check(((object[])Items(list.People)).Select(p => (string)((dynamic)p).Name).Order(StringComparer.Ordinal).SequenceEqual(["Ana", longest]), "only Ana and the 64 x's were added");
        Check(((object[])Items(List(home.Restarted()).People)).Length == 2, "nothing refused was stored");
        Console.WriteLine("PASS people: a name is trimmed, at most 64 characters, and taken whatever its case and surrounding spaces");
    }

    static void RenameRefusesATakenName()
    {
        using var home = new Home();
        var list = List(home.Open("def-a"));
        var ana = list.Add(Named("Ana"), Samples(Seeded(40)));
        list.Add(Named("Ivan"), Samples(Seeded(41)));
        var refused = Refused(() => list.Rename((string)ana.Id, Named(" ivan")), "a rename to a taken name");
        Check(refused == "ivan is already someone else's name.", "the refusal: " + refused);
        list.Rename((string)ana.Id, Named(" ANA "));
        Check(Only(list, "ANA").Id == ana.Id, "a rename to its own name in another case");
        list.Rename((string)ana.Id, Named("Ana Marija"));
        Check(Only(List(home.Restarted()), "Ana Marija").Id == ana.Id, "the rename is stored");
        Check(Refused(() => list.Rename("gone0000", "Mia"), "a rename of no one") == "This person is no longer in the list.", "a person no one has");
        Console.WriteLine("PASS people: a rename to a taken name is refused, and to its own name in another case is not");
    }

    static void SamplesAreCappedAndLearnedOnesReplaced()
    {
        using var home = new Home();
        var data = home.Open("def-a");
        var list = List(data);
        var ana = list.Add(Named("Ana"), Samples(Enumerable.Range(100, 19).Select(seed => (object)Seeded(seed)).ToArray()));
        string id = ana.Id;
        list.AddSamples(id, Samples(Seeded(119)), false);
        Check(((object[])Items(Only(list, "Ana").Samples)).Length == 20, "20 named samples");
        Check(Refused(() => list.AddSamples(id, Samples(Seeded(120)), false), "the 21st named sample") == "Ana has no room for more samples.", "the 21st named sample is refused");
        Check(Refused(() => list.Add(Named("Mia"), Samples(Enumerable.Range(130, 21).Select(seed => (object)Seeded(seed)).ToArray())), "21 samples at once")
            == "Mia has no room for more samples.", "a person named with 21 samples is refused");

        // 20 learned samples, the oldest first, then a 21st, which replaces the oldest learned one.
        for (var i = 0; i < 20; i++) list.AddSamples(id, Samples(NewSample(Unit(200 + i), Crop(200 + i), T0.AddHours(1 + i))), true);
        var learned = ((object[])Items(Only(list, "Ana").Samples)).Where(s => ((dynamic)s).Learned).ToArray();
        Check(learned.Length == 20, $"20 learned samples: {learned.Length}");
        string oldest = ((dynamic)learned.MinBy(s => (DateTimeOffset)((dynamic)s).At)!).Id;
        var person = list.AddSamples(id, Samples(NewSample(Unit(300), Crop(300), T0.AddDays(2))), true);
        var after = ((object[])Items(person.Samples));
        Check(after.Count(s => ((dynamic)s).Learned) == 20 && after.Count(s => !((dynamic)s).Learned) == 20, $"still 20 learned and 20 named: {after.Length}");
        Check(after.All(s => ((dynamic)s).Id != oldest) && list.Crop(oldest) == null, "the oldest learned sample is gone, crop and all");
        Check(data.Keys($"samples/{id}/{oldest}/").Count == 0, "the replaced sample's keys are removed");
        Check(after.Any(s => ((dynamic)s).At == T0.AddDays(2)), "the new learned sample is kept");
        Check(((object[])Items(Only(List(home.Restarted()), "Ana").Samples)).Length == 40, "40 samples are stored");
        Check(Refused(() => list.AddSamples("gone0000", Samples(Seeded(1)), true), "samples for no one") == "This person is no longer in the list.", "samples for a person no one has");
        Console.WriteLine("PASS people: the 21st named sample is refused, and the 21st learned one replaces the oldest learned");
    }

    static void ACropOverOneMebibyteIsRefused()
    {
        using var home = new Home();
        var list = List(home.Open("def-a"));
        list.Add(Named("Ana"), Samples(NewSample(Unit(50), Crop(50, 1024 * 1024), T0)));
        Throws<ArgumentException>(() => list.Add(Named("Ivan"), Samples(NewSample(Unit(51), Crop(51, 1024 * 1024 + 1), T0))), "a crop over 1 MiB");
        Throws<ArgumentException>(() => list.Add(Named("Ivan"), Samples(NewSample(new float[64], Crop(52), T0))), "an embedding of 64 values");
        Check(((object[])Items(list.People)).Length == 1 && List(home.Restarted()).Named("Ivan") == null, "nothing of Ivan was stored");
        Console.WriteLine("PASS people: a crop over 1 MiB, or an embedding without 128 values, is refused");
    }

    static void The201stPersonIsRefused()
    {
        using var home = new Home();
        var list = List(home.Open("def-a"));
        for (var i = 0; i < 200; i++) list.Add($"Person {i}", Samples(NewSample(Unit(1000 + i), Crop(1000 + i, 40), T0)));
        Check(((object[])Items(list.People)).Length == 200, "200 people");
        var refused = Refused(() => list.Add(Named("Mia"), Samples(Seeded(60))), "the 201st person");
        Check(refused == "The list has room for 200 people; delete someone to add another.", "the refusal: " + refused);
        Check(((object[])Items(List(home.Restarted()).People)).Length == 200, "200 people are stored");
        Console.WriteLine("PASS people: the 201st person is refused");
    }

    static void MergeMovesSamplesUpToTheCaps()
    {
        using var home = new Home();
        var data = home.Open("def-a");
        var list = List(data);
        var ana = list.Add(Named("Ana"), Samples(Enumerable.Range(400, 15).Select(seed => (object)Seeded(seed)).ToArray()));
        var ivan = list.Add(Named("Ivan"), Samples(Enumerable.Range(500, 10).Select(seed => (object)Seeded(seed)).ToArray()));
        string anaId = ana.Id, ivanId = ivan.Id;
        for (var i = 0; i < 3; i++) list.AddSamples(ivanId, Samples(NewSample(Unit(600 + i), Crop(600 + i), T0.AddHours(i))), true);
        list.Seen(ivanId, T0.AddDays(1), "camera.porch");
        list.Seen(anaId, T0.AddHours(5), Camera);
        var newestNamed = ((object[])Items(Only(list, "Ivan").Samples)).Where(s => !((dynamic)s).Learned).OrderByDescending(s => (DateTimeOffset)((dynamic)s).At).Take(5)
            .Select(s => (string)((dynamic)s).Id).ToHashSet();

        list.Merge(ivanId, anaId);
        Check(list.Named("Ivan") == null && ((object[])Items(list.People)).Length == 1, "Ivan is gone");
        var merged = Only(list, "Ana");
        var samples = ((object[])Items(merged.Samples));
        Check(samples.Count(s => !((dynamic)s).Learned) == 20 && samples.Count(s => ((dynamic)s).Learned) == 3, $"20 named and 3 learned: {samples.Length}");
        Check(newestNamed.All(id => samples.Any(s => ((dynamic)s).Id == id)), "Ivan's newest named samples filled Ana's room");
        Check(merged.LastSeen == T0.AddDays(1) && merged.LastSeenAt == "camera.porch", $"the later last seen is kept: {merged.LastSeen} {merged.LastSeenAt}");
        Check(samples.All(s => list.Crop((string)((dynamic)s).Id) != null), "every sample kept has its crop");
        Check(data.Keys($"people/{ivanId}").Count == 0 && data.Keys($"samples/{ivanId}/").Count == 0, "none of Ivan's keys is left: " + string.Join(", ", data.Keys()));
        var stored = Only(List(home.Restarted()), "Ana");
        Check(((object[])Items(stored.Samples)).Length == 23, "the merge is stored");
        Check(Refused(() => list.Merge(anaId, anaId), "a merge into itself") == "A person cannot be merged with themselves.", "a merge into itself");
        Check(Refused(() => list.Merge(anaId, "gone0000"), "a merge into no one") == "This person is no longer in the list.", "a merge into no one");
        Console.WriteLine("PASS people: a merge moves samples up to the caps, named first, keeps the later last seen and removes the source");
    }

    static void AnInterruptedMergeLeavesBothReadable()
    {
        using var home = new Home();
        var data = home.Open("def-a");
        var list = List(data);
        var ana = list.Add(Named("Ana"), Samples(Seeded(700), Seeded(701)));
        var ivan = list.Add(Named("Ivan"), Samples(Seeded(702), Seeded(703), Seeded(704)));
        string anaId = ana.Id, ivanId = ivan.Id;
        var ivans = ((object[])Items(ivan.Samples)).Select(s => (string)((dynamic)s).Id).ToArray();

        // The merge's first step, as an interruption after the samples are written and before the source is removed leaves it.
        var moved = ((object)list).GetType().GetMethod("MoveSamples", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("PeopleList has no MoveSamples step");
        Invoke(moved, list, ivanId, anaId);

        var after = List(home.Restarted());
        var anaAfter = ((object[])Items(Only(after, "Ana").Samples)).Select(s => (string)((dynamic)s).Id).ToArray();
        var ivanAfter = ((object[])Items(Only(after, "Ivan").Samples)).Select(s => (string)((dynamic)s).Id).ToArray();
        Check(anaAfter.Length == 5 && ivans.All(anaAfter.Contains), "Ana has Ivan's samples: " + string.Join(", ", anaAfter));
        Check(ivanAfter.Order(StringComparer.Ordinal).SequenceEqual(ivans.Order(StringComparer.Ordinal)), "Ivan still has his");
        Check(anaAfter.All(id => after.Crop(id) != null) && ivanAfter.All(id => after.Crop(id) != null), "every sample has its crop");

        // A second merge, by another detector, finishes the job, without a sample twice.
        var other = List(home.Open("def-b"));
        other.Merge(ivanId, anaId);
        var done = ((object[])Items(Only(List(home.Restarted()), "Ana").Samples)).Select(s => (string)((dynamic)s).Id).ToArray();
        Check(done.Length == 5 && done.Distinct().Count() == 5, "Ana has five samples, each once: " + string.Join(", ", done));
        Check(other.Named("Ivan") == null && data.Keys($"samples/{ivanId}/").Count == 0 && data.Keys($"people/{ivanId}").Count == 0, "Ivan is gone: " + string.Join(", ", data.Keys()));
        Console.WriteLine("PASS people: an interrupted merge leaves both people readable, and a second merge finishes it");
    }

    static void TheLastNamedSampleIsKept()
    {
        using var home = new Home();
        var data = home.Open("def-a");
        var list = List(data);
        var ana = list.Add(Named("Ana"), Samples(Seeded(800), Seeded(801)));
        string id = ana.Id;
        var learned = list.AddSamples(id, Samples(Seeded(802)), true);
        var named = ((object[])Items(ana.Samples)).Select(s => (string)((dynamic)s).Id).ToArray();
        string learnedId = ((object[])Items(learned.Samples)).Select(s => (dynamic)s).Single(s => s.Learned).Id;

        list.RemoveSample(id, learnedId);
        list.RemoveSample(id, named[0]);
        Check(((object[])Items(Only(list, "Ana").Samples)).Select(s => (string)((dynamic)s).Id).SequenceEqual([named[1]]), "one named sample is left");
        Check(data.Keys($"samples/{id}/{named[0]}/").Count == 0 && data.Keys($"samples/{id}/{learnedId}/").Count == 0, "the removed samples' keys are gone");
        Check(Refused(() => list.RemoveSample(id, named[1]), "the last named sample") == LastNamed, "the last named sample is kept");
        Check(((object[])Items(Only(List(home.Restarted()), "Ana").Samples)).Length == 1, "it is still stored");
        list.RemoveSample(id, "nosuchsample");
        list.RemoveSample("gone0000", "nosuchsample");
        Console.WriteLine("PASS people: a sample is removed with its keys, except the last named one");
    }

    static void DeleteRemovesEveryKey()
    {
        using var home = new Home();
        var data = home.Open("def-a");
        var list = List(data);
        var ana = list.Add(Named("Ana"), Samples(Seeded(900), Seeded(901)));
        list.AddSamples((string)ana.Id, Samples(Seeded(902)), true);
        list.Seen((string)ana.Id, T0, Camera);
        var before = data.Keys();
        list.Delete((string)ana.Id);
        Check(((object[])Items(list.People)).Length == 0 && list.Named("Ana") == null, "Ana is gone");
        Check(data.Keys().SequenceEqual(["format", "names"]), "only the format and the names are left: " + string.Join(", ", data.Keys()) + " of " + string.Join(", ", before));
        Check(data.Get<Dictionary<string, string>>("names")!.Count == 0, "the names are empty");
        list.Delete((string)ana.Id);
        list.Add(Named("ana"), Samples(Seeded(903)));
        Console.WriteLine("PASS people: a delete removes the person's every key, and frees the name");
    }

    static void SeenIsWrittenAtMostOnceAMinute()
    {
        using var home = new Home();
        var list = List(home.Open("def-a"));
        string id = list.Add(Named("Ana"), Samples(Seeded(950))).Id;
        DateTimeOffset? Stored() => Only(List(home.Restarted()), "Ana").LastSeen;
        list.Seen(id, T0, Camera);
        Check(Stored() == T0 && Only(list, "Ana").LastSeenAt == Camera, "the first sighting is written");
        list.Seen(id, T0.AddSeconds(59), "camera.porch");
        Check(Stored() == T0, "a sighting within a minute is not written: " + Stored());
        list.Seen(id, T0.AddSeconds(61), "camera.porch");
        Check(Stored() == T0.AddSeconds(61) && Only(List(home.Restarted()), "Ana").LastSeenAt == "camera.porch", "a sighting a minute later is: " + Stored());
        list.Seen("gone0000", T0, Camera);
        Check(((object[])Items(List(home.Restarted()).People)).Length == 1, "a sighting of no one adds no one");
        Console.WriteLine("PASS people: last seen is written at most once a minute a person");
    }

    // ---- Two lists on one store ----

    static async Task TwoListsAddAtOnceAndLoseNoName()
    {
        using var home = new Home();
        var a = List(home.Open("def-a"));
        var b = List(home.Open("def-b"));
        using var reading = new CancellationTokenSource();
        var reads = 0;
        // Reads the snapshot over and over while both write and reload.
        var reader = Task.Run(() =>
        {
            while (!reading.IsCancellationRequested)
            {
                foreach (var person in ((object[])Items(a.People))) _ = ((object[])Items(((dynamic)person).Samples)).Length;
                reads++;
            }
        });
        var adds = Enumerable.Range(0, 16).SelectMany(i => new[]
        {
            Task.Run(() => { a.Add(Named($"A {i}"), Samples(Seeded(2000 + i))); }),
            Task.Run(() => { b.Add(Named($"B {i}"), Samples(Seeded(3000 + i))); }),
        }).ToArray();
        await Task.WhenAll(adds);
        reading.Cancel();
        await reader;
        var stored = ((object[])Items(List(home.Restarted()).People)).Select(p => (string)((dynamic)p).Name).Order(StringComparer.Ordinal).ToArray();
        var expected = Enumerable.Range(0, 16).SelectMany(i => new[] { $"A {i}", $"B {i}" }).Order(StringComparer.Ordinal).ToArray();
        Check(stored.SequenceEqual(expected), $"all 32 names are stored: {stored.Length}");
        await Until(() => ((object[])Items(a.People)).Length == 32 && ((object[])Items(b.People)).Length == 32, "both lists to hold all 32");
        Check(reads > 0, "the snapshot was read meanwhile");
        Console.WriteLine($"PASS people: two lists adding 16 people each at once lose no name, and their snapshots read meanwhile ({reads} reads)");
    }

    static async Task ChangedReachesTheOtherList()
    {
        using var home = new Home();
        var (ranA, ranB) = (0, 0);
        var a = List(home.Open("def-a"), () => { Interlocked.Increment(ref ranA); return Task.CompletedTask; });
        dynamic? seen = null;
        dynamic b = null!;
        b = List(home.Open("def-b"), () =>
        {
            seen = b.Named("Ana");
            Interlocked.Increment(ref ranB);
            return Task.CompletedTask;
        });
        var ana = a.Add(Named("Ana"), Samples(Seeded(4000)));
        await Until(() => Volatile.Read(ref ranB) > 0 && seen != null, "B's change action, after B reloaded, to see Ana");
        Check(seen!.Id == ana.Id && ((object[])Items(seen.Samples)).Length == 1, "B sees Ana with her sample");
        b.Rename((string)ana.Id, Named("Ana Marija"));
        await Until(() => a.Named("Ana Marija") != null, "A to see B's rename");
        await Task.Delay(200);
        var ownRuns = Volatile.Read(ref ranA);
        a.Delete((string)ana.Id);
        await Until(() => b.Named("Ana Marija") == null, "B to see A's delete");
        await Task.Delay(200);
        Check(Volatile.Read(ref ranA) == ownRuns, $"A's own delete ran A's change action {ranA - ownRuns} times");
        Console.WriteLine("PASS people: a change one list makes reloads the other and runs its change action, and never the writer's own");
    }

    static async Task AListReloadsToEmptyAfterAClear()
    {
        using var home = new Home();
        var a = List(home.Open("def-a"));
        var b = List(home.Open("def-b"));
        a.Add(Named("Ana"), Samples(Seeded(4100)));
        a.Add(Named("Ivan"), Samples(Seeded(4101)));
        await Until(() => ((object[])Items(b.People)).Length == 2, "B to see both");
        // Stored data's dialog, or a device's button, clears the store.
        home.Open("def-c").Clear();
        await Until(() => ((object[])Items(a.People)).Length == 0 && ((object[])Items(b.People)).Length == 0, "both lists to reload to empty");
        a.Reload();
        Check(((object[])Items(a.People)).Length == 0, "a reload after the clear is empty");
        a.Add(Named("Ana"), Samples(Seeded(4102)));
        Check(((object[])Items(a.People)).Length == 1, "a person is added after the clear");
        Console.WriteLine("PASS people: a list reloads to empty after the store is cleared, and is written again after");
    }

    // ---- What an interruption or a hand deletion leaves ----

    static void ANameWithoutItsPersonIsListedWithoutSamples()
    {
        using var home = new Home();
        var data = home.Open("def-a");
        var list = List(data);
        string id = list.Add(Named("Ana"), Samples(Seeded(5000))).Id;
        data.Remove($"people/{id}");
        list.Reload();
        var ana = Only(list, "Ana");
        Check(((object[])Items(ana.Samples)).Length == 0 && ana.Added == DateTimeOffset.MinValue, $"Ana is listed without samples, added at an unknown time: {ana}");
        Check(Match(Unit(5000), list.People).Kind.ToString() == "Unknown", "a person without samples matches no one");
        Check(Refused(() => list.Add(Named("ana"), Samples(Seeded(5001))), "her name") == "ana is already someone else's name.", "her name stays taken");
        var again = list.AddSamples(id, Samples(Seeded(5002)), false);
        Check(((object[])Items(again.Samples)).Length == 1, "a sample can be added to her again");
        list.Delete(id);
        Check(data.Keys().SequenceEqual(["format", "names"]), "and she can be deleted: " + string.Join(", ", data.Keys()));
        Console.WriteLine("PASS people: a name whose person is gone is listed without samples, and can be given samples or deleted");
    }

    static void SamplesNoOneListsAreIgnored()
    {
        using var home = new Home();
        var data = home.Open("def-a");
        var list = List(data);
        string id = list.Add(Named("Ana"), Samples(Seeded(5100))).Id;
        byte[] embedding = new byte[512];
        data.SetBytes("samples/nobody00/sample00/embedding", embedding);
        data.SetBytes("samples/nobody00/sample00/crop", Crop(5101));
        data.SetBytes($"samples/{id}/unlisted0/embedding", embedding);
        data.Set("people/nobody01", new { added = T0, samples = Array.Empty<object>() });
        list.Reload();
        Check(((object[])Items(list.People)).Length == 1 && ((object[])Items(Only(list, "Ana").Samples)).Length == 1, "only Ana and her one listed sample are loaded");
        Check(list.Crop("sample00") == null, "a sample no one lists has no crop");
        Console.WriteLine("PASS people: samples and people that no name lists are ignored");
    }

    static void AnEmbeddingOfTheWrongLengthIsLeftOut()
    {
        using var home = new Home();
        var data = home.Open("def-a");
        var list = List(data);
        var ana = list.Add(Named("Ana"), Samples(Seeded(5200), Seeded(5201)));
        string id = ana.Id;
        var ids = ((object[])Items(ana.Samples)).Select(s => (string)((dynamic)s).Id).ToArray();
        data.SetBytes($"samples/{id}/{ids[0]}/embedding", new byte[100]);
        data.Remove($"samples/{id}/{ids[1]}/crop");
        var again = List(home.Restarted());
        var samples = ((object[])Items(Only(again, "Ana").Samples)).Select(s => (string)((dynamic)s).Id).ToArray();
        Check(samples.SequenceEqual([ids[1]]), "the sample whose embedding has 100 bytes is left out: " + string.Join(", ", samples));
        Check(again.Crop(ids[1]) == null, "a sample whose crop is gone has none, and is still used");
        Console.WriteLine("PASS people: a sample whose embedding is not 512 bytes is left out, and one without its crop is kept");
    }

    /// <summary>Every key of the store with its bytes, to see that nothing was written.</summary>
    static string Contents(SharpData data) => string.Join("|", data.Keys().Select(key =>
    {
        try { return key + "=" + Convert.ToHexString(data.GetBytes(key)!); }
        catch (InvalidOperationException) { return key + "=" + System.Text.Json.JsonSerializer.Serialize(data.Get<System.Text.Json.JsonElement>(key)); }
    }));

    static void ANewerFormatIsNeitherReadNorWritten()
    {
        using var home = new Home();
        var data = home.Open("def-a");
        var list = List(data);
        string id = list.Add(Named("Ana"), Samples(Seeded(5300))).Id;
        data.Set("format", 2);
        var before = Contents(data);
        var newer = List(home.Restarted());
        Check(((object[])Items(newer.People)).Length == 0, "a store of a newer format is not read");
        const string Newer = "The people list was saved by a newer hass-sharp-vision; update this one to change it.";
        Check(Refused(() => newer.Add(Named("Ivan"), Samples(Seeded(5301))), "an add") == Newer, "an add is refused");
        list.Reload();
        Check(Refused(() => list.Rename(id, "Mia"), "a rename") == Newer, "a rename is refused");
        Check(Refused(() => list.Delete(id), "a delete") == Newer, "a delete is refused");
        Check(Refused(() => list.AddSamples(id, Samples(Seeded(5302)), true), "samples") == Newer, "samples are refused");
        Check(Refused(() => list.RemoveSample(id, "nosuchsample"), "a sample's removal") == Newer, "a sample's removal is refused");
        Check(Refused(() => list.Merge(id, id + "x"), "a merge") == Newer, "a merge is refused");
        list.Seen(id, T0, Camera);
        Check(Contents(data) == before, "nothing was written");
        data.Set("format", "one");
        var unreadable = List(home.Restarted());
        Check(((object[])Items(unreadable.People)).Length == 0, "a format that is not a number is not read either");
        Check(Refused(() => unreadable.Add(Named("Ivan"), Samples(Seeded(5303))), "an add") == "The people list cannot be read; delete it in Stored data to start again.",
            "nor written");
        Console.WriteLine("PASS people: a store of a newer format is neither read nor written, and the refusal says so");
    }

    static void UnreadableNamesAreNeitherReadNorWritten()
    {
        using var home = new Home();
        var data = home.Open("def-a");
        var list = List(data);
        list.Add(Named("Ana"), Samples(Seeded(5400)));
        data.SetBytes("names", [1, 2, 3]);
        var before = Contents(data);
        var damaged = List(home.Restarted());
        Check(((object[])Items(damaged.People)).Length == 0, "unreadable names give no one");
        var refused = Refused(() => damaged.Add(Named("Ivan"), Samples(Seeded(5401))), "an add");
        Check(refused == "The people list cannot be read; delete it in Stored data to start again.", "the refusal: " + refused);
        Check(Contents(data) == before, "nothing was written");
        Console.WriteLine("PASS people: names that cannot be read give an empty list, and changes are refused");
    }

    // ---- What an interrupted add, delete or merge leaves, and the next list finishes ----

    /// <summary>
    /// What an add, delete or merge notes in <c>unfinished/&lt;id&gt;</c> while it runs: when it began, and whether it removes
    /// the person.
    /// </summary>
    static void Unfinished(SharpData data, string id, DateTimeOffset at, bool removing) => data.Set($"unfinished/{id}", new { at, removing });

    /// <summary>A sample's keys and a person's record, as an add writes them before it writes the name.</summary>
    static void Written(SharpData data, string id, int seed)
    {
        data.SetBytes($"samples/{id}/sample{seed}/embedding", new byte[512]);
        data.SetBytes($"samples/{id}/sample{seed}/crop", Crop(seed));
        data.Set($"people/{id}", new { added = T0, samples = new[] { new { id = $"sample{seed}", at = T0, camera = Camera, learned = false, fit = 1.5f } } });
    }

    /// <summary>Every key of a person's: their record and their samples'.</summary>
    static string[] KeysOf(SharpData data, string id) => [.. data.Keys($"people/{id}").Where(key => key == $"people/{id}"), .. data.Keys($"samples/{id}/")];

    static void AnInterruptedAddLeavesNoFaceData()
    {
        using var home = new Home();
        var data = home.Open("def-a");
        var list = List(data);
        list.Add(Named("Ana"), Samples(Seeded(6000)));
        // An add interrupted after its samples and record, before its name: begun an hour ago, so no add is still writing it.
        Unfinished(data, "interrupted0", DateTimeOffset.UtcNow.AddHours(-1), false);
        Written(data, "interrupted0", 6001);
        // An add another detector is writing at this moment.
        Unfinished(data, "inprogress00", DateTimeOffset.UtcNow, false);
        Written(data, "inprogress00", 6002);

        var after = List(home.Restarted());
        var stored = home.Restarted("check");
        Check(KeysOf(stored, "interrupted0").Length == 0 && stored.Keys("unfinished/interrupted0").Count == 0,
            "nothing of the interrupted add is left: " + string.Join(", ", stored.Keys()));
        Check(KeysOf(stored, "inprogress00").Length == 3 && stored.Keys("unfinished/inprogress00").Count == 1,
            "the add in progress keeps its keys and its note: " + string.Join(", ", stored.Keys()));
        Check(((object[])Items(after.People)).Length == 1 && after.Named("Ana") != null, "Ana is still listed, and only she");
        Check(stored.Keys("unfinished/").Count == 1, "Ana's own add left no note: " + string.Join(", ", stored.Keys("unfinished/")));

        // A delete finishes what is old by then, too. One DataStores of its own, which has read what the last list removed.
        var deleting = new DataStores(home.Root).OpenShared(StoreName, new DataUser("def-b", "front_door", action => Task.Run(action)));
        Unfinished(deleting, "inprogress00", DateTimeOffset.UtcNow.AddHours(-1), false);
        var other = List(deleting);
        Unfinished(deleting, "interrupted1", DateTimeOffset.UtcNow.AddHours(-1), false);
        Written(deleting, "interrupted1", 6003);
        other.Delete((string)other.Named("Ana").Id);
        Check(deleting.Keys().SequenceEqual(["format", "names"]), "a delete finished the interrupted add: " + string.Join(", ", deleting.Keys()));
        Console.WriteLine("PASS people: an interrupted add leaves no face data once a list is made on the store, and an add in progress is left alone");
    }

    static void AnInterruptedDeleteLeavesNoFaceData()
    {
        using var home = new Home();
        var data = home.Open("def-a");
        var list = List(data);
        var ana = list.Add(Named("Ana"), Samples(Seeded(6100), Seeded(6101)));
        list.AddSamples((string)ana.Id, Samples(Seeded(6102)), true);
        var ivan = list.Add(Named("Ivan"), Samples(Seeded(6103)));
        string id = ana.Id;
        // A delete interrupted after it removed the name, before the record and the samples: just now, so it is young.
        Unfinished(data, id, DateTimeOffset.UtcNow, true);
        data.Update<Dictionary<string, string>>("names", names => { names!.Remove(id); return names; });
        Check(KeysOf(data, id).Length == 7, "the interruption left Ana's record and three samples: " + string.Join(", ", KeysOf(data, id)));

        var after = List(home.Restarted());
        var stored = home.Restarted("check");
        Check(KeysOf(stored, id).Length == 0 && stored.Keys("unfinished/").Count == 0, "nothing of Ana is left: " + string.Join(", ", stored.Keys()));
        Check(after.Named("Ana") == null && after.Named("Ivan").Id == ivan.Id && KeysOf(stored, (string)ivan.Id).Length == 3, "Ivan is untouched");

        // A delete interrupted before it removed the name leaves the person, and its note goes once it is old.
        Unfinished(stored, (string)ivan.Id, DateTimeOffset.UtcNow.AddHours(-1), true);
        var again = List(home.Restarted());
        Check(again.Named("Ivan") != null && home.Restarted("check").Keys("unfinished/").Count == 0, "Ivan is still listed, and the note is gone");
        Console.WriteLine("PASS people: an interrupted delete leaves no face data once a list is made on the store");
    }

    static void AnInterruptedMergeLeavesNoFaceData()
    {
        using var home = new Home();
        var data = home.Open("def-a");
        var list = List(data);
        var ana = list.Add(Named("Ana"), Samples(Seeded(6200)));
        var ivan = list.Add(Named("Ivan"), Samples(Seeded(6201), Seeded(6202)));
        string anaId = ana.Id, ivanId = ivan.Id;
        // A merge interrupted after it copied the samples and removed the source's name, before the source's keys went.
        var moved = ((object)list).GetType().GetMethod("MoveSamples", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("PeopleList has no MoveSamples step");
        Invoke(moved, list, ivanId, anaId);
        Unfinished(data, ivanId, DateTimeOffset.UtcNow, true);
        data.Update<Dictionary<string, string>>("names", names => { names!.Remove(ivanId); return names; });

        var after = List(home.Restarted());
        var stored = home.Restarted("check");
        Check(KeysOf(stored, ivanId).Length == 0 && stored.Keys("unfinished/").Count == 0, "nothing of Ivan is left: " + string.Join(", ", stored.Keys()));
        var samples = ((object[])Items(Only(after, "Ana").Samples)).Select(s => (string)((dynamic)s).Id).ToArray();
        Check(samples.Length == 3 && samples.All(sample => after.Crop(sample) != null), "Ana has her sample and Ivan's two, with their crops: " + samples.Length);
        Check(KeysOf(stored, anaId).Length == 7, "Ana's record and three samples are stored");
        Console.WriteLine("PASS people: an interrupted merge leaves none of the source's face data once a list is made on the store");
    }

    // ---- Unknown faces ----

    /// <summary>The unknown-face groups of the detector whose private store is <paramref name="data"/>, at <see cref="Camera"/>.</summary>
    static Faces Groups(SharpData data, object? rules = null) => new(Activator.CreateInstance(Type("FaceGroups"), data, Camera, rules ?? (object)DefaultRules)!);

    /// <summary>
    /// A <c>FaceGroups</c> of the compiled library's, called through <c>dynamic</c> behind members of known types, so
    /// that what they give can go into a lambda.
    /// </summary>
    sealed class Faces(object target)
    {
        readonly dynamic groups = target;

        /// <summary>
        /// A face the people list does not know, seen in the check at <paramref name="now"/>; its crop is made from
        /// <paramref name="seed"/>, and each time it is asked for counts in <paramref name="crops"/>.
        /// </summary>
        public void See(float[] face, DateTimeOffset now, double eyes = 20, string? suggested = null, int seed = 0, int[]? crops = null)
        {
            Func<byte[]> crop = () =>
            {
                if (crops != null) crops[0]++;
                return PeopleTests.Crop(seed);
            };
            groups.Seen(Embedding(face), crop, eyes, now, suggested);
        }

        /// <summary>Every group, shown or not, as the internal <c>All</c> gives them.</summary>
        public dynamic[] All
        {
            get
            {
                object target = groups;
                var all = target.GetType().GetProperty("All", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("FaceGroups has no All");
                return ((IEnumerable<object>)all.GetValue(target)!).Select(group => (dynamic)group).ToArray();
            }
        }

        public dynamic[] Shown => ((IEnumerable<object>)groups.Shown).Select(group => (dynamic)group).ToArray();
        public dynamic[] Samples(string id) => ((IEnumerable<object>)groups.Samples(id)).Select(sample => (dynamic)sample).ToArray();
        public dynamic Find(string id) => groups.Find(id);
        public byte[]? Crop(string id) => groups.Crop(id);
        public void Remove(string id) => groups.Remove(id);
        public void Expire(DateTimeOffset now, TimeSpan keep) => groups.Expire(now, keep);
        public void Rematch(object people) => groups.Rematch((dynamic)people, DefaultRules);
    }

    static bool Holds(dynamic[] samples, float[] face) => samples.Any(sample => ((float[])sample.Embedding.Values).SequenceEqual(face));

    static void AFaceStartsAGroupAndACloseOneJoinsIt()
    {
        using var home = new Home();
        var a = Unit(7000);
        var b = At(a, 0.55, 7001);
        double score = (float)Embedding(b).Similarity(Embedding(a));
        var c = At(a, score - 0.01, 7002);
        Check(Cosine(b, c) < score - 0.01, $"the face just under is not close to the one that joined either: {Cosine(b, c)}");
        var groups = Groups(home.Device(), (object)Rules(score));
        groups.See(a, T0);
        var started = groups.All;
        Check(started.Length == 1 && started[0].Sightings == 1 && started[0].First == T0 && started[0].Last == T0 && started[0].SuggestedPersonId == null,
            "a face starts a group: " + string.Join(", ", started));
        Check(groups.Shown.Length == 0, "a group of one sighting is not shown");
        groups.See(b, T0.AddSeconds(1));
        groups.See(c, T0.AddSeconds(2));
        var all = groups.All.OrderBy(group => (DateTimeOffset)group.First).ToArray();
        Check(all.Length == 2 && all[0].Id == started[0].Id && all[0].Sightings == 2 && all[0].Last == T0.AddSeconds(1),
            "the face at the match score exactly joined the group: " + string.Join(", ", all));
        Check(all[1].Sightings == 1 && all[1].First == T0.AddSeconds(2), "the face just under it started another");
        Check(groups.Samples((string)all[0].Id).Length == 2, "the face that joined is one of the group's samples");
        Check(groups.Find((string)all[1].Id).Equals(all[1]) && groups.Find("nosuchgroup") == null, "Find gives a group by its id, shown or not");
        Console.WriteLine("PASS people: a face starts a group, one at the match score from a sample joins it, and one just under starts another");
    }

    static void TwoGroupsAFaceIsCloseToAreJoinedIntoTheOlder()
    {
        using var home = new Home();
        var data = home.Device();
        var groups = Groups(data);
        var (a, c) = (Unit(7010), Unit(7011));
        var d = Normalised(a.Zip(c).Select(pair => (double)pair.First + pair.Second).ToArray());
        Check(Cosine(a, c) < 0.3 && Cosine(d, a) >= 0.5 && Cosine(d, c) >= 0.5 && Cosine(d, a) < 0.9, "d is close to both, and they are not to each other");
        for (var i = 0; i < 3; i++) groups.See(a, T0.AddSeconds(i), seed: 7010);
        for (var i = 3; i < 6; i++) groups.See(c, T0.AddSeconds(i), seed: 7011);
        var shown = groups.Shown;
        Check(shown.Length == 2, "two groups are shown");
        var older = shown.Single(group => group.First == T0);
        var younger = shown.Single(group => group.First == T0.AddSeconds(3));
        Check(shown[0].Id == younger.Id, "the newest sighting first");

        groups.See(d, T0.AddSeconds(6), seed: 7012);
        var joined = groups.All;
        Check(joined.Length == 1 && joined[0].Id == older.Id && joined[0].Sightings == 7 && joined[0].First == T0 && joined[0].Last == T0.AddSeconds(6),
            "the two are joined into the older, with every sighting: " + string.Join(", ", joined));
        var samples = groups.Samples((string)older.Id);
        Check(samples.Length == 3 && Holds(samples, a) && Holds(samples, c) && Holds(samples, d), $"the group has the samples of both and the new face: {samples.Length}");
        Check(samples.All(sample => sample.Camera == Camera && ((byte[])sample.CropJpeg).Length == 600), "each sample has its camera and its crop");
        Check(data.Keys($"groups/{younger.Id}").Count == 0, "none of the younger group's keys is left: " + string.Join(", ", data.Keys()));
        var next = Groups(home.RestartedDevice());
        Check(next.All.Single().Id == older.Id && next.Samples((string)older.Id).Length == 3, "the join is stored");
        Console.WriteLine("PASS people: two groups a new face is close to are joined into the older");
    }

    static void AGroupIsShownAtItsThirdSightingAndAFrameCountsOnce()
    {
        using var home = new Home();
        var groups = Groups(home.Device());
        var a = Unit(7020);
        // Two faces of one group in one frame: one check, one sighting.
        groups.See(a, T0);
        groups.See(At(a, 0.95, 7021), T0);
        Check(groups.All.Single().Sightings == 1, "two faces in one check are one sighting");
        groups.See(a, T0.AddSeconds(1));
        Check(groups.All.Single().Sightings == 2 && groups.Shown.Length == 0, "two sightings are not shown");
        groups.See(a, T0.AddSeconds(2));
        var shown = groups.Shown;
        Check(shown.Length == 1 && shown[0].Sightings == 3 && shown[0].Last == T0.AddSeconds(2), "the third sighting shows the group: " + string.Join(", ", shown));
        Console.WriteLine("PASS people: a group is shown at its third sighting, and the faces of one check are one sighting");
    }

    static void GroupsExpire()
    {
        using var home = new Home();
        var data = home.Device();
        var groups = Groups(data);
        var keep = TimeSpan.FromDays(7);
        var (a, c) = (Unit(7030), Unit(7031));
        groups.See(a, T0);
        groups.See(a, T0.AddMinutes(1));
        for (var i = 0; i < 3; i++) groups.See(c, T0.AddSeconds(i));
        string unshown = groups.All.Single(group => group.Sightings == 2).Id, shown = groups.Shown.Single().Id;

        groups.Expire(T0.AddMinutes(59), keep);
        Check(groups.All.Length == 2, "an unshown group stays for its first hour");
        groups.Expire(T0.AddHours(1), keep);
        Check(groups.All.Single().Id == shown && data.Keys($"groups/{unshown}").Count == 0, "an unshown group is gone after an hour, with its keys");
        var last = T0.AddSeconds(2);
        groups.Expire(last + keep - TimeSpan.FromSeconds(1), keep);
        Check(groups.Shown.Length == 1, "a shown group stays for the keep time");
        groups.Expire(last + keep, keep);
        Check(groups.All.Length == 0 && groups.Shown.Length == 0 && data.Keys("groups/").Count == 0, "a shown group is gone after the keep time: " + string.Join(", ", data.Keys()));

        // A face of a group not shown within its hour starts another, also before an Expire.
        var later = T0.AddDays(10);
        groups.See(a, later);
        groups.See(a, later.AddMinutes(1));
        groups.See(a, later.AddMinutes(61));
        var again = groups.All.Single();
        Check(again.First == later.AddMinutes(61) && again.Sightings == 1, "the late sighting starts a group of its own: " + again);
        Console.WriteLine("PASS people: an unshown group is gone after an hour, and a shown one after the keep time");
    }

    static void The51stGroupDropsTheOneSeenLongestAgo()
    {
        using var home = new Home();
        var data = home.Device();
        var groups = Groups(data);
        var faces = Enumerable.Range(0, 51).Select(i => Unit(7100 + i)).ToArray();
        var closest = faces.SelectMany((face, i) => faces.Skip(i + 1).Select(other => Cosine(face, other))).Max();
        Check(closest < 0.5, $"the 51 faces are each other's strangers: {closest}");
        for (var i = 0; i < 50; i++) groups.See(faces[i], T0.AddSeconds(i));
        groups.See(faces[0], T0.AddSeconds(100));
        Check(groups.All.Length == 50, "50 groups");
        string longestAgo = groups.All.Single(group => group.First == T0.AddSeconds(1)).Id;
        groups.See(faces[50], T0.AddSeconds(101));
        var all = groups.All;
        Check(all.Length == 50 && all.All(group => group.Id != longestAgo), "the group seen longest ago is gone");
        Check(all.Any(group => group.First == T0) && all.Any(group => group.First == T0.AddSeconds(101)), "the first group, seen again, stays, and the new one is kept");
        Check(data.Keys($"groups/{longestAgo}").Count == 0, "none of its keys is left");
        Check(Groups(home.RestartedDevice()).All.Length == 50, "50 groups are stored");
        Console.WriteLine("PASS people: the 51st group drops the one seen longest ago");
    }

    static void AGroupKeepsFiveSamplesPreferringLargerAndUnlikeFaces()
    {
        using var home = new Home();
        var data = home.Device();
        var groups = Groups(data);
        int[] crops = [0];
        var a = Unit(7200);
        groups.See(a, T0, eyes: 20, seed: 7200, crops: crops);
        string id = groups.All.Single().Id;
        Check(crops[0] == 1, "the first face's crop is made once");
        groups.See(At(a, 0.95, 7201), T0.AddSeconds(1), eyes: 18, seed: 7201, crops: crops);
        Check(crops[0] == 1 && groups.Samples(id).Length == 1, "a smaller face like a sample is not kept, and its crop is not made");
        var larger = At(a, 0.95, 7202);
        groups.See(larger, T0.AddSeconds(2), eyes: 30, seed: 7202, crops: crops);
        var samples = groups.Samples(id);
        Check(crops[0] == 2 && samples.Length == 1 && Holds(samples, larger) && ((byte[])samples[0].CropJpeg).SequenceEqual(Crop(7202)),
            "a larger face like a sample takes its place, with its crop");

        var unlike = Enumerable.Range(0, 6).Select(i => At(a, 0.7, 7210 + i)).ToArray();
        Check(unlike.All(face => Cosine(face, larger) is >= 0.5 and < 0.9), "each unlike face joins the group and adds something to it");
        Check(unlike.SelectMany((face, i) => unlike.Skip(i + 1).Select(other => Cosine(face, other))).Max() < 0.9, "the unlike faces are unlike each other");
        for (var i = 0; i < 4; i++) groups.See(unlike[i], T0.AddSeconds(3 + i), eyes: 25, seed: 7210 + i, crops: crops);
        Check(crops[0] == 6 && groups.Samples(id).Length == 5, "four unlike faces fill the group's five samples");
        groups.See(unlike[4], T0.AddSeconds(10), eyes: 40, seed: 7214, crops: crops);
        samples = groups.Samples(id);
        Check(crops[0] == 7 && samples.Length == 5 && Holds(samples, unlike[4]) && Holds(samples, larger), "a larger unlike face is kept in place of a smallest one");
        Check(unlike.Take(4).Count(face => !Holds(samples, face)) == 1, "one of the smallest went");
        groups.See(unlike[5], T0.AddSeconds(11), eyes: 10, seed: 7215, crops: crops);
        samples = groups.Samples(id);
        Check(crops[0] == 7 && samples.Length == 5 && !Holds(samples, unlike[5]), "a smaller face is not kept in a full group, and its crop is not made");
        Check(data.Keys($"groups/{id}/").Count == 10, "the store has the five samples' embeddings and crops only: " + string.Join(", ", data.Keys($"groups/{id}/")));
        Check(groups.All.Single().Sightings == 9, "every check was a sighting");
        Console.WriteLine("PASS people: a group keeps at most five samples, preferring larger faces and unlike ones, and makes a crop only for a face it keeps");
    }

    static void RematchRemovesRecognisedGroupsAndUpdatesSuggestions()
    {
        using var home = new Home();
        var data = home.Device();
        var groups = Groups(data);
        var (a, c) = (Unit(7300), Unit(7301));
        for (var i = 0; i < 3; i++) groups.See(a, T0.AddSeconds(i));
        for (var i = 0; i < 3; i++) groups.See(c, T0.AddSeconds(i), suggested: i == 0 ? "ivan" : null);
        string aId = groups.Shown.Single(group => group.First == T0 && group.SuggestedPersonId == null).Id;
        string cId = groups.All.Single(group => group.Id != aId).Id;
        Check(groups.Find(cId).SuggestedPersonId == "ivan", "a sighting's suggestion is kept, and one without leaves it");

        groups.Rematch((object)People(Person("ana", At(a, 0.8, 7302)), Person("mia", At(c, 0.45, 7303))));
        var left = groups.All;
        Check(left.Length == 1 && left[0].Id == cId && data.Keys($"groups/{aId}").Count == 0, "the group that is now Ana is removed, keys and all");
        Check(left[0].SuggestedPersonId == "mia", "the group that is now a maybe for Mia suggests her: " + left[0]);
        groups.Rematch((object)People(Person("ana", At(a, 0.8, 7302))));
        Check(groups.Find(cId).SuggestedPersonId == null, "a suggestion whose person is gone is cleared");
        Check(Groups(home.RestartedDevice()).All.Single().SuggestedPersonId == null, "the cleared suggestion is stored");
        Console.WriteLine("PASS people: matching the groups again removes one that is now someone, and updates and clears suggestions");
    }

    static void TheNextRevisionReadsTheSameGroups()
    {
        using var home = new Home();
        var groups = Groups(home.Device());
        var (a, c) = (Unit(7400), Unit(7401));
        var b = At(a, 0.7, 7402);
        groups.See(a, T0, eyes: 30, suggested: "ivan", seed: 7400);
        groups.See(b, T0.AddSeconds(1), seed: 7402);
        groups.See(a, T0.AddSeconds(2), seed: 7400);
        groups.See(c, T0.AddSeconds(3), seed: 7401);
        var next = Groups(home.RestartedDevice());
        string Listed(Faces g) => string.Join(" | ", g.All.Select(group => (string)group.ToString()).Order(StringComparer.Ordinal));
        Check(Listed(next) == Listed(groups), $"the same groups: {Listed(next)} against {Listed(groups)}");
        Check(next.Shown.Length == 1 && next.Shown[0].Equals(groups.Shown[0]), "the same group is shown");
        string id = next.Shown[0].Id;
        var samples = next.Samples(id);
        Check(samples.Length == 2 && Holds(samples, a) && Holds(samples, b), "the same samples, embeddings bit for bit");
        var first = samples.Single(sample => ((float[])sample.Embedding.Values).SequenceEqual(a));
        Check(((byte[])first.CropJpeg).SequenceEqual(Crop(7400)) && first.At == T0 && first.Camera == Camera && first.Embedding.Fit == 1.5f, "with its crop, time, camera and fit");
        Check(next.Crop(id)!.SequenceEqual(Crop(7400)), "the group's crop is its largest face's");
        Console.WriteLine("PASS people: the detector's next revision reads the same groups from its store");
    }

    /// <summary>Each file of a store's directory with its write time and bytes: two differ when a write happened in between.</summary>
    static Dictionary<string, string> Files(string directory) => !Directory.Exists(directory) ? [] :
        Directory.EnumerateFiles(directory).ToDictionary(file => Path.GetFileName(file), file => File.GetLastWriteTimeUtc(file).Ticks + ":" + Convert.ToHexString(File.ReadAllBytes(file)));

    static void LastSeenAndCountAreWrittenAtMostOnceAMinute()
    {
        using var home = new Home();
        var groups = Groups(home.Device());
        var face = Unit(7500);
        var written = new List<int>();
        var before = Files(home.DeviceDirectory);
        for (var s = 0; s <= 130; s++)
        {
            groups.See(face, T0.AddSeconds(s));
            var after = Files(home.DeviceDirectory);
            if (after.Count != before.Count || after.Any(file => !before.TryGetValue(file.Key, out var was) || was != file.Value)) written.Add(s);
            before = after;
        }
        // Started, shown, then once a minute.
        Check(written.SequenceEqual([0, 2, 62, 122]), "written at: " + string.Join(", ", written));
        Check(groups.All.Single().Sightings == 131, "every check is counted in memory");
        var stored = Groups(home.RestartedDevice()).All.Single();
        Check(stored.Sightings == 123 && stored.Last == T0.AddSeconds(122), "the store has the count and last seen of the last write: " + stored);
        Console.WriteLine("PASS people: a group's last seen and count are written at most once a minute, and when it is first shown");
    }

    static void ARemovedGroupLeavesNoFaceData()
    {
        using var home = new Home();
        var data = home.Device();
        var groups = Groups(data);
        var (a, c) = (Unit(7600), Unit(7601));
        for (var i = 0; i < 3; i++) groups.See(a, T0.AddSeconds(i), seed: 7600);
        groups.See(c, T0.AddSeconds(3), seed: 7601);
        string aId = groups.Shown.Single().Id;
        Check(groups.Crop(aId)!.SequenceEqual(Crop(7600)), "the group has a crop");
        groups.Remove(aId);
        Check(groups.Find(aId) == null && groups.Shown.Length == 0 && data.Keys($"groups/{aId}").Count == 0, "the group is gone, keys and all");
        Check(groups.Samples(aId).Length == 0 && groups.Crop(aId) == null, "it has no samples and no crop");
        groups.Remove(aId);
        groups.Remove("nosuchgroup");
        Check(Groups(home.RestartedDevice()).All.Single().First == T0.AddSeconds(3), "the store has only the other group");
        Console.WriteLine("PASS people: a group removed leaves none of its keys");
    }

    static void WhatAnInterruptionLeavesOfGroupsIsCleanedUp()
    {
        using var home = new Home();
        var data = home.Device();
        var groups = Groups(data);
        for (var i = 0; i < 3; i++) groups.See(Unit(7700), T0.AddSeconds(i), seed: 7700);
        string id = groups.Shown.Single().Id;
        // A group started or removed half way: its record and samples without its id in the index.
        data.Set("groups/orphan000001", new { first = T0, last = T0, sightings = 1, samples = new[] { new { id = "sample000001", at = T0, eyes = 20.0, fit = 1.5f } } });
        data.SetBytes("groups/orphan000001/sample000001/embedding", new byte[512]);
        data.SetBytes("groups/orphan000001/sample000001/crop", Crop(7701));
        // A sample written before its group's record listed it.
        data.SetBytes($"groups/{id}/stray0000001/embedding", new byte[512]);
        data.SetBytes($"groups/{id}/stray0000001/crop", Crop(7702));
        // An index that lists a group whose record is gone.
        data.Update<List<string>>("groups", index => [.. index!, "norecord0001"]);
        data.SetBytes("groups/norecord0001/sample000002/crop", Crop(7703));

        var next = Groups(home.RestartedDevice());
        var stored = home.RestartedDevice();
        Check(stored.Keys("groups/orphan000001").Count == 0 && stored.Keys("groups/norecord0001").Count == 0 && stored.Keys($"groups/{id}/stray0000001/").Count == 0,
            "nothing of them is left: " + string.Join(", ", stored.Keys()));
        Check(next.All.Single().Id == id && next.Samples(id).Length == 1 && stored.Keys($"groups/{id}/").Count == 2, "the group is kept with its sample");
        Check(stored.Get<List<string>>("groups")!.SequenceEqual([id]), "the index lists it alone");

        // An index that cannot be read: every group goes, as it is a detector's own cache of faces.
        stored.SetBytes("groups", [1, 2, 3]);
        var fresh = Groups(home.RestartedDevice());
        var check = home.RestartedDevice();
        Check(fresh.All.Length == 0 && check.Keys("groups/").Count == 0, "no group is left: " + string.Join(", ", check.Keys()));
        fresh.See(Unit(7704), T0.AddMinutes(5));
        Check(fresh.All.Length == 1 && Groups(home.RestartedDevice()).All.Length == 1, "and the groups are written again");
        Console.WriteLine("PASS people: what an interrupted change of the groups leaves is removed when the groups are next loaded");
    }

    static void ANewerFormatOfGroupsIsNeitherReadNorWritten()
    {
        using var home = new Home();
        var data = home.Device();
        var groups = Groups(data);
        var a = Unit(7800);
        for (var i = 0; i < 3; i++) groups.See(a, T0.AddSeconds(i));
        string id = groups.Shown.Single().Id;
        foreach (var format in new object[] { 2, "one" })
        {
            data.Set("format", format);
            var before = Contents(home.RestartedDevice());
            var newer = Groups(home.RestartedDevice());
            Check(newer.All.Length == 0 && newer.Shown.Length == 0, $"a store of format {format} is not read");
            for (var i = 0; i < 3; i++) newer.See(a, T0.AddMinutes(1 + i));
            newer.See(Unit(7801), T0.AddMinutes(5));
            newer.Rematch((object)People());
            newer.Remove(id);
            newer.Expire(T0.AddDays(30), TimeSpan.FromDays(7));
            Check(newer.Samples(id).Length == 0 && newer.Crop(id) == null && newer.All.Length == 0, "nor are groups made");
            Check(Contents(home.RestartedDevice()) == before, $"nothing was written to a store of format {format}");
        }
        Console.WriteLine("PASS people: a store of groups of a newer format is neither read nor written");
    }

    static void AClearedStoreGetsNoGroupBack()
    {
        using var home = new Home();
        var data = home.Device();
        var groups = Groups(data);
        var (a, c) = (Unit(7900), Unit(7901));
        for (var i = 0; i < 3; i++) groups.See(a, T0.AddSeconds(i));
        groups.See(c, T0.AddSeconds(3));
        var old = groups.All.Select(group => (string)group.Id).ToArray();
        // The store is cleared while the detector runs.
        data.Clear();
        groups.See(a, T0.AddSeconds(4));
        var all = groups.All;
        Check(all.Length == 1 && all[0].Sightings == 1 && all[0].First == T0.AddSeconds(4) && groups.Shown.Length == 0, "the groups from before are forgotten: " + string.Join(", ", all));
        var keys = home.RestartedDevice().Keys();
        Check(old.All(id => !keys.Any(key => key.Contains(id))), "none of them is written back: " + string.Join(", ", keys));
        Console.WriteLine("PASS people: a store cleared while the detector runs gets none of its groups back");
    }

    // ---- Learning ----

    /// <summary>A person of made-up samples, each named or learned at a time.</summary>
    static dynamic PersonOf(string id, params (float[] Values, bool Learned, DateTimeOffset At)[] samples)
    {
        var array = Array.CreateInstance(Type("FaceSample"), samples.Length);
        for (var i = 0; i < samples.Length; i++)
            array.SetValue(Activator.CreateInstance(Type("FaceSample"), $"{id}s{i}", (object)Embedding(samples[i].Values), samples[i].At, Camera, samples[i].Learned), i);
        return Activator.CreateInstance(Type("Person"), id, id, DateTimeOffset.UnixEpoch, array, null, null)!;
    }

    /// <summary>Whether a face of <paramref name="eyes"/> pixels between the eyes, recognised in <paramref name="people"/> by the default rules, is learned.</summary>
    static bool Learns(float[] face, double eyes, object people, DateTimeOffset now)
    {
        var match = Match(face, people);
        Check(match.Kind.ToString() == "Recognised" && match.Person.Id == "ana", $"the face is recognised as Ana: {match}");
        return (bool)Invoke(Type("FaceMatcher").GetMethod("Learns")!, null, (object)Embedding(face), eyes, (object)match, now, (object)DefaultRules)!;
    }

    static readonly DateTimeOffset Now = T0.AddDays(1);

    static void AFaceIsLearnedWhenAllFiveHold()
    {
        var face = Unit(8000);
        var people = People(PersonOf("ana", (At(face, 0.75, 8001), false, T0), (At(face, 0.2, 8002), true, Now.AddMinutes(-10))), Person("ivan", At(face, 0.3, 8003)));
        Check(Learns(face, 30, people, Now), "a face over every line is learned");
        Check(Learns(face, 24, people, Now), "24 pixels between the eyes are enough");
        Console.WriteLine("PASS people: a face over the learn score and margin, anchored on a named sample, large, new and the first in ten minutes is learned");
    }

    static void AFaceUnderTheLearnScoreIsNotLearned()
    {
        var face = Unit(8010);
        Check(!Learns(face, 30, People(PersonOf("ana", (At(face, 0.58, 8011), false, T0)), Person("ivan", At(face, 0.2, 8012))), Now), "a face at 0.58 is recognised, not learned");
        Console.WriteLine("PASS people: a face under the learn score is not learned");
    }

    static void AFaceUnderTheLearnMarginIsNotLearned()
    {
        var face = Unit(8020);
        Check(!Learns(face, 30, People(PersonOf("ana", (At(face, 0.75, 8021), false, T0)), Person("ivan", At(face, 0.63, 8022))), Now), "a face 0.12 ahead of the next is recognised, not learned");
        Console.WriteLine("PASS people: a face under the learn margin is not learned");
    }

    static void AFaceAnchoredOnlyOnALearnedSampleIsNotLearned()
    {
        var face = Unit(8030);
        var ivan = Person("ivan", At(face, 0.2, 8033));
        // Ana's only sample over the match score is a learned one: a learned sample never qualifies another.
        Check(!Learns(face, 30, People(PersonOf("ana", (At(face, 0.45, 8031), false, T0), (At(face, 0.75, 8032), true, T0)), ivan), Now), "no named sample anchors it");
        Check(Learns(face, 30, People(PersonOf("ana", (At(face, 0.55, 8031), false, T0), (At(face, 0.75, 8032), true, T0)), ivan), Now), "a named sample over the match score does");
        Console.WriteLine("PASS people: a face whose only sample over the match score is a learned one is not learned");
    }

    static void ASmallFaceIsNotLearned()
    {
        var face = Unit(8040);
        Check(!Learns(face, 23.9, People(PersonOf("ana", (At(face, 0.75, 8041), false, T0)), Person("ivan", At(face, 0.2, 8042))), Now), "23.9 pixels between the eyes are too few");
        Console.WriteLine("PASS people: a face under the learn size is not learned");
    }

    static void AFaceThatAddsNothingIsNotLearned()
    {
        var face = Unit(8050);
        var people = People(PersonOf("ana", (At(face, 0.75, 8051), false, T0), (At(face, 0.91, 8052), true, T0)), Person("ivan", At(face, 0.2, 8053)));
        Check(!Learns(face, 30, people, Now), "a face at 0.91 from a sample adds nothing");
        Console.WriteLine("PASS people: a face at 0.9 or more from one of the person's samples is not learned");
    }

    static void OneLearnedSampleInTenMinutes()
    {
        var face = Unit(8060);
        var ivan = Person("ivan", At(face, 0.2, 8063));
        Check(!Learns(face, 30, People(PersonOf("ana", (At(face, 0.75, 8061), false, T0), (At(face, 0.2, 8062), true, Now.AddMinutes(-9))), ivan), Now),
            "a person who gained a learned sample nine minutes ago gains none");
        Check(Learns(face, 30, People(PersonOf("ana", (At(face, 0.75, 8061), false, T0), (At(face, 0.2, 8062), true, Now.AddMinutes(-10))), ivan), Now), "ten minutes later one is");
        Console.WriteLine("PASS people: a person gains at most one learned sample in ten minutes");
    }

    // ---- Privacy ----

    /// <summary>No line logged while the suite ran has a name it gave, or a crop's bytes, as text, hex or base64.</summary>
    static void NoNameOrCropIsLogged()
    {
        string[] lines;
        string[] given;
        lock (logging) (lines, given) = ([.. logged], [.. names]);
        Check(given.Length > 30, $"the suite gave names: {given.Length}");
        var marker = Encoding.ASCII.GetBytes(CropMarker);
        var forms = new[] { CropMarker, Convert.ToHexString(marker), Convert.ToHexStringLower(marker), Convert.ToBase64String(marker)[..^4] };
        foreach (var line in lines)
        {
            Check(!given.Any(name => line.Contains(name, StringComparison.OrdinalIgnoreCase)), "a log line has a name: " + line.Length + " characters");
            Check(!forms.Any(form => line.Contains(form, StringComparison.Ordinal)), "a log line has a crop's bytes");
        }
        Console.WriteLine($"PASS people: none of the {lines.Length} lines logged has a name or a crop's bytes");
    }
}

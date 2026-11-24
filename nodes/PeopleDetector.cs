// hass-sharp-vision 1.1.0, from https://github.com/dompagoj/hass-sharp-vision
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>The camera of a <see cref="PeopleDetector"/>: each frame it checks, with the faces and people found drawn on it.</summary>
public class PeopleDetectorCamera : SharpCamera
{
    /// <summary>The camera keyed <c>camera</c>, with no name of its own: Home Assistant shows the device's.</summary>
    public PeopleDetectorCamera() : base("camera") { }
}

/// <summary>
/// Watches a Home Assistant camera and reports the faces and people it sees, and recognises the faces of the people you
/// name. Its page, on the Device tab, lists the faces it does not know, for you to name.
/// </summary>
/// <remarks>
/// <para>
/// The step list shows the summary above, so it names no member. The device reports whether a face or a person is seen
/// (held on for <see cref="StayOnFor"/> after the last), how many are in the newest frame checked, and when one was last
/// seen; and it has a camera that shows each frame checked with what was found drawn on it, its entities in the order
/// its constructor gives. With <see cref="RecogniseFaces"/> on, it recognises the clear faces of each frame against the
/// people list every People detector shares (<see cref="PeopleList"/>), says who is in frame (Recognised, held as seen
/// is), groups the faces it does not know (<see cref="FaceGroups"/>), and learns new looks of the people it is sure of;
/// its page lists the unknown faces to name or dismiss, the people to rename, merge and delete, and a person's samples
/// to remove.
/// </para>
/// <para>
/// Drop it in an entity definition as a node of its own and give it a camera; or derive a C# device node's class from
/// it, such as <c>public class FrontDoor : PeopleDetector { }</c>. It downloads the vision library pinned by Vision.cs
/// as the definition starts, and the face recognition model in the background once it has started. Setup, Start and
/// Stop are its own; a derived class may still add entities in its constructor.
/// </para>
/// </remarks>
[Node("People detector", Category = "Vision")]
public class PeopleDetector : SharpDevice
{
    // The side each model is given the frame at: faces at 640 find smaller faces than at 320; people at 416.
    const int FaceInput = 640, PersonInput = 416;
    // The most faces embedded in a check, the largest first.
    const int MostEmbedded = 4;
    // The long side of a face's crop, in pixels.
    const int CropSide = 160;
    // The longest a check waits for a frame: a camera that gives none fails the check.
    static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);
    // While one stays seen, last seen is written at most this often.
    static readonly TimeSpan LastSeenEvery = TimeSpan.FromSeconds(60);
    // How long after a failed open the recogniser is opened again, on a check.
    static readonly TimeSpan OpenAgainAfter = TimeSpan.FromMinutes(10);
    // How often at most the unknown groups are expired, and a face that failed to be recognised is logged.
    static readonly TimeSpan ExpireEvery = TimeSpan.FromMinutes(1), FailureLogEvery = TimeSpan.FromMinutes(1);
    // How often the page is drawn again while it lists anything, so that its times ("5 minutes ago") and counts stay true.
    static readonly TimeSpan RedrawEvery = TimeSpan.FromMinutes(1);
    // The most samples of a person: every named and every learned one.
    const int MostSamples = PeopleList.MostNamed + PeopleList.MostLearned;
    // The sentences of a press for what is gone, or cannot be done.
    const string FaceGone = "This face is no longer in the list.", PersonGone = "This person is no longer in the list.",
        SampleGone = "This sample is no longer in the list.", NoPicture = "This face has no picture left; dismiss it.",
        NoPerson = "Person is required.", NoOneToMergeInto = "Choose a person in Merge into first.";
    // The id of the page's set of options of the people, which the pick fields choose from.
    const string PeopleSet = "people";
    // The key of the detector's own data that keeps the last visit, for Recognised's last_recognised after a restart.
    const string VisitKey = "recognised";

    readonly PeopleDetectorCamera camera;
    readonly Watch faces, people;
    // Recognised: who is in frame.
    readonly SharpSensor who;
    VisionDetector? detector;
    SharpVideo? video;
    RecognitionRules rules = RecognitionRules.Default;
    string cameraId = "";

    // Guards recognizer, opening, stopped, openAgain and openFailure: the recogniser is opened in the background and
    // closed by Stop().
    readonly Lock recognition = new();
    IFaceRecognizer? recognizer;
    bool opening, stopped;
    DateTimeOffset openAgain;
    // Why the last open failed, so that a reason is logged as a warning once.
    string? openFailure;
    // Only the checks use these, one at a time.
    DateTimeOffset expireNext, failureLogged = DateTimeOffset.MinValue;

    // Held while the people list and the unknown groups are used together: a check's matching and noting of its faces
    // (not its embedding), each press that changes the list or the groups, and the matching again after another's change
    // of the list. So a check never matches or notes a face against a list older than the one the groups were last
    // matched against, nor in the middle of a press. Guards matchedAgainst. It is never held across an await, nor while
    // waiting for a frame, embedding a face or logging (a log line goes to Python), and the page is drawn outside it
    // (Closed takes page inside it; nothing takes it inside page). Guards visits too, which Recognised shows: who is in
    // frame is worked out with the matching, against the same list, and set on the sensor after the lock is let go (Show).
    // Two detectors on one store cannot wait on each other's: a write to a store holds only the store's own lock, and the
    // other users' Changed actions are queued (Task.Run) after that lock is let go, so a detector writing the shared store
    // under this lock never runs, or waits for, another detector's code; nor does the people list hold its lock across a
    // store call or its callback.
    readonly Lock lists = new();
    // The people list the groups were last matched against: a check that finds another (a change whose ListsChanged has
    // not run yet, or a write of its own) matches them again first.
    IReadOnlyList<Person>? matchedAgainst;
    readonly Visits visits = new();

    // Guards shownTicket and shown: Show() sets Recognised under it, never under lists.
    readonly Lock showing = new();
    // The ticket of the last Shown handed to Show(), and the last one set on the sensor.
    long shownTicket;
    Shown? shown;

    // Guards the page: each draw hands its lists to Ui under it; and samplesOf, drawnGroups and drawnTicket. Drawing runs
    // from the checks, the minute's timer, the handlers and the people list's Changed, which can run at the same time.
    readonly Lock page = new();
    // The person whose samples the page shows; none when it shows none.
    string? samplesOf;
    // The shown groups and their suggestions as last drawn (Key), so that a check draws again only when they change.
    string drawnGroups = "";
    // Each draw's ticket, in the order they began: a draw hands its lists over only when no later one has.
    long ticketsBegun, drawnTicket;

    /// <summary>The device with its camera and its seven sensors, in this order: face seen, person seen, faces, people, last face seen, last person seen, recognised.</summary>
    public PeopleDetector()
    {
        Manufacturer = "HassSharp";
        Model = "People detector";
        camera = Add(new PeopleDetectorCamera());
        var faceSeen = Add(new SharpBinarySensor("face_seen", "Face seen") { DeviceClass = "occupancy", RestoreState = false });
        var personSeen = Add(new SharpBinarySensor("person_seen", "Person seen") { DeviceClass = "occupancy", RestoreState = false });
        var faceCount = Add(new SharpSensor("faces", "Faces") { StateClass = "measurement", Icon = "mdi:face-recognition", RestoreState = false });
        var personCount = Add(new SharpSensor("people", "People") { StateClass = "measurement", Icon = "mdi:account-multiple", RestoreState = false });
        var lastFace = Add(new SharpSensor("last_face_seen", "Last face seen") { DeviceClass = "timestamp", RestoreState = true });
        var lastPerson = Add(new SharpSensor("last_person_seen", "Last person seen") { DeviceClass = "timestamp", RestoreState = true });
        who = Add(new SharpSensor("recognised", "Recognised") { Icon = "mdi:account-eye", RestoreState = false });
        faces = new Watch(faceSeen, faceCount, lastFace);
        people = new Watch(personSeen, personCount, lastPerson);
    }

    /// <summary>The camera to watch.</summary>
    [Setting("Camera")]
    public CameraEntity Camera { get; set; }

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

    /// <summary>Whether people are looked for: off, the person model is not run, and Person seen, People and Last person seen are unavailable.</summary>
    [Setting("Detect people")]
    public bool DetectPeople { get; set; } = true;

    /// <summary>Whether the faces seen are recognised against the people list: off, the face recognition model is not downloaded and nothing is matched.</summary>
    [Setting("Recognise faces")]
    public bool RecogniseFaces { get; set; } = true;

    /// <summary>The lowest score a face is recognised at, from 0.3 to 0.9: higher recognises fewer faces, and fewer wrongly.</summary>
    [Setting("Match score", Minimum = 0.3, Maximum = 0.9)]
    public double MatchScore { get; set; } = RecognitionRules.Default.MatchScore;

    /// <summary>Whether a face recognised beyond doubt is kept as a further sample of its person, so that new looks are learned.</summary>
    [Setting("Learn new looks")]
    public bool LearnNewLooks { get; set; } = true;

    /// <summary>How many days a face nobody has named is kept after it was last seen, from 1 to 365.</summary>
    [Setting("Keep unknown faces for", Minimum = 1, Maximum = 365)]
    public int KeepUnknownFacesFor { get; set; } = 7;

    /// <summary>Whether the camera picture shows a recognised face's name above its box.</summary>
    [Setting("Names on the camera")]
    public bool NamesOnTheCamera { get; set; } = true;

    /// <summary>The people list every People detector shares; set in <see cref="Start"/>, and only with <see cref="RecogniseFaces"/> on.</summary>
    protected PeopleList People { get; private set; } = null!;

    /// <summary>The faces this detector saw that nobody has named; set in <see cref="Start"/>, and only with <see cref="RecogniseFaces"/> on.</summary>
    protected FaceGroups Unknown { get; private set; } = null!;

    /// <summary>
    /// Opens the face recogniser: <see cref="Vision.OpenRecognizerAsync"/>, which downloads the model the first time.
    /// Called in the background from <see cref="Start"/>, and again on a check after it failed.
    /// </summary>
    protected virtual async Task<IFaceRecognizer> OpenRecognizer() =>
        await Vision.OpenRecognizerAsync(Files, RuntimeInformation.RuntimeIdentifier, Stopping);

    /// <summary>
    /// The time now, as the checks and the opening of the recogniser go by it: when a face, a person or a recognised
    /// person was seen, and how long ago that was. The system clock's, <see cref="DateTimeOffset.UtcNow"/>; a derived class
    /// may give another, as the tests do to end a hold when they choose.
    /// </summary>
    protected virtual DateTimeOffset Now => DateTimeOffset.UtcNow;

    /// <summary>Downloads the vision library for this platform, once, and opens its detector.</summary>
    protected sealed override async Task Setup()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        detector = await Vision.OpenAsync(Files, rid, Stopping);
        Log($"People detector ready ({rid})");
    }

    /// <summary>
    /// Opens the camera's live feed and checks its newest frame every <see cref="CheckEvery"/> seconds. With
    /// <see cref="RecogniseFaces"/> on, it opens the people list and the unknown groups first, and begins opening the
    /// recogniser; Recognised is Nobody, with the last visit this detector kept, and unavailable until the recogniser
    /// opens. With it off, Recognised is unavailable.
    /// </summary>
    protected sealed override Task Start()
    {
        video = SharpVideo.Open(Camera, new VideoOptions { MaxWidth = 1280 });
        if (!DetectPeople) people.Off();
        if (!RecogniseFaces) who.Available = false;
        if (RecogniseFaces)
        {
            rules = RecognitionRules.Default.WithMatchScore(MatchScore);
            cameraId = Camera.EntityId;
            // The groups first: the people list's callback uses them, and may run as soon as the list is made.
            Unknown = new FaceGroups(Data, cameraId, rules);
            People = new PeopleList(Shared(PeopleList.StoreName), ListsChanged);
            var kept = KeptVisit();
            Shown start;
            lock (lists)
            {
                // The list may have changed while this detector was off, and the match score with the settings.
                Rematch(People.People);
                start = visits.Restored(kept, People.People);
            }
            Show(start);
            Draw();
            Every(RedrawEvery, Refresh, immediately: false);
            BeginOpening(Now);
        }
        Every(TimeSpan.FromSeconds(CheckEvery), Check);
        return Task.CompletedTask;
    }

    /// <summary>Closes the camera's feed, the recogniser and the detector. It runs also when <see cref="Start"/> did not.</summary>
    protected sealed override async Task Stop()
    {
        if (video != null) await video.DisposeAsync();
        IFaceRecognizer? open;
        lock (recognition)
        {
            stopped = true;
            (open, recognizer) = (recognizer, null);
        }
        // No check runs by now: Stop() runs after the last. One still being opened is closed as it opens.
        open?.Dispose();
        detector?.Dispose();
        Log("People detector stopped", PyLogLevel.Debug);
    }

    // The people list changed on another detector: the unknown groups are matched against it again, and the page drawn.
    // It runs queued, never inside the other's write (see lists), so it may wait for the lock.
    Task ListsChanged()
    {
        // Another detector wrote while Start() was still making the list, before People was set: Start() matches and
        // draws right after.
        if (People == null) return Task.CompletedTask;
        Shown listed;
        lock (lists)
        {
            Rematch(People.People);
            listed = visits.Listed(People.People);
        }
        // A rename, a merge or a delete there shows on Recognised at once, not at the next face.
        Show(listed);
        Draw();
        return Task.CompletedTask;
    }

    // Under lists: the groups matched against the people list, which they are then in line with.
    void Rematch(IReadOnlyList<Person> everyone)
    {
        Unknown.Rematch(everyone, rules);
        matchedAgainst = everyone;
    }

    // ---- The recogniser ----

    // Begins opening the recogniser in the background, unless it is open, being opened, or failed less than
    // OpenAgainAfter ago.
    void BeginOpening(DateTimeOffset now)
    {
        lock (recognition)
        {
            if (stopped || recognizer != null || opening || now < openAgain) return;
            opening = true;
            openAgain = now + OpenAgainAfter;
        }
        _ = Open();
    }

    async Task Open()
    {
        IFaceRecognizer? opened = null;
        try { opened = await OpenRecognizer(); }
        catch (Exception e) when (!Stopping.IsCancellationRequested)
        {
            // A download or the library's start failed: neither message holds anything of a person.
            var reason = e.Message;
            bool again;
            lock (recognition)
            {
                again = openFailure == reason;
                openFailure = reason;
            }
            Log($"Faces are not recognised until the face recogniser opens, which is tried again later: {reason}", again ? PyLogLevel.Debug : PyLogLevel.Warn);
        }
        catch (Exception) { }
        lock (recognition)
        {
            opening = false;
            if (opened != null && !stopped) (recognizer, opened, openFailure) = (opened, null, null);
        }
        // Opened as the definition stopped.
        opened?.Dispose();
    }

    // The recogniser when it is open; else none, after beginning to open it again when that is due.
    IFaceRecognizer? Recognizer(DateTimeOffset now)
    {
        lock (recognition)
            if (recognizer != null) return recognizer;
        BeginOpening(now);
        lock (recognition) return recognizer;
    }

    // ---- A check ----

    // One check: the next frame, its faces and people, the faces recognised, the camera, and the entities. The timer runs
    // one at a time, and Stop() runs after the last, so nothing here is shared with another thread but the recogniser,
    // the people list and the groups, which are safe to share.
    async Task Check()
    {
        var hold = TimeSpan.FromSeconds(StayOnFor);
        // The people list before the check: a check that writes to it (a last seen, a learned sample) replaces it.
        IReadOnlyList<Person>? before = null;
        try
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(Stopping);
            waiting.CancelAfter(FrameTimeout);
            SharpImage? next;
            try { next = await video!.NextFrameAsync(waiting.Token); }
            catch (OperationCanceledException) when (waiting.IsCancellationRequested && !Stopping.IsCancellationRequested)
            {
                throw new TimeoutException($"no frame from {Camera.EntityId} within {FrameTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s");
            }
            if (next == null) return;
            using var frame = next;
            var foundFaces = detector!.Faces(frame, FaceInput, FaceScore);
            IReadOnlyList<DetectedPerson> foundPeople = DetectPeople ? detector.People(frame, PersonInput, PersonScore) : [];
            var now = Now;
            if (RecogniseFaces) before = People.People;
            var names = RecogniseFaces ? Recognise(frame, foundFaces, now, hold) : null;
            var drawing = Vision.DrawPeople(frame.Copy(), foundPeople);
            using (var drawn = names != null && NamesOnTheCamera ? Vision.DrawFaces(drawing, foundFaces, names) : Vision.DrawFaces(drawing, foundFaces)) camera.Show(drawn);
            faces.Checked(foundFaces.Count, now, hold);
            if (DetectPeople) people.Checked(foundPeople.Count, now, hold);
        }
        catch (Exception) when (!Stopping.IsCancellationRequested)
        {
            // The counts are unknown now; each seen goes off once its hold has passed, so none is left on.
            var now = Now;
            faces.Failed(now, hold);
            if (DetectPeople) people.Failed(now, hold);
            if (RecogniseFaces) CheckFailed(now, hold);
            throw;
        }
        // After the entities, so that drawing the page cannot fail them.
        if (before != null) Redraw(before);
    }

    // Recognises the usable faces of a frame, the largest first and at most MostEmbedded, and expires the unknown groups
    // when that is due. The faces are embedded first, outside lists; then, under it, matched against the people list as
    // it is now, with the groups in line with it, and noted. Gives each face's name, in the order of found: a recognised
    // face's person's, null for the others.
    // A face the matcher gives to a person, but that belongs to an unknown group known not to be them, is not theirs
    // (FaceGroups.KnownNotToBe: the matcher itself knows nothing of the groups). Two faces of one frame are two people,
    // so a person is given to one face of the check, the best match; another face matched to them goes to the unknown
    // faces, as any face not recognised does, without suggesting them. The group of each face not recognised is known not
    // to be the people the check recognised.
    // Who is in frame for Recognised is worked out under lists with the matching, also in a check without a face to
    // compare or with the recogniser not open (Recognised is unavailable then), and set after the lock is let go.
    string?[] Recognise(SharpImage frame, IReadOnlyList<DetectedFace> found, DateTimeOffset now, TimeSpan hold)
    {
        var names = new string?[found.Count];
        var recognizer = Recognizer(now);
        List<(int Index, FaceEmbedding Embedding)>? embedded = null;
        if (recognizer != null && found.Count > 0)
        {
            var chosen = Enumerable.Range(0, found.Count)
                .Where(index => Usable(found[index]))
                .OrderByDescending(index => Vision.EyeDistance(found[index]))
                .Take(MostEmbedded)
                .ToList();
            // Every face embedded first, then matched, then each noted, the largest first.
            embedded = [];
            foreach (var index in chosen)
            {
                try
                {
                    if (Embed(recognizer, frame, found[index]) is { } embedding) embedded.Add((index, embedding));
                }
                catch (Exception e) when (!Stopping.IsCancellationRequested) { Failed(e, now); }
            }
        }
        Matching? matching = null;
        Shown seen;
        lock (lists)
        {
            if (embedded != null) matching = MatchAndNote(frame, found, embedded, names, now);
            seen = visits.Checked(recognizer != null, matching?.Given ?? [], matching?.Unknown ?? 0, now, hold, People.People);
        }
        // Outside the lock: setting an entity publishes, a log line goes to Python, and the visit is written to the store.
        Show(seen);
        if (seen.Began is { } visit) Keep(visit);
        foreach (var failure in matching?.Failures ?? []) Failed(failure, now);
        if (now >= expireNext)
        {
            expireNext = now + ExpireEvery;
            lock (lists) Unknown.Expire(now, TimeSpan.FromDays(KeepUnknownFacesFor));
        }
        return names;
    }

    // Under lists: matches a check's embedded faces against the people list as it is now, with the groups brought in line
    // with it first when it changed since they were last matched, and notes each, setting the names of those recognised.
    // Gives the failures of the faces that could not be noted, for the caller to log; the ids of the people given a face,
    // the largest face's first; and how many faces were compared and given to no one (no match, a maybe, or a second face
    // of a person, or one of a group known not to be them), for Recognised.
    Matching MatchAndNote(SharpImage frame, IReadOnlyList<DetectedFace> found, List<(int Index, FaceEmbedding Embedding)> embedded, string?[] names, DateTimeOffset now)
    {
        var failures = new List<Exception>();
        var everyone = People.People;
        if (!ReferenceEquals(everyone, matchedAgainst)) Rematch(everyone);
        // One snapshot for the check: Learns reads it, which has no sample learned in this check.
        var matched = embedded
            .Select(face => new Matched(face.Index, found[face.Index], face.Embedding, Vision.EyeDistance(found[face.Index]), FaceMatcher.Match(face.Embedding, everyone, rules)))
            .ToList();
        var elsewhere = matched.Where(face => Unknown.KnownNotToBe(face.Embedding, face.Match)).Select(face => face.Index).ToHashSet();
        var given = matched.Where(face => face.Match.Kind == MatchKind.Recognised && face.Match.Person != null && !elsewhere.Contains(face.Index))
            .GroupBy(face => face.Match.Person!.Id, StringComparer.Ordinal)
            .Select(faces => faces.MaxBy(face => face.Match.Score)!)
            .ToList();
        var recognised = given.Select(face => face.Match.Person!.Id).ToList();
        var named = given.Select(face => face.Index).ToHashSet();
        foreach (var face in matched)
        {
            try
            {
                if (named.Contains(face.Index)) names[face.Index] = Note(frame, face, everyone, now);
                else
                {
                    // A maybe suggests its person, unless the face is known not to be them.
                    var suggested = face.Match is { Kind: MatchKind.Maybe, Person: { } maybe } && !elsewhere.Contains(face.Index) ? maybe.Id : null;
                    Unknown.Seen(face.Embedding, () => Crop(frame, face.Face), face.Eyes, now, suggested, recognised);
                }
            }
            catch (Exception e) when (!Stopping.IsCancellationRequested) { failures.Add(e); }
        }
        return new(failures, recognised, matched.Count - given.Count);
    }

    // What MatchAndNote gives back beside the names: the failures to log, the ids of the people given a face, and the
    // number of faces compared and given to no one.
    sealed record Matching(List<Exception> Failures, IReadOnlyList<string> Given, int Unknown);

    // A face clear enough to embed: large enough, and looking towards the camera.
    bool Usable(DetectedFace face) => Vision.EyeDistance(face) >= rules.UsableEyes && Vision.Frontal(face);

    // A face embedded; null when it fits too badly to be compared at all.
    FaceEmbedding? Embed(IFaceRecognizer recognizer, SharpImage frame, DetectedFace face)
    {
        var embedding = recognizer.Embed(frame, face);
        // Too far from where an aligned face has its landmarks to be compared at all.
        return embedding.Fit <= rules.FitLimit ? embedding : null;
    }

    // A recognised face, noted as a sighting of its person, and learned when it meets the rules. Gives what the camera
    // names its person by among everyone (PeopleList.Label). Nothing of it is logged. A face not recognised is cut, by
    // Seen, only when a group keeps it, during the call: the frame is still the check's.
    string Note(SharpImage frame, Matched face, IReadOnlyList<Person> everyone, DateTimeOffset now)
    {
        var (embedding, eyes, match, person) = (face.Embedding, face.Eyes, face.Match, face.Match.Person!);
        People.Seen(person.Id, now, cameraId);
        // A person has one face in a check, so is learned from at most once in it.
        if (LearnNewLooks && FaceMatcher.Learns(embedding, eyes, match, now, rules))
        {
            // Refused when the person was deleted meanwhile: the sample is dropped.
            try { People.AddSamples(person.Id, [new NewSample(embedding, Crop(frame, face.Face), now, cameraId)], learned: true); }
            catch (PeopleRefusal) { }
        }
        return PeopleList.Label(person, everyone);
    }

    // A face of a check, embedded and matched: its index in the faces found, the face, its embedding, the pixels between
    // its eyes, and its match.
    sealed record Matched(int Index, DetectedFace Face, FaceEmbedding Embedding, double Eyes, FaceMatch Match);

    // A face's crop, as a JPEG: its box grown by a quarter of its size on each side, kept inside the picture, and resized
    // to CropSide pixels on its long side.
    static byte[] Crop(SharpImage frame, DetectedFace face)
    {
        int width = frame.Width, height = frame.Height;
        float growX = face.Width / 4, growY = face.Height / 4;
        var left = Math.Clamp((int)MathF.Floor(face.X - growX), 0, width - 1);
        var top = Math.Clamp((int)MathF.Floor(face.Y - growY), 0, height - 1);
        var right = Math.Clamp((int)MathF.Ceiling(face.X + face.Width + growX), left + 1, width);
        var bottom = Math.Clamp((int)MathF.Ceiling(face.Y + face.Height + growY), top + 1, height);
        int cropWidth = right - left, cropHeight = bottom - top;
        var scale = (double)CropSide / Math.Max(cropWidth, cropHeight);
        using var cut = frame.Crop(left, top, cropWidth, cropHeight);
        using var sized = cut.Resize(Math.Max(1, (int)Math.Round(cropWidth * scale)), Math.Max(1, (int)Math.Round(cropHeight * scale)));
        return sized.ToJpeg();
    }

    // A face that failed to be recognised, logged at most once a minute with the exception's type and message, which hold
    // no name: a PeopleRefusal's would, and is never logged.
    void Failed(Exception failure, DateTimeOffset now)
    {
        if (failure is PeopleRefusal || now - failureLogged < FailureLogEvery) return;
        failureLogged = now;
        Log($"A face could not be recognised: {failure.GetType().Name}: {failure.Message}", PyLogLevel.Warn);
    }

    // ---- Recognised ----

    // The longest state Home Assistant keeps, and what ends a state cut short at a whole name.
    const int LongestState = 255;
    const string More = ", …";

    // A check failed: Recognised loses only whom the hold has passed for, as seen does.
    void CheckFailed(DateTimeOffset now, TimeSpan hold)
    {
        Shown after;
        lock (lists) after = visits.Failed(now, hold, People.People);
        Show(after);
    }

    // Sets Recognised to what was worked out under lists, after the lock is let go: setting an entity publishes. The
    // checks, the presses and another detector's changes work one out each on their own thread, so one worked out earlier
    // can come here after a later one: each has the ticket of its making under lists, and one older than the last handed
    // here is dropped, so the sensor never goes back to an older state. The sensor is set only when what it shows changes,
    // so that an arrival or a leaving is one publish, not one each check.
    void Show(Shown next)
    {
        lock (showing)
        {
            if (next.Ticket <= shownTicket) return;
            shownTicket = next.Ticket;
            if (shown != null && shown.Same(next)) return;
            shown = next;
            // New lists each time: the attributes are read by a publish on another thread.
            who.Attributes["people"] = next.Names;
            who.Attributes["first_names"] = next.FirstNames;
            who.Attributes["unknown_faces"] = next.UnknownFaces;
            who.Attributes["last_recognised"] = next.LastRecognised;
            who.Attributes["last_recognised_at"] = next.LastRecognisedAt;
            // The value publishes, with the attributes; set while unavailable, it is sent with the availability after it.
            who.Value = next.State;
            if (who.Available != next.Available) who.Available = next.Available;
        }
    }

    // A visit that began, kept in the detector's own data so that last_recognised comes back after a restart: written once
    // for each visit, by the check that began it, outside lists (the checks run one at a time). A store that refuses it,
    // full, loses only last_recognised at the next start.
    void Keep(Visit visit)
    {
        try { Data.Set(VisitKey, visit); }
        catch (InvalidDataException) { }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException) { }
    }

    // The visit kept; none when there is none, or it cannot be read.
    Visit? KeptVisit()
    {
        try { return Data.Get<Visit>(VisitKey) is { PersonId: not null } visit ? visit : null; }
        catch (InvalidDataException) { return null; }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException) { return null; }
    }

    // A visit: the person, by id, and when they were given a face while not in frame.
    sealed record Visit(string PersonId, DateTimeOffset At);

    // What Recognised shows: whether it is available, its state, and its attributes; with the visit the check began, to
    // keep, and the ticket that orders them as they were worked out under lists.
    sealed record Shown(long Ticket, bool Available, string State, string[] Names, string[] FirstNames, int UnknownFaces, string? LastRecognised, DateTimeOffset? LastRecognisedAt, Visit? Began)
    {
        // Whether the sensor shows the same: all but the ticket and the visit begun.
        public bool Same(Shown other) =>
            Available == other.Available && State == other.State && Names.SequenceEqual(other.Names) && FirstNames.SequenceEqual(other.FirstNames)
            && UnknownFaces == other.UnknownFaces && LastRecognised == other.LastRecognised && LastRecognisedAt == other.LastRecognisedAt;
    }

    // Who Recognised says is in frame, by person id: each person a check gave a face within Stay on for, held as seen is,
    // and the faces of those checks given to no one; and the last visit. Used under lists only, so it is always of the
    // people list the faces were matched against; the names are written from the list as it is when Recognised is worked
    // out, so a rename shows at once, and a person who is no longer in it goes.
    sealed class Visits
    {
        // Each person in frame, by id: when their visit began, and the last check that gave them a face.
        readonly Dictionary<string, (DateTimeOffset Began, DateTimeOffset Last)> inFrame = new(StringComparer.Ordinal);
        // The checks within the hold that had faces given to no one, the oldest first, each with more of them than every
        // later one: so the first has the most, and there are at most MostEmbedded.
        readonly List<(DateTimeOffset At, int Count)> unknown = [];
        // The visit that began last, kept after the person leaves; the one kept in the store at the start.
        Visit? last;
        // Whether the recogniser was open at the last check.
        bool open;
        // The tickets handed out.
        long tickets;

        // At the start: nobody in frame, the visit kept, and not available until a check finds the recogniser open.
        public Shown Restored(Visit? kept, IReadOnlyList<Person> everyone)
        {
            last = kept;
            return Showing(everyone, null);
        }

        // A check: the people it gave a face, the largest face's first, and how many faces it gave to no one; with the
        // recogniser not open, none of either. A person not in frame begins a visit, and of several in one check the one
        // of the largest face is the last recognised.
        public Shown Checked(bool open, IReadOnlyList<string> given, int unknownFaces, DateTimeOffset now, TimeSpan hold, IReadOnlyList<Person> everyone)
        {
            this.open = open;
            Visit? began = null;
            foreach (var id in given)
            {
                if (inFrame.TryGetValue(id, out var visit)) inFrame[id] = (visit.Began, now);
                else
                {
                    inFrame[id] = (now, now);
                    began ??= new Visit(id, now);
                }
            }
            if (unknownFaces > 0)
            {
                while (unknown.Count > 0 && unknown[^1].Count <= unknownFaces) unknown.RemoveAt(unknown.Count - 1);
                unknown.Add((now, unknownFaces));
            }
            Expire(now, hold);
            if (began != null) last = began;
            return Showing(everyone, began);
        }

        // A check that failed: whom the hold has passed for goes, and nothing before.
        public Shown Failed(DateTimeOffset now, TimeSpan hold, IReadOnlyList<Person> everyone)
        {
            Expire(now, hold);
            return Showing(everyone, null);
        }

        // The people list changed: the names as it has them now.
        public Shown Listed(IReadOnlyList<Person> everyone) => Showing(everyone, null);

        // Whom no check gave a face within the hold goes, and so do the faces given to no one of the checks before it; what
        // the check at now gave stays, also with no hold, as seen stays on.
        void Expire(DateTimeOffset now, TimeSpan hold)
        {
            foreach (var (id, visit) in inFrame.ToList())
                if (visit.Last != now && now - visit.Last >= hold) inFrame.Remove(id);
            unknown.RemoveAll(check => check.At != now && now - check.At >= hold);
        }

        // What Recognised shows now: the people in frame by full name, without those no longer in the list, which go from
        // the frame too; and the last visit's person when they are in it.
        Shown Showing(IReadOnlyList<Person> everyone, Visit? began)
        {
            var byId = everyone.ToDictionary(person => person.Id, StringComparer.Ordinal);
            foreach (var id in inFrame.Keys.Where(id => !byId.ContainsKey(id)).ToList()) inFrame.Remove(id);
            var here = inFrame.Keys.Select(id => byId[id])
                .OrderBy(person => person.FullName, StringComparer.OrdinalIgnoreCase).ThenBy(person => person.FullName, StringComparer.Ordinal)
                .ToList();
            string[] names = [.. here.Select(person => person.FullName)];
            var faces = unknown.Count > 0 ? unknown[0].Count : 0;
            var state = names.Length > 0 ? Joined(names) : faces > 0 ? "Unknown" : "Nobody";
            var lastPerson = last != null && byId.TryGetValue(last.PersonId, out var person) ? person : null;
            return new(++tickets, open, state, names, [.. here.Select(person => person.FirstName)], faces, lastPerson?.FullName, lastPerson != null ? last!.At : null, began);
        }

        // The names joined by ", ", or as many whole names as fit in LongestState with More after them.
        static string Joined(string[] names)
        {
            var all = string.Join(", ", names);
            if (all.Length <= LongestState) return all;
            // A full name is at most 129 characters, so the first always fits.
            var kept = names[0];
            for (var i = 1; i < names.Length && kept.Length + 2 + names[i].Length + More.Length <= LongestState; i++) kept += ", " + names[i];
            return kept + More;
        }
    }

    // ---- The page ----

    // Draws the page: the people as the set of options its pick fields choose from, first, then the unknown faces, the
    // people, and the samples of the person chosen, if any. The set and the cards are built from one snapshot of the list
    // and the stores outside the lock, then handed to Ui under it, the set before the lists, so that each card's choice is
    // one of the set's; a draw that a later one has overtaken hands nothing over, so the page never goes back to an older
    // state. A person whose samples are open and who is gone closes them. A draw as the definition stops, when the stores
    // and the page are closed, draws nothing.
    void Draw()
    {
        try { DrawNow(); }
        catch (ObjectDisposedException) when (Stopping.IsCancellationRequested) { }
    }

    void DrawNow()
    {
        var ticket = Interlocked.Increment(ref ticketsBegun);
        string? open;
        lock (page) open = samplesOf;
        var now = DateTimeOffset.UtcNow;
        var shown = Unknown.Shown;
        var everyone = People.People;
        var places = new Dictionary<string, string>(StringComparer.Ordinal);
        var views = everyone.Take(PeopleList.MostPeople).Select(person => View(person, now, places)).ToList();
        var options = views.Select(Option).ToList();
        var unknown = UnknownCards(shown, everyone, now);
        var people = PersonCards(views);
        var chosen = open == null ? null : everyone.FirstOrDefault(person => person.Id == open);
        var samples = chosen == null ? null : SampleCards(chosen, now, places);
        var unknownEmpty = Unknown.OfAnotherFormat ? "The unknown faces were saved by a newer hass-sharp-vision; update this one." : "No unknown faces.";
        var peopleEmpty = People.Problem ?? "Nobody is named yet.";
        lock (page)
        {
            if (ticket < drawnTicket) return;
            drawnTicket = ticket;
            drawnGroups = Key(shown);
            Ui.Options(PeopleSet, options);
            Ui.List("unknown", "Unknown faces", unknown, attention: true, empty: unknownEmpty);
            Ui.List("people", "People", people, empty: peopleEmpty);
            if (chosen != null) Ui.List("samples", $"Samples of {chosen.FullName}", samples!);
            else
            {
                // Closed, or the person is gone: a person chosen since is drawn by the draw that follows the choice.
                if (samplesOf == open) samplesOf = null;
                Ui.Remove("samples");
            }
        }
    }

    // After a check: the page is drawn again when the shown groups or their suggestions changed, or the check wrote to the
    // people list. A count or a time alone waits for the minute's redraw, as a check runs several times a second.
    void Redraw(IReadOnlyList<Person> before)
    {
        string drawn;
        lock (page) drawn = drawnGroups;
        if (ReferenceEquals(before, People.People) && Key(Unknown.Shown) == drawn) return;
        Draw();
    }

    // The minute's redraw, while the page lists anything whose time it shows.
    Task Refresh()
    {
        if (Unknown.Shown.Count > 0 || People.People.Count > 0) Draw();
        return Task.CompletedTask;
    }

    static string Key(IReadOnlyList<FaceGroup> shown) =>
        string.Join(",", shown.Select(group => $"{group.Id}:{group.SuggestedPersonId}").Order(StringComparer.Ordinal));

    // A person as the page shows them, on their card and as an option of the set: their samples and where they were last
    // seen, and their newest named face.
    sealed record PersonView(Person Person, string Detail, byte[]? Face);

    PersonView View(Person person, DateTimeOffset now, Dictionary<string, string> places)
    {
        var learned = person.Samples.Count(sample => sample.Learned);
        var samples = Count(person.Samples.Count, "sample") + (learned > 0 ? $" ({learned} learned)" : "");
        var seen = person.LastSeen is { } last ? $"last seen {Ago(last, now)}{At(person.LastSeenAt, places)}" : "not seen yet";
        var face = person.Samples.Where(sample => !sample.Learned).OrderByDescending(sample => sample.At)
            .Select(sample => People.Crop(sample.Id)).FirstOrDefault(crop => crop != null);
        return new(person, $"{samples} · {seen}", face);
    }

    // A person as an option of the set: their full name, their card's detail line, and their face when it is a JPEG the
    // page takes.
    static UiOption Option(PersonView view)
    {
        var option = new UiOption(view.Person.Id, view.Person.FullName, view.Detail);
        return IsJpeg(view.Face) ? option.Image(view.Face!) : option;
    }

    // A card for each shown group, the newest sighting first: its largest face, how often and when it was seen, the person
    // it might be, a Person to choose for it from the people (the suggestion's chosen) or a new person to make of it, Save
    // and Dismiss.
    List<UiCard> UnknownCards(IReadOnlyList<FaceGroup> shown, IReadOnlyList<Person> everyone, DateTimeOffset now)
    {
        // The pick field takes what it needs of the form when it is added, so one form serves every card.
        var newPerson = new UiForm("New person")
            .Field("first_name", "First name", required: true)
            .Field("last_name", "Last name");
        var cards = new List<UiCard>();
        foreach (var group in shown.Take(FaceGroups.MostGroups))
        {
            var id = group.Id;
            var suggested = group.SuggestedPersonId is { } personId ? everyone.FirstOrDefault(person => person.Id == personId) : null;
            cards.Add(Card(id, Unknown.Crop(id))
                .Text($"Seen {Count(group.Sightings, "time")} · last {Ago(group.Last, now)}", suggested == null ? null : $"Might be {suggested.FullName}")
                .Pick("person", "Person", PeopleSet, value: suggested?.Id, required: true, add: newPerson)
                .Action("Save", values => Pressed(() => SaveFace(id, values.Picked("person"), values.New("person"))), style: UiActionStyle.Primary)
                .Action("Dismiss", _ => Pressed(() => DismissFace(id)), validate: false));
        }
        return cards;
    }

    // A card for each person, by full name: their newest named face, their samples and where they were last seen, a First
    // name and a Last name to rename them with and Save; with someone else in the list, Merge into, which chooses from
    // the people but them, and Merge; Samples, and Delete.
    List<UiCard> PersonCards(IReadOnlyList<PersonView> views)
    {
        var cards = new List<UiCard>();
        foreach (var (person, detail, face) in views)
        {
            var id = person.Id;
            var card = Card(id, face)
                .Text(person.FullName, detail)
                .Field("first_name", "First name", person.FirstName, required: true)
                .Field("last_name", "Last name", person.LastName);
            // Merge into and Merge are there only when there is someone to merge into.
            var others = views.Count > 1;
            if (others) card.Pick("into", "Merge into", PeopleSet, except: [id]);
            card.Action("Save", values => Pressed(() => Rename(id, values["first_name"], values["last_name"])), style: UiActionStyle.Primary);
            // A question is fixed when the card is drawn, so it cannot name the person chosen.
            if (others)
                card.Action("Merge", values => Pressed(() => Merge(id, values.Picked("into"))), confirm: $"Merge {person.FullName} into the person chosen in Merge into?");
            cards.Add(card
                .Action("Samples", _ => Pressed(() => OpenSamples(id)), validate: false)
                .Action("Delete", _ => Pressed(() => Delete(id)), confirm: $"Delete {person.FullName} and their samples?", style: UiActionStyle.Danger, validate: false));
        }
        return cards;
    }

    // A card for each sample of a person, named first, then learned, the newest first: its face, what it is, when and where
    // it was seen, and Remove; the first has Close. A person without samples has one card, to close the list with.
    List<UiCard> SampleCards(Person person, DateTimeOffset now, Dictionary<string, string> places)
    {
        var personId = person.Id;
        var ordered = person.Samples.Where(sample => !sample.Learned).OrderByDescending(sample => sample.At)
            .Concat(person.Samples.Where(sample => sample.Learned).OrderByDescending(sample => sample.At))
            .Take(MostSamples);
        var cards = new List<UiCard>();
        foreach (var sample in ordered)
        {
            var sampleId = sample.Id;
            var card = Card(sampleId, People.Crop(sampleId))
                .Text($"{(sample.Learned ? "Learned" : "Named")} · {Ago(sample.At, now)}{At(sample.Camera, places)}")
                .Action("Remove", _ => Pressed(() => RemoveSample(personId, sampleId)), style: UiActionStyle.Danger, validate: false);
            if (cards.Count == 0) card.Action("Close", _ => Pressed(CloseSamples), validate: false);
            cards.Add(card);
        }
        if (cards.Count == 0) cards.Add(new UiCard("none").Text($"{person.FullName} has no samples left.").Action("Close", _ => Pressed(CloseSamples), validate: false));
        return cards;
    }

    // A card with its picture, or without one when the crop is gone or is not a JPEG the page takes.
    static UiCard Card(string id, byte[]? jpeg) => IsJpeg(jpeg) ? new UiCard(id).Image(jpeg!) : new UiCard(id);

    // Whether a crop is a JPEG the page takes as a card's or an option's picture.
    static bool IsJpeg(byte[]? jpeg) => jpeg is { Length: >= 2 and <= PeopleList.LargestCrop } && jpeg[0] == 0xFF && jpeg[1] == 0xD8;

    static string Count(int count, string what) => $"{count} {what}{(count == 1 ? "" : "s")}";

    // How long ago, in the largest whole unit up to days; a date beyond a week, as UTC has it.
    static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var ago = now - at;
        if (ago < TimeSpan.FromMinutes(1)) return "a moment ago";
        if (ago < TimeSpan.FromHours(1)) return Count((int)ago.TotalMinutes, "minute") + " ago";
        if (ago < TimeSpan.FromDays(1)) return Count((int)ago.TotalHours, "hour") + " ago";
        if (ago < TimeSpan.FromDays(7)) return Count((int)ago.TotalDays, "day") + " ago";
        return at.UtcDateTime.ToString(at.Year == now.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
    }

    // " at " the camera's friendly name, or its entity id when Home Assistant has no such camera now; nothing for none.
    string At(string? camera, Dictionary<string, string> places)
    {
        if (string.IsNullOrEmpty(camera)) return "";
        if (!places.TryGetValue(camera, out var place))
        {
            place = camera;
            try
            {
                if (State(camera).Attributes.TryGetProperty("friendly_name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String
                    && name.GetString() is { Length: > 0 } friendly)
                    place = friendly.Length > 100 ? friendly[..100] : friendly;
            }
            // A camera that is gone, or unavailable, is shown by its id.
            catch (Exception) { }
            places[camera] = place;
        }
        return $" at {place}";
    }

    // ---- The page's actions ----

    // Runs a press's change and draws the page after it, and Recognised, also when it is refused. A PeopleRefusal's sentence, which may
    // hold a name, is the card's and is not logged; nothing else here throws with a name.
    Task Pressed(Action change)
    {
        try { change(); }
        catch (PeopleRefusal refusal) { throw new UiRefusal(refusal.Message); }
        finally
        {
            // A rename, a merge or a delete shows on Recognised at once, not at the next face.
            Relisted();
            Draw();
        }
        return Task.CompletedTask;
    }

    // Names a group: its samples go to the person chosen, by id, or to a new person of the form's names, first, and the
    // group after, so that a group an interruption leaves behind matches its person at the next start and goes. The
    // groups apart from it are known not to be that person. HassSharp has checked that the person chosen is in the set
    // as drawn; the people list may have lost them since, and AddSamples refuses then.
    // The person is written before the group goes, and not the other way: a stop between the two leaves the group, which
    // matches the person at the next start and goes, rather than a group gone with no person made of it. The lock makes
    // no order better: a stop ends the process whatever it holds.
    void SaveFace(string groupId, string? personId, UiValues? newPerson)
    {
        lock (lists)
        {
            if (Unknown.Find(groupId) == null) throw new UiRefusal(FaceGone);
            var samples = Unknown.Samples(groupId);
            if (samples.Count == 0) throw new UiRefusal(NoPicture);
            var person = personId != null ? People.AddSamples(personId, samples, learned: false)
                : newPerson != null ? People.Add(newPerson["first_name"], newPerson["last_name"], samples)
                : throw new UiRefusal(NoPerson);
            Unknown.Named(groupId, person.Id);
            // Other groups of the same face are now recognised, and go too.
            Rematch(People.People);
        }
    }

    void DismissFace(string groupId)
    {
        lock (lists)
        {
            if (Unknown.Find(groupId) == null) throw new UiRefusal(FaceGone);
            Unknown.Remove(groupId);
        }
    }

    void Rename(string personId, string firstName, string lastName)
    {
        Known(personId);
        People.Rename(personId, firstName, lastName);
    }

    // Merges a person into the one chosen, by id: Merge refuses one no longer in the list.
    void Merge(string personId, string? into)
    {
        lock (lists)
        {
            Known(personId);
            if (into == null) throw new UiRefusal(NoOneToMergeInto);
            People.Merge(personId, into);
            Closed(personId);
            // Here only: another detector's groups see the person gone, and drop them.
            Unknown.Merged(personId, into);
            Rematch(People.People);
        }
    }

    void OpenSamples(string personId)
    {
        Known(personId);
        lock (page) samplesOf = personId;
    }

    void CloseSamples()
    {
        lock (page) samplesOf = null;
    }

    void Delete(string personId)
    {
        lock (lists)
        {
            Known(personId);
            People.Delete(personId);
            Closed(personId);
            Rematch(People.People);
        }
    }

    void RemoveSample(string personId, string sampleId)
    {
        lock (lists)
        {
            var person = Known(personId);
            if (!person.Samples.Any(sample => sample.Id == sampleId)) throw new UiRefusal(SampleGone);
            People.RemoveSample(personId, sampleId);
            Rematch(People.People);
        }
    }

    // The person, as the people list has them now.
    Person Known(string personId) => People.People.FirstOrDefault(person => person.Id == personId) ?? throw new UiRefusal(PersonGone);

    // Recognised with the names of the people list as it is now.
    void Relisted()
    {
        Shown listed;
        lock (lists) listed = visits.Listed(People.People);
        Show(listed);
    }

    // A person merged away or deleted: their samples are not shown any more.
    void Closed(string personId)
    {
        lock (page)
            if (samplesOf == personId) samplesOf = null;
    }

    // What is seen of faces, or of people: whether one is, how many, and when one last was. Each entity is set only when
    // its value changes.
    sealed class Watch(SharpBinarySensor seen, SharpSensor count, SharpSensor last)
    {
        // The time of the last check that found one; null before any.
        DateTimeOffset? found;
        // When last seen was last written while seen stays on.
        DateTimeOffset written;

        // A check that worked: the count, then whether one is seen.
        public void Checked(int counted, DateTimeOffset now, TimeSpan hold)
        {
            // The value first: set while unavailable, it is sent with the availability that follows.
            if (count.Value is not int shown || shown != counted) count.Value = counted;
            if (!count.Available) count.Available = true;
            if (counted > 0) found = now;
            Seen(counted > 0 || Held(now, hold), now);
        }

        // A check that failed: the count is unavailable, and a seen that is on goes off once its hold has passed. One
        // unknown stays unknown: nothing was seen of it.
        public void Failed(DateTimeOffset now, TimeSpan hold)
        {
            if (count.Available) count.Available = false;
            if (seen.IsOn == true) Seen(Held(now, hold), now);
        }

        // Not looked for at all: the three are unavailable.
        public void Off()
        {
            seen.Available = false;
            count.Available = false;
            last.Available = false;
        }

        bool Held(DateTimeOffset now, TimeSpan hold) => found is { } at && now - at < hold;

        void Seen(bool on, DateTimeOffset now)
        {
            if (on)
            {
                if (seen.IsOn != true)
                {
                    seen.IsOn = true;
                    Last(now);
                }
                else if (now - written >= LastSeenEvery) Last(now);
            }
            else if (seen.IsOn != false)
            {
                seen.IsOn = false;
                // The last detection's time, so last seen ends on it.
                if (found is { } at && !Equals(last.Value, at)) last.Value = at;
            }
        }

        // Last seen is the last detection's time.
        void Last(DateTimeOffset now)
        {
            written = now;
            if (found is { } at && !Equals(last.Value, at)) last.Value = at;
        }
    }
}

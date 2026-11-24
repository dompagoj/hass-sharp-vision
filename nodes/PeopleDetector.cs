// hass-sharp-vision 1.1.0, from https://github.com/dompagoj/hass-sharp-vision
using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>The camera of a <see cref="PeopleDetector"/>: each frame it checks, with the faces and people found drawn on it.</summary>
public class PeopleDetectorCamera : SharpCamera
{
    /// <summary>The camera keyed <c>camera</c>, with no name of its own: Home Assistant shows the device's.</summary>
    public PeopleDetectorCamera() : base("camera") { }
}

/// <summary>
/// A device that watches one of Home Assistant's cameras and reports the faces and people it sees: whether one is seen
/// (held on for <see cref="StayOnFor"/> after the last), how many are in the newest frame checked, and when one was last
/// seen; and a camera that shows each frame checked with what was found drawn on it. With <see cref="RecogniseFaces"/>
/// on, it recognises the clear faces of each frame against the people list every People detector shares
/// (<see cref="PeopleList"/>), groups the ones it does not know (<see cref="FaceGroups"/>), and learns new looks of the
/// people it is sure of; its page on the Device tab lists the unknown faces to name or dismiss, the people to rename,
/// merge and delete, and a person's samples to remove. Drop it in an entity definition as a node of its own and give it
/// a camera; or derive a C# device node's class from it, such as <c>public class FrontDoor : PeopleDetector { }</c>. It
/// downloads the vision library pinned by Vision.cs as the definition starts, and the face recognition model in the
/// background once it has started. Setup, Start and Stop are its own; a derived class may still add entities in its
/// constructor.
/// </summary>
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
        SampleGone = "This sample is no longer in the list.", NoPicture = "This face has no picture left; dismiss it.";

    readonly PeopleDetectorCamera camera;
    readonly Watch faces, people;
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

    // Guards the page: each draw hands its lists to Ui under it; and samplesOf, drawnGroups and drawnTicket. Drawing runs
    // from the checks, the minute's timer, the handlers and the people list's Changed, which can run at the same time.
    readonly Lock page = new();
    // The person whose samples the page shows; none when it shows none.
    string? samplesOf;
    // The shown groups and their suggestions as last drawn (Key), so that a check draws again only when they change.
    string drawnGroups = "";
    // Each draw's ticket, in the order they began: a draw hands its lists over only when no later one has.
    long ticketsBegun, drawnTicket;

    /// <summary>The device with its camera and its six sensors, in this order: face seen, person seen, faces, people, last face seen, last person seen.</summary>
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
    /// recogniser.
    /// </summary>
    protected sealed override Task Start()
    {
        video = SharpVideo.Open(Camera, new VideoOptions { MaxWidth = 1280 });
        if (!DetectPeople) people.Off();
        if (RecogniseFaces)
        {
            rules = RecognitionRules.Default.WithMatchScore(MatchScore);
            cameraId = Camera.EntityId;
            // The groups first: the people list's callback uses them, and may run as soon as the list is made.
            Unknown = new FaceGroups(Data, cameraId, rules);
            People = new PeopleList(Shared(PeopleList.StoreName), ListsChanged);
            // The list may have changed while this detector was off, and the match score with the settings.
            Unknown.Rematch(People.People, rules);
            Draw();
            Every(RedrawEvery, Refresh, immediately: false);
            BeginOpening(DateTimeOffset.UtcNow);
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
    Task ListsChanged()
    {
        // Another detector wrote while Start() was still making the list, before People was set: Start() matches and
        // draws right after.
        if (People == null) return Task.CompletedTask;
        Unknown.Rematch(People.People, rules);
        Draw();
        return Task.CompletedTask;
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
            var now = DateTimeOffset.UtcNow;
            if (RecogniseFaces) before = People.People;
            var names = RecogniseFaces ? Recognise(frame, foundFaces, now) : null;
            var drawing = Vision.DrawPeople(frame.Copy(), foundPeople);
            using (var drawn = names != null && NamesOnTheCamera ? Vision.DrawFaces(drawing, foundFaces, names) : Vision.DrawFaces(drawing, foundFaces)) camera.Show(drawn);
            faces.Checked(foundFaces.Count, now, hold);
            if (DetectPeople) people.Checked(foundPeople.Count, now, hold);
        }
        catch (Exception) when (!Stopping.IsCancellationRequested)
        {
            // The counts are unknown now; each seen goes off once its hold has passed, so none is left on.
            var now = DateTimeOffset.UtcNow;
            faces.Failed(now, hold);
            if (DetectPeople) people.Failed(now, hold);
            throw;
        }
        // After the entities, so that drawing the page cannot fail them.
        if (before != null) Redraw(before);
    }

    // Recognises the usable faces of a frame, the largest first and at most MostEmbedded, and expires the unknown groups
    // when that is due. Gives each face's name, in the order of found: a recognised face's person's, null for the others.
    // Two faces of one frame are two people, so a person is given to one face of the check, the best match: another face
    // matched to them is not recognised in this check, nor seen, learned from or grouped.
    string?[] Recognise(SharpImage frame, IReadOnlyList<DetectedFace> found, DateTimeOffset now)
    {
        var names = new string?[found.Count];
        if (Recognizer(now) is { } recognizer && found.Count > 0)
        {
            // One snapshot for the check: Learns reads it, which has no sample learned in this check.
            var everyone = People.People;
            var chosen = Enumerable.Range(0, found.Count)
                .Where(index => Usable(found[index]))
                .OrderByDescending(index => Vision.EyeDistance(found[index]))
                .Take(MostEmbedded)
                .ToList();
            // Every face matched first, then each noted, the largest first.
            var matched = new List<Matched>();
            foreach (var index in chosen)
            {
                try
                {
                    if (Match(recognizer, frame, index, found[index], everyone) is { } face) matched.Add(face);
                }
                catch (Exception e) when (!Stopping.IsCancellationRequested) { Failed(e, now); }
            }
            var given = matched.Where(face => face.Match.Kind == MatchKind.Recognised && face.Match.Person != null)
                .GroupBy(face => face.Match.Person!.Id, StringComparer.Ordinal)
                .Select(faces => faces.MaxBy(face => face.Match.Score)!.Index)
                .ToHashSet();
            foreach (var face in matched)
            {
                if (face.Match.Kind == MatchKind.Recognised && !given.Contains(face.Index)) continue;
                try { names[face.Index] = Note(frame, face, now); }
                catch (Exception e) when (!Stopping.IsCancellationRequested) { Failed(e, now); }
            }
        }
        if (now >= expireNext)
        {
            expireNext = now + ExpireEvery;
            Unknown.Expire(now, TimeSpan.FromDays(KeepUnknownFacesFor));
        }
        return names;
    }

    // A face clear enough to embed: large enough, and looking towards the camera.
    bool Usable(DetectedFace face) => Vision.EyeDistance(face) >= rules.UsableEyes && Vision.Frontal(face);

    // A face embedded and matched against the people list; null when it fits too badly to be compared at all.
    Matched? Match(IFaceRecognizer recognizer, SharpImage frame, int index, DetectedFace face, IReadOnlyList<Person> everyone)
    {
        var embedding = recognizer.Embed(frame, face);
        // Too far from where an aligned face has its landmarks to be compared at all.
        if (!(embedding.Fit <= rules.FitLimit)) return null;
        return new Matched(index, face, embedding, Vision.EyeDistance(face), FaceMatcher.Match(embedding, everyone, rules));
    }

    // One matched face, noted as a sighting of its person or of an unknown face, and learned when it meets the rules.
    // Gives its person's name when it is recognised, and null otherwise. Nothing of it is logged.
    string? Note(SharpImage frame, Matched face, DateTimeOffset now)
    {
        var (embedding, eyes, match) = (face.Embedding, face.Eyes, face.Match);
        // Cut only when a face is kept, during the call: the frame is still the check's.
        Func<byte[]> crop = () => Crop(frame, face.Face);
        switch (match)
        {
            case { Kind: MatchKind.Recognised, Person: { } person }:
                People.Seen(person.Id, now, cameraId);
                // A person has one face in a check, so is learned from at most once in it.
                if (LearnNewLooks && FaceMatcher.Learns(embedding, eyes, match, now, rules))
                {
                    // Refused when the person was deleted meanwhile: the sample is dropped.
                    try { People.AddSamples(person.Id, [new NewSample(embedding, crop(), now, cameraId)], learned: true); }
                    catch (PeopleRefusal) { }
                }
                return person.Name;
            case { Kind: MatchKind.Maybe, Person: { } suggested }:
                Unknown.Seen(embedding, crop, eyes, now, suggested.Id);
                return null;
            default:
                Unknown.Seen(embedding, crop, eyes, now, null);
                return null;
        }
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

    // ---- The page ----

    // Draws the page: the unknown faces, the people, and the samples of the person chosen, if any. The cards are built
    // from snapshots and the stores outside the lock, then handed to Ui under it; a draw that a later one has overtaken
    // hands nothing over, so the page never goes back to an older state. A person whose samples are open and who is gone
    // closes them. A draw as the definition stops, when the stores and the page are closed, draws nothing.
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
        var unknown = UnknownCards(shown, everyone, now);
        var people = PersonCards(everyone, now, places);
        var chosen = open == null ? null : everyone.FirstOrDefault(person => person.Id == open);
        var samples = chosen == null ? null : SampleCards(chosen, now, places);
        var unknownEmpty = Unknown.OfAnotherFormat ? "The unknown faces were saved by a newer hass-sharp-vision; update this one." : "No unknown faces.";
        var peopleEmpty = People.Problem ?? "Nobody is named yet.";
        lock (page)
        {
            if (ticket < drawnTicket) return;
            drawnTicket = ticket;
            drawnGroups = Key(shown);
            Ui.List("unknown", "Unknown faces", unknown, attention: true, empty: unknownEmpty);
            Ui.List("people", "People", people, empty: peopleEmpty);
            if (chosen != null) Ui.List("samples", $"Samples of {chosen.Name}", samples!);
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

    // A card for each shown group, the newest sighting first: its largest face, how often and when it was seen, the person
    // it might be, a Name to give it (the suggestion's filled in), Save and Dismiss.
    List<UiCard> UnknownCards(IReadOnlyList<FaceGroup> shown, IReadOnlyList<Person> everyone, DateTimeOffset now)
    {
        var names = everyone.Select(person => person.Name).ToArray();
        var cards = new List<UiCard>();
        foreach (var group in shown.Take(FaceGroups.MostGroups))
        {
            var id = group.Id;
            var suggested = group.SuggestedPersonId is { } personId ? everyone.FirstOrDefault(person => person.Id == personId) : null;
            cards.Add(Card(id, Unknown.Crop(id))
                .Text($"Seen {Count(group.Sightings, "time")} · last {Ago(group.Last, now)}", suggested == null ? null : $"Might be {suggested.Name}")
                .Field("name", "Name", suggested?.Name, required: true, suggestions: names)
                .Action("Save", values => Pressed(() => SaveFace(id, values["name"])), style: UiActionStyle.Primary)
                .Action("Dismiss", _ => Pressed(() => DismissFace(id)), validate: false));
        }
        return cards;
    }

    // A card for each person, by name: their newest named face, their samples and where they were last seen, a Name to
    // rename them with and Save; with someone else in the list, Merge into and Merge; Samples, and Delete.
    List<UiCard> PersonCards(IReadOnlyList<Person> everyone, DateTimeOffset now, Dictionary<string, string> places)
    {
        var cards = new List<UiCard>();
        foreach (var person in everyone.Take(PeopleList.MostPeople))
        {
            var id = person.Id;
            var learned = person.Samples.Count(sample => sample.Learned);
            var samples = Count(person.Samples.Count, "sample") + (learned > 0 ? $" ({learned} learned)" : "");
            var seen = person.LastSeen is { } last ? $"last seen {Ago(last, now)}{At(person.LastSeenAt, places)}" : "not seen yet";
            var face = person.Samples.Where(sample => !sample.Learned).OrderByDescending(sample => sample.At)
                .Select(sample => People.Crop(sample.Id)).FirstOrDefault(crop => crop != null);
            var others = everyone.Where(other => other.Id != id).Select(other => other.Name).ToArray();
            var card = Card(id, face)
                .Text(person.Name, $"{samples} · {seen}")
                .Field("name", "Name", person.Name, required: true);
            // A select needs an option, so Merge into and Merge are there only when there is someone to merge into.
            if (others.Length > 0) card.Select("into", "Merge into", others, others[0]);
            card.Action("Save", values => Pressed(() => Rename(id, values["name"])), style: UiActionStyle.Primary);
            // A question is fixed when the card is drawn: it names the one other person, or the choice.
            if (others.Length > 0)
                card.Action("Merge", values => Pressed(() => Merge(id, values["into"])),
                    confirm: others.Length == 1 ? $"Merge {person.Name} into {others[0]}?" : $"Merge {person.Name} into the person chosen in Merge into?");
            cards.Add(card
                .Action("Samples", _ => Pressed(() => OpenSamples(id)), validate: false)
                .Action("Delete", _ => Pressed(() => Delete(id)), confirm: $"Delete {person.Name} and their samples?", style: UiActionStyle.Danger, validate: false));
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
        if (cards.Count == 0) cards.Add(new UiCard("none").Text($"{person.Name} has no samples left.").Action("Close", _ => Pressed(CloseSamples), validate: false));
        return cards;
    }

    // A card with its picture, or without one when the crop is gone or is not a JPEG the page takes.
    static UiCard Card(string id, byte[]? jpeg) =>
        jpeg is { Length: >= 2 and <= PeopleList.LargestCrop } && jpeg[0] == 0xFF && jpeg[1] == 0xD8 ? new UiCard(id).Image(jpeg) : new UiCard(id);

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

    // Runs a press's change and draws the page after it, also when it is refused. A PeopleRefusal's sentence, which may
    // hold a name, is the card's and is not logged; nothing else here throws with a name.
    Task Pressed(Action change)
    {
        try { change(); }
        catch (PeopleRefusal refusal) { throw new UiRefusal(refusal.Message); }
        finally { Draw(); }
        return Task.CompletedTask;
    }

    // Names a group: its samples go to the person of that name, or to a new person, first, and the group after, so that a
    // group an interruption leaves behind matches its person at the next start and goes.
    void SaveFace(string groupId, string name)
    {
        if (Unknown.Find(groupId) == null) throw new UiRefusal(FaceGone);
        var samples = Unknown.Samples(groupId);
        if (samples.Count == 0) throw new UiRefusal(NoPicture);
        if (People.Named(name) is { } person) People.AddSamples(person.Id, samples, learned: false);
        else People.Add(name, samples);
        Unknown.Remove(groupId);
        // Other groups of the same face are now recognised, and go too.
        Unknown.Rematch(People.People, rules);
    }

    void DismissFace(string groupId)
    {
        if (Unknown.Find(groupId) == null) throw new UiRefusal(FaceGone);
        Unknown.Remove(groupId);
    }

    void Rename(string personId, string name)
    {
        Known(personId);
        People.Rename(personId, name);
    }

    void Merge(string personId, string into)
    {
        Known(personId);
        var target = People.Named(into) ?? throw new UiRefusal(PersonGone);
        People.Merge(personId, target.Id);
        Closed(personId);
        Unknown.Rematch(People.People, rules);
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
        Known(personId);
        People.Delete(personId);
        Closed(personId);
        Unknown.Rematch(People.People, rules);
    }

    void RemoveSample(string personId, string sampleId)
    {
        var person = Known(personId);
        if (!person.Samples.Any(sample => sample.Id == sampleId)) throw new UiRefusal(SampleGone);
        People.RemoveSample(personId, sampleId);
        Unknown.Rematch(People.People, rules);
    }

    // The person, as the people list has them now.
    Person Known(string personId) => People.People.FirstOrDefault(person => person.Id == personId) ?? throw new UiRefusal(PersonGone);

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

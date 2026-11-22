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
/// seen; and a camera that shows each frame checked with what was found drawn on it. Derive a C# device node's class from
/// it, such as <c>public class FrontDoor : PeopleDetector { }</c>, and give it a camera. It downloads the vision library
/// pinned by Vision.cs as the definition starts. Setup, Start and Stop are its own; a derived class may still add
/// entities in its constructor.
/// </summary>
public abstract class PeopleDetector : SharpDevice
{
    // The side each model is given the frame at: faces at 640 find smaller faces than at 320; people at 416.
    const int FaceInput = 640, PersonInput = 416;
    // The longest a check waits for a frame: a camera that gives none fails the check.
    static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);
    // While one stays seen, last seen is written at most this often.
    static readonly TimeSpan LastSeenEvery = TimeSpan.FromSeconds(60);

    readonly PeopleDetectorCamera camera;
    readonly Watch faces, people;
    VisionDetector? detector;
    SharpVideo? video;

    /// <summary>The device with its camera and its six sensors, in this order: face seen, person seen, faces, people, last face seen, last person seen.</summary>
    protected PeopleDetector()
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

    /// <summary>Downloads the vision library for this platform, once, and opens its detector.</summary>
    protected sealed override async Task Setup()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        detector = await Vision.OpenAsync(Files, rid, Stopping);
        Log($"People detector ready ({rid})");
    }

    /// <summary>Opens the camera's live feed and checks its newest frame every <see cref="CheckEvery"/> seconds.</summary>
    protected sealed override Task Start()
    {
        video = SharpVideo.Open(Camera, new VideoOptions { MaxWidth = 1280 });
        Every(TimeSpan.FromSeconds(CheckEvery), Check);
        return Task.CompletedTask;
    }

    /// <summary>Closes the camera's feed and the detector. It runs also when <see cref="Start"/> did not.</summary>
    protected sealed override async Task Stop()
    {
        if (video != null) await video.DisposeAsync();
        detector?.Dispose();
        Log("People detector stopped", PyLogLevel.Debug);
    }

    // One check: the next frame, its faces and people, the camera, and the entities. The timer runs one at a time, and
    // Stop() runs after the last, so nothing here is shared with another thread.
    async Task Check()
    {
        var hold = TimeSpan.FromSeconds(StayOnFor);
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
            var foundPeople = detector.People(frame, PersonInput, PersonScore);
            using (var drawn = Vision.DrawFaces(Vision.DrawPeople(frame.Copy(), foundPeople), foundFaces)) camera.Show(drawn);
            var now = DateTimeOffset.UtcNow;
            faces.Checked(foundFaces.Count, now, hold);
            people.Checked(foundPeople.Count, now, hold);
        }
        catch (Exception) when (!Stopping.IsCancellationRequested)
        {
            // The counts are unknown now; each seen goes off once its hold has passed, so none is left on.
            var now = DateTimeOffset.UtcNow;
            faces.Failed(now, hold);
            people.Failed(now, hold);
            throw;
        }
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

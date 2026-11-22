using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>A point in a picture, in its pixels from the top left.</summary>
/// <param name="X">Pixels from the left.</param>
/// <param name="Y">Pixels from the top.</param>
public readonly record struct VisionPoint(float X, float Y);

/// <summary>A face found in a picture, in the picture's pixels: its box, its score and five landmarks.</summary>
/// <param name="X">The box's left edge.</param>
/// <param name="Y">The box's top edge.</param>
/// <param name="Width">The box's width.</param>
/// <param name="Height">The box's height.</param>
/// <param name="Score">How sure the model is, 0–1.</param>
/// <param name="RightEye">The face's right eye, on the left of the picture for a face looking at the camera.</param>
/// <param name="LeftEye">The face's left eye.</param>
/// <param name="NoseTip">The tip of the nose.</param>
/// <param name="RightMouthCorner">The right corner of the mouth.</param>
/// <param name="LeftMouthCorner">The left corner of the mouth.</param>
public sealed record DetectedFace(float X, float Y, float Width, float Height, float Score,
    VisionPoint RightEye, VisionPoint LeftEye, VisionPoint NoseTip, VisionPoint RightMouthCorner, VisionPoint LeftMouthCorner);

/// <summary>A person found in a picture, in the picture's pixels: their box and its score.</summary>
/// <param name="X">The box's left edge.</param>
/// <param name="Y">The box's top edge.</param>
/// <param name="Width">The box's width.</param>
/// <param name="Height">The box's height.</param>
/// <param name="Score">How sure the model is, 0–1.</param>
public sealed record DetectedPerson(float X, float Y, float Width, float Height, float Score);

/// <summary>
/// A face and person detector of the native hs_vision library, made by <see cref="Vision.OpenAsync"/>. Its calls run one
/// at a time; <see cref="Dispose"/> waits for the one running and closes it, and any call after that throws.
/// </summary>
public sealed unsafe class VisionDetector : IDisposable
{
    // At most this many faces or people a call: a crowd of more counts as this many.
    const int MaxResults = 64;
    // A Face is 15 floats (x, y, w, h, score, then the five landmarks as x, y pairs) and a Person 5, with no padding.
    const int FaceFloats = 15;
    const int PersonFloats = 5;

    // Around every native call and the close: the library leaves a call after close, a double close and a close
    // during a call undefined.
    readonly Lock gate = new();
    readonly Vision.Exports exports;
    // The native detector; zero once closed.
    nint detector;

    internal VisionDetector(Vision.Exports exports, nint detector)
    {
        this.exports = exports;
        this.detector = detector;
    }

    /// <summary>The faces in a picture, the best first, at most 64.</summary>
    /// <param name="image">The picture.</param>
    /// <param name="inputSize">The side the picture is scaled to for the model, a multiple of 32 from 64 to 1280.</param>
    /// <param name="minScore">The lowest score a face is kept with, 0–1.</param>
    /// <exception cref="ArgumentException">The input size or the minimum score is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The detector has been closed.</exception>
    /// <exception cref="InvalidOperationException">The library failed.</exception>
    public IReadOnlyList<DetectedFace> Faces(SharpImage image, int inputSize = 320, double minScore = 0.7)
    {
        Vision.CheckArguments(inputSize, minScore);
        ArgumentNullException.ThrowIfNull(image);
        var found = stackalloc float[MaxResults * FaceFloats];
        var count = Detect(image, inputSize, minScore, found, faces: true);
        var faces = new DetectedFace[count];
        for (var i = 0; i < count; i++)
        {
            var f = found + i * FaceFloats;
            faces[i] = new DetectedFace(f[0], f[1], f[2], f[3], f[4],
                new VisionPoint(f[5], f[6]), new VisionPoint(f[7], f[8]), new VisionPoint(f[9], f[10]), new VisionPoint(f[11], f[12]), new VisionPoint(f[13], f[14]));
        }
        return faces;
    }

    /// <summary>The people in a picture, the best first, at most 64.</summary>
    /// <param name="image">The picture.</param>
    /// <param name="inputSize">The side the picture is scaled to for the model, a multiple of 32 from 64 to 1280.</param>
    /// <param name="minScore">The lowest score a person is kept with, 0–1.</param>
    /// <exception cref="ArgumentException">The input size or the minimum score is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The detector has been closed.</exception>
    /// <exception cref="InvalidOperationException">The library failed.</exception>
    public IReadOnlyList<DetectedPerson> People(SharpImage image, int inputSize = 416, double minScore = 0.45)
    {
        Vision.CheckArguments(inputSize, minScore);
        ArgumentNullException.ThrowIfNull(image);
        var found = stackalloc float[MaxResults * PersonFloats];
        var count = Detect(image, inputSize, minScore, found, faces: false);
        var people = new DetectedPerson[count];
        for (var i = 0; i < count; i++)
        {
            var p = found + i * PersonFloats;
            people[i] = new DetectedPerson(p[0], p[1], p[2], p[3], p[4]);
        }
        return people;
    }

    // Runs hs_vision_faces or hs_vision_people on the picture's RGB pixels, writing at most 64 to found.
    int Detect(SharpImage image, int inputSize, double minScore, float* found, bool faces)
    {
        int width = image.Width, height = image.Height;
        var length = image.PixelsLength(SharpPixelFormat.Rgb);
        var pixels = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            image.CopyPixels(pixels, SharpPixelFormat.Rgb);
            fixed (byte* rgb = pixels)
                lock (gate)
                {
                    if (detector == 0) throw new ObjectDisposedException(nameof(VisionDetector), "The vision detector has been closed");
                    var count = faces
                        ? exports.Faces(detector, rgb, width, height, width * 3, inputSize, (float)minScore, found, MaxResults)
                        : exports.People(detector, rgb, width, height, width * 3, inputSize, (float)minScore, found, MaxResults);
                    if (count < 0) throw new InvalidOperationException($"{(faces ? "Face" : "Person")} detection failed: {exports.LastError()}");
                    return Math.Min(count, MaxResults);
                }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    /// <summary>Waits for a detection running, then closes the detector; a second call does nothing.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (detector == 0) return;
            exports.Close(detector);
            detector = 0;
        }
        GC.SuppressFinalize(this);
    }

    // A detector never disposed, such as one a device's Setup() made before its definition failed to start, is closed
    // when its load context is collected. Nothing reaches it by then, so no call can be running.
    ~VisionDetector()
    {
        if (detector != 0) exports.Close(detector);
    }
}

/// <summary>
/// The native hs_vision library: downloaded once from the hass-sharp-vision release this code is pinned to, checked by
/// its SHA-256, and loaded for the process (it is never unloaded). Also draws what it finds on a picture.
/// </summary>
public static class Vision
{
    // Not an unsafe class: OpenAsync awaits, which an unsafe context may not. Its native parts are unsafe each.
    const string Source = "https://github.com/dompagoj/hass-sharp-vision/releases/download/v1.0.0/";

    // The release's native/SHA256SUMS, by runtime identifier.
    static readonly Dictionary<string, string> Sha256 = new(StringComparer.Ordinal)
    {
        // pinned:begin
        ["linux-arm64"] = "70c21a44411ce2c5b71128aa605653f38234123e1aecdd8e908f17f3f94e9bdd",
        ["linux-musl-arm64"] = "947135b392e481b42d08fb97de2d8d5544d038b24aa581465cc0c3822e1b6607",
        ["linux-musl-x64"] = "3f57c050c4c8344fdb9ec86a4ff30341e805a8dde4fec5e54b895ba17cea097f",
        ["linux-x64"] = "16242e7b754de087fbb796851e661ed5c5bfa5080b7f5ac79ea7ca96b27c8ff7",
        // pinned:end
    };

    const int AbiVersion = 1;
    // SharpImage's limit: an image carries at most 500 drawings.
    const int MaxDrawings = 500;

    // The library's exports, once loaded in this load context. The OS keeps one copy of the library per process.
    static readonly Lock loading = new();
    static Exports? loaded;

    /// <summary>The exports of hs_vision's C ABI version 1 besides hs_vision_abi_version, which <see cref="Load"/> checks first.</summary>
    internal sealed unsafe class Exports(nint library)
    {
        readonly delegate* unmanaged<nint> open = (delegate* unmanaged<nint>)NativeLibrary.GetExport(library, "hs_vision_open");
        readonly delegate* unmanaged<byte*> lastError = (delegate* unmanaged<byte*>)NativeLibrary.GetExport(library, "hs_vision_last_error");
        // (detector, rgb, width, height, stride, input size, minimum score, out, max): how many it wrote, or -1.
        internal readonly delegate* unmanaged<nint, byte*, int, int, int, int, float, float*, int, int> Faces = (delegate* unmanaged<nint, byte*, int, int, int, int, float, float*, int, int>)NativeLibrary.GetExport(library, "hs_vision_faces");
        internal readonly delegate* unmanaged<nint, byte*, int, int, int, int, float, float*, int, int> People = (delegate* unmanaged<nint, byte*, int, int, int, int, float, float*, int, int>)NativeLibrary.GetExport(library, "hs_vision_people");
        internal readonly delegate* unmanaged<nint, void> Close = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(library, "hs_vision_close");

        /// <summary>Why this thread's last call failed.</summary>
        internal string LastError()
        {
            var message = lastError();
            return message == null ? "no reason given" : Marshal.PtrToStringUTF8((nint)message) ?? "no reason given";
        }

        internal VisionDetector Open()
        {
            var detector = open();
            if (detector == 0) throw new InvalidOperationException($"The vision library could not start: {LastError()}");
            return new VisionDetector(this, detector);
        }
    }

    /// <summary>
    /// Downloads the library for <paramref name="runtimeIdentifier"/> (once: a file already downloaded is used as it is),
    /// loads it and opens a detector. Call it from a <c>[NodeSetup]</c> or a device's <c>Setup()</c>, and dispose the
    /// detector when the definition stops (<see cref="NodeSetupContext.Own"/>).
    /// </summary>
    /// <param name="files">The definition's files.</param>
    /// <param name="runtimeIdentifier">The runtime identifier, such as <c>linux-musl-arm64</c>.</param>
    /// <param name="cancellation">Ends the download: the definition's stopping.</param>
    /// <exception cref="PlatformNotSupportedException">There is no build for <paramref name="runtimeIdentifier"/>.</exception>
    /// <exception cref="System.IO.InvalidDataException">The download does not match its checksum.</exception>
    /// <exception cref="InvalidOperationException">The library is another version, or cannot start.</exception>
    public static async Task<VisionDetector> OpenAsync(SharpFiles files, string runtimeIdentifier, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(runtimeIdentifier);
        if (!Sha256.TryGetValue(runtimeIdentifier, out var sha256))
            throw new PlatformNotSupportedException($"The vision library has no native build for {runtimeIdentifier}; it runs on {Listed(Sha256.Keys)}");
        var name = $"libhs_vision-{runtimeIdentifier}.so";
        var path = await files.Download(Source + name, sha256, name, cancellation);
        return Load(path).Open();
    }

    static unsafe Exports Load(string path)
    {
        lock (loading)
        {
            if (loaded != null) return loaded;
            var library = NativeLibrary.Load(path);
            var version = ((delegate* unmanaged<uint>)NativeLibrary.GetExport(library, "hs_vision_abi_version"))();
            if (version != AbiVersion) throw new InvalidOperationException($"The vision library is version {version}, not {AbiVersion}: the download does not match this code");
            return loaded = new Exports(library);
        }
    }

    // "a", "a and b", "a, b and c".
    static string Listed(IEnumerable<string> names)
    {
        var sorted = names.Order(StringComparer.Ordinal).ToArray();
        return sorted.Length < 2 ? string.Join("", sorted) : string.Join(", ", sorted[..^1]) + " and " + sorted[^1];
    }

    /// <summary>Refuses a minimum score outside 0–1 and an input size that is not a multiple of 32 from 64 to 1280.</summary>
    internal static void CheckArguments(int inputSize, double minScore)
    {
        if (minScore is not (>= 0 and <= 1)) throw new ArgumentException("Minimum score is from 0 to 1");
        if (inputSize is < 64 or > 1280 || inputSize % 32 != 0) throw new ArgumentException("Input size is a multiple of 32 from 64 to 1280");
    }

    static int Round(float value) => (int)MathF.Round(value);
    static string Score(float score) => score.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Draws each face on <paramref name="image"/>, the best first and as many as fit under its 500 drawings, 7 each: a
    /// green box, a dot on each landmark (right eye blue, left eye red, nose tip yellow, right mouth corner magenta, left
    /// mouth corner orange) and its score above the box.
    /// </summary>
    /// <param name="image">The picture to draw on, such as a copy (<see cref="SharpImage.Copy"/>).</param>
    /// <param name="faces">The faces found in it.</param>
    /// <returns><paramref name="image"/>.</returns>
    public static SharpImage DrawFaces(SharpImage image, IReadOnlyList<DetectedFace> faces)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(faces);
        var room = Math.Max(0, (MaxDrawings - image.Drawings) / 7);
        foreach (var face in faces.OrderByDescending(face => face.Score).Take(room))
        {
            int x = Round(face.X), y = Round(face.Y);
            image.DrawRect(x, y, Round(face.Width), Round(face.Height), SharpColor.Green, Math.Clamp(Round(MathF.Min(face.Width, face.Height) / 50), 2, 50));
            var radius = Math.Clamp(Round(face.Width / 40), 2, 4096);
            foreach (var (point, color) in new[] { (face.RightEye, SharpColor.Blue), (face.LeftEye, SharpColor.Red), (face.NoseTip, SharpColor.Yellow),
                (face.RightMouthCorner, SharpColor.FromRgb(255, 0, 255)), (face.LeftMouthCorner, SharpColor.Orange) })
                image.DrawCircle(Round(point.X), Round(point.Y), radius, color, filled: true);
            image.DrawText(Score(face.Score), x, Math.Max(0, y - 20), SharpColor.White, 16, background: true);
        }
        return image;
    }

    /// <summary>
    /// Draws each person on <paramref name="image"/>, the best first and as many as fit under its 500 drawings, 2 each: a
    /// cyan box and its score above it.
    /// </summary>
    /// <param name="image">The picture to draw on, such as a copy (<see cref="SharpImage.Copy"/>).</param>
    /// <param name="people">The people found in it.</param>
    /// <returns><paramref name="image"/>.</returns>
    public static SharpImage DrawPeople(SharpImage image, IReadOnlyList<DetectedPerson> people)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(people);
        var color = SharpColor.FromRgb(0, 200, 255);
        var room = Math.Max(0, (MaxDrawings - image.Drawings) / 2);
        foreach (var person in people.OrderByDescending(person => person.Score).Take(room))
        {
            int x = Round(person.X), y = Round(person.Y);
            image.DrawRect(x, y, Round(person.Width), Round(person.Height), color, Math.Clamp(Round(MathF.Min(person.Width, person.Height) / 100), 2, 50));
            image.DrawText(Score(person.Score), x, Math.Max(0, y - 20), color, 16, background: true);
        }
        return image;
    }
}

/// <summary>Face detection for entity definitions: Detect faces, set up once as the definition starts.</summary>
public static class FaceDetection
{
    // This definition's detector: each definition compiles the library into a load context of its own.
    static VisionDetector? detector;

    /// <summary>Downloads and opens the detector, which the definition closes when it stops.</summary>
    [NodeSetup]
    public static async Task Setup(NodeSetupContext context)
    {
        var opened = context.Own(await Vision.OpenAsync(context.Files, context.RuntimeIdentifier, context.Stopping));
        Volatile.Write(ref detector, opened);
        context.Log($"Face detection ready ({context.RuntimeIdentifier})");
    }

    /// <summary>
    /// Finds the faces in a picture and draws them on a copy of it, the best first: a green box, a dot on each eye, the
    /// nose tip and the mouth corners, and the score above the box. The picture itself is left as it was.
    /// </summary>
    /// <param name="image">The picture, such as a camera's frame.</param>
    /// <param name="minScore">The lowest score a face is drawn with, from 0 to 1.</param>
    /// <param name="inputSize">The side the picture is scaled to for the model, a multiple of 32 from 64 to 1280: larger finds smaller faces, more slowly.</param>
    /// <returns>A copy of the picture with the faces drawn on it.</returns>
    [Node("Detect faces", Category = "Vision", Pure = true)]
    [return: Port("Image")]
    public static SharpImage DetectFaces([Port("Image")] SharpImage image, [Port("Minimum score")] double minScore = 0.7, [Port("Input size")] int inputSize = 320)
    {
        Vision.CheckArguments(inputSize, minScore);
        var current = Volatile.Read(ref detector) ?? throw new InvalidOperationException("Detect faces is not set up: it works in an entity definition, once the definition has started");
        IReadOnlyList<DetectedFace> faces;
        try { faces = current.Faces(image, inputSize, minScore); }
        catch (ObjectDisposedException closed) when (closed.ObjectName == nameof(VisionDetector))
        {
            throw new InvalidOperationException("Detect faces has stopped with its definition");
        }
        return Vision.DrawFaces(image.Copy(), faces);
    }
}

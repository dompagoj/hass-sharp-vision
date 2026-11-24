// hass-sharp-vision 1.1.0, from https://github.com/dompagoj/hass-sharp-vision
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

/// <summary>
/// The lines a face's embedding is judged by against the people list. <see cref="Default"/> holds the values of
/// <c>docs/calibration.md</c>.
/// </summary>
/// <param name="MatchScore">The lowest score a face is recognised at.</param>
/// <param name="MatchMargin">How far a recognised face's score must be above the second-best person's.</param>
/// <param name="SuggestScore">The lowest score at which a face that is not recognised is still suggested as the best person.</param>
/// <param name="LearnScore">The lowest score a face is learned as a sample of the person at.</param>
/// <param name="LearnMargin">How far a learned face's score must be above the second-best person's.</param>
/// <param name="UsableEyes">The fewest pixels between the eyes of a face that is embedded at all.</param>
/// <param name="LearnEyes">The fewest pixels between the eyes of a face that is learned.</param>
/// <param name="FitLimit">The worst alignment fit (<see cref="FaceEmbedding.Fit"/>) a face is used with.</param>
public sealed record RecognitionRules(double MatchScore, double MatchMargin, double SuggestScore, double LearnScore, double LearnMargin, double UsableEyes, double LearnEyes, double FitLimit)
{
    /// <summary>The rules the People detector ships with: <c>docs/calibration.md</c>'s Defaults.</summary>
    public static RecognitionRules Default { get; } = new(0.50, 0.10, 0.40, 0.60, 0.15, 16, 24, 7);

    /// <summary>
    /// These rules with another match score, and the suggest and learn scores a tenth under and over it, as the People
    /// detector's <i>Match score</i> setting moves all three.
    /// </summary>
    public RecognitionRules WithMatchScore(double matchScore) => this with
    {
        MatchScore = matchScore,
        // Rounded, so that 0.7 gives 0.8 and not 0.7999999999999999.
        SuggestScore = Math.Round(matchScore - 0.1, 10),
        LearnScore = Math.Round(matchScore + 0.1, 10),
    };
}

/// <summary>A named person of the people list.</summary>
/// <param name="Id">The person's id: short, URL-safe and meaningless.</param>
/// <param name="Name">The name, trimmed.</param>
/// <param name="Added">When the person was added; <see cref="DateTimeOffset.MinValue"/> when that is no longer known.</param>
/// <param name="Samples">The person's face samples, named and learned.</param>
/// <param name="LastSeen">When a detector last recognised the person; null when none has.</param>
/// <param name="LastSeenAt">The camera's entity id where the person was last seen.</param>
public sealed record Person(string Id, string Name, DateTimeOffset Added, IReadOnlyList<FaceSample> Samples, DateTimeOffset? LastSeen, string? LastSeenAt);

/// <summary>A face of a person, as its embedding; its crop is read with <see cref="PeopleList.Crop"/>.</summary>
/// <param name="Id">The sample's id.</param>
/// <param name="Embedding">The face's embedding.</param>
/// <param name="At">When the face was seen.</param>
/// <param name="Camera">The entity id of the camera the face was seen at.</param>
/// <param name="Learned">Whether a detector learned the sample; false when the user named it.</param>
public sealed record FaceSample(string Id, FaceEmbedding Embedding, DateTimeOffset At, string Camera, bool Learned);

/// <summary>A face to add to a person: its embedding and its crop.</summary>
/// <param name="Embedding">The face's embedding, of 128 values.</param>
/// <param name="CropJpeg">The face's crop as a JPEG, at most 1 MiB; stored as given.</param>
/// <param name="At">When the face was seen.</param>
/// <param name="Camera">The entity id of the camera the face was seen at.</param>
public sealed record NewSample(FaceEmbedding Embedding, byte[] CropJpeg, DateTimeOffset At, string Camera);

/// <summary>A change to the people list that was refused; its message is a plain sentence for the user, never logged here.</summary>
public sealed class PeopleRefusal(string message) : Exception(message);

/// <summary>How a face compares with the people list.</summary>
public enum MatchKind
{
    /// <summary>No one comes close enough.</summary>
    Unknown,
    /// <summary>The best person is close, but not close enough or not far enough ahead of the next: a suggestion only.</summary>
    Maybe,
    /// <summary>The best person, at or over the match score and the margin ahead of the next.</summary>
    Recognised,
}

/// <summary>A face compared with the people list.</summary>
/// <param name="Kind">Whether the face is recognised, a maybe or unknown.</param>
/// <param name="Person">The best person; null when the face is unknown.</param>
/// <param name="Score">The best person's score, the cosine of their closest sample; 0 when no one has a sample.</param>
/// <param name="Margin">How far the best score is above the second-best person's; the whole score when there is none.</param>
public sealed record FaceMatch(MatchKind Kind, Person? Person, double Score, double Margin);

/// <summary>Compares a face's embedding with every sample of every person, and judges whether a recognised face is learned.</summary>
public static class FaceMatcher
{
    /// <summary>
    /// The cosine at or over which a face adds nothing to a sample: such a face is not learned, and a group keeps the
    /// larger of the two.
    /// </summary>
    public const double Alike = 0.9;

    /// <summary>How long after a person's last learned sample the next one may be learned.</summary>
    public static readonly TimeSpan LearnedEvery = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The person a face is: a person's score is their best sample's cosine; the face is recognised when the best score is
    /// at least <see cref="RecognitionRules.MatchScore"/> and beats the second-best person's by at least
    /// <see cref="RecognitionRules.MatchMargin"/>, a maybe when it is not but the best score is at least
    /// <see cref="RecognitionRules.SuggestScore"/>, and unknown otherwise.
    /// </summary>
    /// <param name="face">The face's embedding.</param>
    /// <param name="people">The people to compare with, such as <see cref="PeopleList.People"/>.</param>
    /// <param name="rules">The lines to judge by.</param>
    public static FaceMatch Match(FaceEmbedding face, IReadOnlyList<Person> people, RecognitionRules rules)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(rules);
        Person? best = null;
        double bestScore = double.NegativeInfinity, second = double.NegativeInfinity;
        foreach (var person in people)
        {
            if (person.Samples.Count == 0) continue;
            var score = person.Samples.Max(sample => (double)sample.Embedding.Similarity(face));
            if (score > bestScore)
            {
                second = bestScore;
                (best, bestScore) = (person, score);
            }
            else if (score > second) second = score;
        }
        if (best == null) return new(MatchKind.Unknown, null, 0, 0);
        var margin = bestScore - (double.IsNegativeInfinity(second) ? 0 : second);
        var kind = bestScore >= rules.MatchScore && margin >= rules.MatchMargin ? MatchKind.Recognised
            : bestScore >= rules.SuggestScore ? MatchKind.Maybe
            : MatchKind.Unknown;
        return new(kind, kind == MatchKind.Unknown ? null : best, bestScore, margin);
    }

    /// <summary>
    /// Whether a recognised face is kept as a learned sample of its person: its score is at least
    /// <see cref="RecognitionRules.LearnScore"/> and its margin at least <see cref="RecognitionRules.LearnMargin"/>; it is
    /// at least <see cref="RecognitionRules.MatchScore"/> from one of the person's named samples, so that a learned sample
    /// never qualifies another and the learned ones cannot drift away from the faces the user named; it has at least
    /// <see cref="RecognitionRules.LearnEyes"/> pixels between the eyes; it is under <see cref="Alike"/> from every sample
    /// the person has; and the person gained no learned sample in the <see cref="LearnedEvery"/> before
    /// <paramref name="now"/>.
    /// </summary>
    /// <param name="face">The face's embedding.</param>
    /// <param name="eyes">The pixels between the face's eyes.</param>
    /// <param name="match">The face's match, from <see cref="Match"/>.</param>
    /// <param name="now">When the face was seen.</param>
    /// <param name="rules">The lines to judge by.</param>
    public static bool Learns(FaceEmbedding face, double eyes, FaceMatch match, DateTimeOffset now, RecognitionRules rules)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(rules);
        if (match.Kind != MatchKind.Recognised || match.Person is not { } person) return false;
        if (match.Score < rules.LearnScore || match.Margin < rules.LearnMargin || eyes < rules.LearnEyes) return false;
        var samples = person.Samples;
        if (!samples.Any(sample => !sample.Learned && sample.Embedding.Similarity(face) >= rules.MatchScore)) return false;
        if (samples.Any(sample => sample.Embedding.Similarity(face) >= Alike)) return false;
        var learned = samples.Where(sample => sample.Learned).Select(sample => (DateTimeOffset?)sample.At).Max();
        return learned is not { } last || now - last >= LearnedEvery;
    }
}

/// <summary>
/// The named people every People detector shares, kept in a store (<see cref="StoreName"/>) and in memory
/// (<see cref="People"/>), which is reloaded when another detector changes the store.
/// </summary>
/// <remarks>
/// <para>
/// The store's keys: <c>format</c>, 1; <c>names</c>, a JSON object from each person's id to their name, which every
/// create, rename, merge and delete changes with one <see cref="SharpData.Update{T}"/>, so that two detectors lose no
/// change and never give two people one name; <c>people/&lt;id&gt;</c>, the person without embeddings and crops;
/// <c>samples/&lt;personId&gt;/&lt;sampleId&gt;/embedding</c>, 128 little-endian floats;
/// <c>samples/&lt;personId&gt;/&lt;sampleId&gt;/crop</c>, the JPEG; and <c>unfinished/&lt;personId&gt;</c> while an add, a
/// delete or a merge's removal of that person runs: when it began, and whether it removes them.
/// </para>
/// <para>
/// A change is written so that an interruption leaves nothing that matters: a sample's embedding and crop before the
/// person lists it, a person before their name, and a name removed before the person and the samples. An add notes its
/// new person in <c>unfinished/</c> before it writes anything of theirs, and a removal before it removes the name; each
/// takes its note out when it is done. A list made on the store, and each delete, finishes what an interruption left:
/// a person a note names who has no name loses their record and samples, unless the note is an add's begun less than
/// ten minutes ago, which may still be writing on another detector; a person who has a name keeps everything. Ids are
/// never reused, so finishing a removal races with nothing. Until then loading ignores what is left: a person or a
/// sample no name or person lists. Samples left under a person who keeps their name (by an interrupted sample addition
/// or merge copy) stay until the person is deleted. A name whose person is gone is listed without samples, so that it
/// can be given samples or deleted; a sample whose embedding is gone or not 512 bytes is left out. A store of another
/// format is neither read nor written.
/// </para>
/// <para>
/// <see cref="People"/> is a snapshot that a reload replaces and never changes, safe to read while another thread
/// reloads or writes. The lock guards only the snapshot and the sightings, and is never held across a store call.
/// </para>
/// </remarks>
public sealed class PeopleList
{
    /// <summary>The shared store the people list is kept in.</summary>
    public const string StoreName = "hass-sharp-vision.people";
    /// <summary>The most named samples a person has.</summary>
    public const int MostNamed = 20;
    /// <summary>The most learned samples a person has; a new one beyond replaces the oldest.</summary>
    public const int MostLearned = 20;
    /// <summary>The most people the list has.</summary>
    public const int MostPeople = 200;
    /// <summary>The longest name, in characters.</summary>
    public const int LongestName = 64;
    /// <summary>The largest crop, in bytes.</summary>
    public const int LargestCrop = 1024 * 1024;

    const int Format = 1;
    internal const int Values = 128;
    const int IdLength = 12;
    const string IdCharacters = "abcdefghijklmnopqrstuvwxyz0123456789";
    static readonly TimeSpan SeenEvery = TimeSpan.FromMinutes(1);
    // How long an add's note in unfinished/ is taken for one still writing.
    static readonly TimeSpan Unfinishing = TimeSpan.FromMinutes(10);

    const string Newer = "The people list was saved by a newer hass-sharp-vision; update this one to change it.";
    const string Unreadable = "The people list cannot be read; delete it in Stored data to start again.";
    const string Gone = "This person is no longer in the list.";
    const string Full = "The list has room for 200 people; delete someone to add another.";
    const string LastNamed = "The last sample a person was named with cannot be removed; delete the person instead.";
    const string NoRoomLeft = "The people list has no room left; remove some samples or people first.";

    readonly SharpData data;
    // Guards people, published and seen; never held across a store call.
    readonly Lock gate = new();
    IReadOnlyList<Person> people = Array.Empty<Person>();
    // Each reload's ticket, in the order they began: a reload publishes only when no later one has.
    long started, published;
    // When this list last wrote each person's last seen.
    readonly Dictionary<string, DateTimeOffset> seen = new(StringComparer.Ordinal);

    /// <summary>The people list of a store, loaded, and reloaded each time another component changes the store.</summary>
    /// <param name="data">The store: <c>Shared(PeopleList.StoreName)</c>.</param>
    /// <param name="changed">Runs after each reload for another component's change, as a <see cref="SharpData.Changed"/> action does.</param>
    public PeopleList(SharpData data, Func<Task>? changed = null)
    {
        this.data = data ?? throw new ArgumentNullException(nameof(data));
        // Subscribed before the first load, so that no change between the two is missed.
        data.Changed(async () =>
        {
            Reload();
            if (changed != null) await changed();
        });
        Finish();
        Reload();
    }

    /// <summary>The people, by name: a snapshot, replaced by each change and reload.</summary>
    public IReadOnlyList<Person> People
    {
        get { lock (gate) return people; }
    }

    /// <summary>Reads the list from the store again; an embedding already read is not read again.</summary>
    public void Reload()
    {
        var ticket = Interlocked.Increment(ref started);
        var loaded = Load(People);
        lock (gate)
        {
            if (ticket < published) return;
            published = ticket;
            people = loaded;
        }
    }

    /// <summary>The person of a name, ignoring case and surrounding spaces; null when no one has it.</summary>
    public Person? Named(string name) => name == null ? null : People.FirstOrDefault(person => Same(person.Name, name));

    /// <summary>The crop of a sample of anyone in the list, as stored; null when there is none.</summary>
    public byte[]? Crop(string sampleId)
    {
        var person = People.FirstOrDefault(p => p.Samples.Any(sample => sample.Id == sampleId));
        if (person == null) return null;
        try { return data.GetBytes(CropKey(person.Id, sampleId)); }
        catch (InvalidDataException) { return null; }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException) { return null; }
    }

    /// <summary>Adds a person, named with the samples given.</summary>
    /// <param name="name">The name; trimmed, 1 to 64 characters, and no one else's.</param>
    /// <param name="samples">1 to 20 faces of the person, which become named samples.</param>
    /// <returns>The person added.</returns>
    /// <exception cref="PeopleRefusal">The name is empty, too long or taken; there are no samples or too many; the list is full.</exception>
    /// <exception cref="ArgumentException">A sample has no 128 values, no crop, or a crop over 1 MiB.</exception>
    public Person Add(string name, IEnumerable<NewSample> samples)
    {
        var trimmed = CheckName(name);
        var given = CheckSamples(samples);
        if (given.Count == 0) throw new PeopleRefusal($"{trimmed} needs a face to be named with.");
        if (given.Count > MostNamed) throw NoRoom(trimmed);
        CheckFormat();
        RefuseTaken(ReadNames(), null, trimmed);

        var id = NewId();
        var listed = given.Select(sample => new StoredSample(NewId(), sample.At, sample.Camera, false, sample.Embedding.Fit)).ToList();
        var record = new StoredPerson(DateTimeOffset.UtcNow, null, null, listed);
        Store(() => data.Set(UnfinishedKey(id), new Unfinished(record.Added, false)));
        try
        {
            WriteSamples(id, listed, given);
            data.Set(RecordKey(id), record);
            ChangeNames(names =>
            {
                RefuseTaken(names, null, trimmed);
                if (names.Count >= MostPeople) throw new PeopleRefusal(Full);
                names[id] = trimmed;
            });
        }
        catch
        {
            Forget(id);
            Finished(id);
            throw;
        }
        Finished(id);
        Reload();
        return Find(id) ?? new Person(id, trimmed, record.Added, given.Select((sample, i) => Sample(listed[i], sample.Embedding.Values)).ToArray(), null, null);
    }

    /// <summary>
    /// Adds samples to a person. Named ones are refused all together when the person would have more than 20; a learned one
    /// beyond 20 replaces the person's oldest learned sample.
    /// </summary>
    /// <returns>The person with the samples.</returns>
    /// <exception cref="PeopleRefusal">The person is no longer in the list, or has no room for the named samples.</exception>
    /// <exception cref="ArgumentException">A sample has no 128 values, no crop, or a crop over 1 MiB.</exception>
    public Person AddSamples(string personId, IEnumerable<NewSample> samples, bool learned)
    {
        var given = CheckSamples(samples);
        CheckFormat();
        if (!IsId(personId) || !ReadNames().TryGetValue(personId, out var name)) throw new PeopleRefusal(Gone);
        if (!learned && given.Count > MostNamed) throw NoRoom(name);
        if (given.Count == 0) return Find(personId) ?? throw new PeopleRefusal(Gone);

        var listed = given.Select(sample => new StoredSample(NewId(), sample.At, sample.Camera, learned, sample.Embedding.Fit)).ToList();
        var replaced = new List<string>();
        try
        {
            WriteSamples(personId, listed, given);
            data.Update<StoredPerson>(RecordKey(personId), current =>
            {
                // A person whose record is gone is listed again.
                current ??= new StoredPerson(DateTimeOffset.UtcNow, null, null, []);
                var samples = (current.Samples ?? []).ToList();
                replaced.Clear();
                if (!learned && samples.Count(sample => !sample.Learned) + listed.Count > MostNamed) throw NoRoom(name);
                samples.AddRange(listed);
                while (samples.Count(sample => sample.Learned) > MostLearned)
                {
                    var oldest = samples.Where(sample => sample.Learned).MinBy(sample => sample.At)!;
                    samples.Remove(oldest);
                    replaced.Add(oldest.Id);
                }
                return current with { Samples = samples };
            });
        }
        catch
        {
            foreach (var sample in listed) RemoveSampleKeys(personId, sample.Id);
            throw;
        }
        foreach (var sampleId in replaced) RemoveSampleKeys(personId, sampleId);
        // Deleted meanwhile, by another detector: what was just written goes too.
        if (!ReadNames().ContainsKey(personId)) Forget(personId);
        Reload();
        return Find(personId) ?? throw new PeopleRefusal(Gone);
    }

    /// <summary>Renames a person.</summary>
    /// <param name="personId">The person's id.</param>
    /// <param name="name">The new name; trimmed, 1 to 64 characters, and no one else's (the person's own in another case is theirs).</param>
    /// <exception cref="PeopleRefusal">The name is empty, too long or someone else's, or the person is no longer in the list.</exception>
    public void Rename(string personId, string name)
    {
        var trimmed = CheckName(name);
        CheckFormat();
        if (!IsId(personId)) throw new PeopleRefusal(Gone);
        ChangeNames(names =>
        {
            if (!names.ContainsKey(personId)) throw new PeopleRefusal(Gone);
            RefuseTaken(names, personId, trimmed);
            names[personId] = trimmed;
        });
        Reload();
    }

    /// <summary>
    /// Merges one person into another: the samples of <paramref name="fromId"/> go to <paramref name="intoId"/>, named
    /// first, each kind as far as its cap allows (the newest first), and <paramref name="fromId"/> is deleted with the
    /// rest. The samples are written before the source is removed, so an interruption leaves both people readable, and a
    /// second merge finishes the job.
    /// </summary>
    /// <exception cref="PeopleRefusal">The two are one person, or <paramref name="intoId"/> is no longer in the list.</exception>
    public void Merge(string fromId, string intoId)
    {
        CheckFormat();
        if (fromId == intoId) throw new PeopleRefusal("A person cannot be merged with themselves.");
        var names = ReadNames();
        if (!IsId(intoId) || !names.ContainsKey(intoId)) throw new PeopleRefusal(Gone);
        // Merged or deleted already.
        if (!IsId(fromId) || !names.ContainsKey(fromId)) return;
        MoveSamples(fromId, intoId);
        // Deleted meanwhile, by another detector: what was just copied to them goes, and the source stays.
        if (!ReadNames().ContainsKey(intoId))
        {
            Forget(intoId);
            Reload();
            throw new PeopleRefusal(Gone);
        }
        RemovePerson(fromId);
        Reload();
    }

    /// <summary>Deletes a person: their name, then their record and every sample. Nothing when the person is gone already.</summary>
    /// <exception cref="PeopleRefusal">The list is of another format, or cannot be read.</exception>
    public void Delete(string personId)
    {
        CheckFormat();
        if (!IsId(personId)) return;
        RemovePerson(personId);
        lock (gate) seen.Remove(personId);
        Finish();
        Reload();
    }

    /// <summary>Removes a sample of a person, with its embedding and crop. Nothing when it is gone already.</summary>
    /// <exception cref="PeopleRefusal">It is the person's last named sample.</exception>
    public void RemoveSample(string personId, string sampleId)
    {
        CheckFormat();
        if (!IsId(personId) || !IsId(sampleId) || ReadRecord(personId) == null) return;
        var removed = false;
        var written = data.Update<StoredPerson>(RecordKey(personId), current =>
        {
            if (current == null) return null!;
            var samples = (current.Samples ?? []).ToList();
            var sample = samples.FirstOrDefault(s => s.Id == sampleId);
            if (sample == null) return current;
            if (!sample.Learned && samples.Count(s => !s.Learned) == 1) throw new PeopleRefusal(LastNamed);
            samples.Remove(sample);
            removed = true;
            return current with { Samples = samples };
        });
        // Deleted meanwhile: the null written in its place goes.
        if (written == null) data.Remove(RecordKey(personId));
        if (removed) RemoveSampleKeys(personId, sampleId);
        Reload();
    }

    /// <summary>
    /// Notes that a person was recognised at a camera. It is written at most once a minute a person, and never moves last
    /// seen back; a person no longer in the list, or a list that cannot be written, is left as it is.
    /// </summary>
    public void Seen(string personId, DateTimeOffset at, string camera)
    {
        if (!IsId(personId) || Find(personId) == null) return;
        lock (gate)
        {
            if (seen.TryGetValue(personId, out var last) && (at - last).Duration() < SeenEvery) return;
            seen[personId] = at;
        }
        try { CheckFormat(); }
        catch (PeopleRefusal) { return; }
        var written = data.Update<StoredPerson>(RecordKey(personId), current =>
        {
            if (current == null) return null!;
            return current.LastSeen is { } before && before >= at ? current : current with { LastSeen = at, LastSeenAt = camera };
        });
        if (written == null) data.Remove(RecordKey(personId));
        Reload();
    }

    // ---- The steps of a merge ----

    /// <summary>
    /// A merge's first step: copies the source's samples the target does not list yet, named first and each kind up to
    /// its cap, and lists them on the target, with the later last seen. The source is left as it was.
    /// </summary>
    internal void MoveSamples(string fromId, string intoId)
    {
        var source = ReadRecord(fromId);
        if (source == null) return;
        var target = ReadRecord(intoId);
        var listed = (target?.Samples ?? []).Select(sample => sample.Id).ToHashSet(StringComparer.Ordinal);
        var waiting = (source.Samples ?? []).Where(sample => IsId(sample.Id) && !listed.Contains(sample.Id)).ToList();
        var namedRoom = MostNamed - (target?.Samples ?? []).Count(sample => !sample.Learned);
        var learnedRoom = MostLearned - (target?.Samples ?? []).Count(sample => sample.Learned);
        var chosen = waiting.Where(sample => !sample.Learned).OrderByDescending(sample => sample.At).Take(Math.Max(0, namedRoom))
            .Concat(waiting.Where(sample => sample.Learned).OrderByDescending(sample => sample.At).Take(Math.Max(0, learnedRoom)))
            .ToList();
        var copied = new List<StoredSample>();
        foreach (var sample in chosen)
        {
            var embedding = TryBytes(EmbeddingKey(fromId, sample.Id));
            if (embedding is not { Length: Values * sizeof(float) }) continue;
            var crop = TryBytes(CropKey(fromId, sample.Id));
            Store(() =>
            {
                data.SetBytes(EmbeddingKey(intoId, sample.Id), embedding);
                if (crop != null) data.SetBytes(CropKey(intoId, sample.Id), crop);
            });
            copied.Add(sample);
        }
        data.Update<StoredPerson>(RecordKey(intoId), current =>
        {
            current ??= source with { Samples = [], LastSeen = null, LastSeenAt = null };
            var samples = (current.Samples ?? []).ToList();
            var ids = samples.Select(sample => sample.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var sample in copied)
            {
                if (ids.Contains(sample.Id)) continue;
                var cap = sample.Learned ? MostLearned : MostNamed;
                if (samples.Count(s => s.Learned == sample.Learned) >= cap) continue;
                samples.Add(sample);
            }
            var later = source.LastSeen is { } seenThere && (current.LastSeen == null || seenThere > current.LastSeen);
            return current with
            {
                Samples = samples,
                LastSeen = later ? source.LastSeen : current.LastSeen,
                LastSeenAt = later ? source.LastSeenAt : current.LastSeenAt,
            };
        });
    }

    /// <summary>
    /// Removes a person: notes the removal in <c>unfinished/</c>, removes their name, then their record and every key of
    /// their samples, and takes the note out.
    /// </summary>
    void RemovePerson(string personId)
    {
        // A store without room even for the note is still emptied, the name first, as before there were notes.
        try { data.Set(UnfinishedKey(personId), new Unfinished(DateTimeOffset.UtcNow, true)); }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException) { }
        // A name gone already is not written again, which would only make every other detector reload.
        if (ReadNames().ContainsKey(personId)) ChangeNames(names => names.Remove(personId));
        Forget(personId);
        Finished(personId);
    }

    /// <summary>
    /// Finishes what an interrupted add, delete or merge left, as each note in <c>unfinished/</c> says: a person without a
    /// name loses their record and samples, unless the note is an add's begun less than ten minutes ago; the note goes
    /// once its person has a name or nothing left, or, for a removal of someone who kept their name, once it is old.
    /// </summary>
    void Finish()
    {
        if (ReadFormat() != Format) return;
        var notes = data.Keys("unfinished/");
        if (notes.Count == 0) return;
        // Read after the notes, so that an add's note it finds was written before the names it reads.
        Dictionary<string, string> names;
        try { names = ReadNames(); }
        catch (PeopleRefusal) { return; }
        var now = DateTimeOffset.UtcNow;
        foreach (var key in notes)
        {
            var id = key["unfinished/".Length..];
            if (!IsId(id)) continue;
            Unfinished? note;
            try { note = data.Get<Unfinished>(key); }
            catch (InvalidDataException) { note = null; }
            catch (InvalidOperationException e) when (e is not ObjectDisposedException) { note = null; }
            // A note that cannot be read is taken for an old add's.
            var removing = note?.Removing ?? false;
            var old = note == null || now - note.At >= Unfinishing;
            if (names.ContainsKey(id))
            {
                // A removal begun just now may be about to remove the name.
                if (!removing || old) Finished(id);
            }
            else if (removing || old)
            {
                Forget(id);
                Finished(id);
            }
        }
    }

    /// <summary>Takes a person's note out of <c>unfinished/</c>, as far as it can.</summary>
    void Finished(string personId)
    {
        try { data.Remove(UnfinishedKey(personId)); }
        catch (Exception e) when (e is not ObjectDisposedException) { }
    }

    // ---- Loading ----

    /// <summary>The list as the store has it; empty when the store is of another format or its names cannot be read.</summary>
    /// <param name="before">The list loaded last, whose embeddings are not read again: a sample's embedding never changes.</param>
    IReadOnlyList<Person> Load(IReadOnlyList<Person> before)
    {
        if (ReadFormat() is not (null or Format)) return Array.Empty<Person>();
        Dictionary<string, string> names;
        try { names = ReadNames(); }
        catch (PeopleRefusal) { return Array.Empty<Person>(); }
        var known = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var person in before)
        foreach (var sample in person.Samples)
            known[EmbeddingKey(person.Id, sample.Id)] = sample.Embedding.Values;

        var loaded = new List<Person>();
        foreach (var (id, name) in names)
        {
            if (!IsId(id) || string.IsNullOrWhiteSpace(name)) continue;
            var record = ReadRecord(id);
            var samples = new List<FaceSample>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sample in record?.Samples ?? [])
            {
                if (sample == null || !IsId(sample.Id) || !ids.Add(sample.Id)) continue;
                var key = EmbeddingKey(id, sample.Id);
                var values = known.TryGetValue(key, out var cached) ? cached : ReadEmbedding(key);
                if (values != null) samples.Add(Sample(sample, values));
            }
            loaded.Add(new Person(id, name, record?.Added ?? DateTimeOffset.MinValue, samples.AsReadOnly(), record?.LastSeen, record?.LastSeenAt));
        }
        loaded.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) is var order and not 0 ? order : string.CompareOrdinal(a.Id, b.Id));
        return loaded.AsReadOnly();
    }

    static FaceSample Sample(StoredSample sample, float[] values) =>
        new(sample.Id, new FaceEmbedding(values, sample.Fit), sample.At, sample.Camera ?? "", sample.Learned);

    /// <summary>The store's format; null when none is written yet, and -1 when it cannot be read.</summary>
    int? ReadFormat()
    {
        try { return data.Get<int?>("format"); }
        catch (InvalidDataException) { return -1; }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException) { return -1; }
    }

    /// <summary>Refuses a change to a store of another format, and writes the format into a store that has none yet.</summary>
    void CheckFormat()
    {
        var format = ReadFormat();
        if (format == null)
        {
            try { format = data.Update<int?>("format", current => current ?? Format); }
            catch (InvalidDataException) { format = -1; }
        }
        if (format == -1) throw new PeopleRefusal(Unreadable);
        if (format != Format) throw new PeopleRefusal(Newer);
    }

    /// <summary>The names, by id; empty when none is written yet.</summary>
    /// <exception cref="PeopleRefusal">They cannot be read.</exception>
    Dictionary<string, string> ReadNames()
    {
        try { return data.Get<Dictionary<string, string>>("names") ?? new(StringComparer.Ordinal); }
        catch (InvalidDataException) { throw new PeopleRefusal(Unreadable); }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException) { throw new PeopleRefusal(Unreadable); }
    }

    /// <summary>Changes the names in one step, so that no other detector's change is lost.</summary>
    /// <exception cref="PeopleRefusal">They cannot be read, or <paramref name="change"/> refused.</exception>
    void ChangeNames(Action<Dictionary<string, string>> change)
    {
        try
        {
            data.Update<Dictionary<string, string>>("names", names =>
            {
                names ??= new(StringComparer.Ordinal);
                change(names);
                return names;
            });
        }
        catch (InvalidDataException) { throw new PeopleRefusal(Unreadable); }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException) { throw new PeopleRefusal(Unreadable); }
    }

    /// <summary>A person's record; null when there is none, or it cannot be read.</summary>
    StoredPerson? ReadRecord(string personId)
    {
        try { return data.Get<StoredPerson>(RecordKey(personId)); }
        catch (InvalidDataException) { return null; }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException) { return null; }
    }

    /// <summary>An embedding's 128 values; null when it is gone, damaged or not 512 bytes.</summary>
    float[]? ReadEmbedding(string key) => EmbeddingValues(TryBytes(key));

    /// <summary>The 128 values of an embedding's stored bytes; null when there are none or they are not 512.</summary>
    internal static float[]? EmbeddingValues(byte[]? bytes)
    {
        if (bytes is not { Length: Values * sizeof(float) }) return null;
        var values = new float[Values];
        for (var i = 0; i < Values; i++) values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        return values;
    }

    /// <summary>An embedding's 128 values as they are stored: little-endian floats.</summary>
    internal static byte[] EmbeddingBytes(float[] values)
    {
        var bytes = new byte[Values * sizeof(float)];
        for (var v = 0; v < Values; v++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(v * sizeof(float)), values[v]);
        return bytes;
    }

    byte[]? TryBytes(string key)
    {
        try { return data.GetBytes(key); }
        catch (InvalidDataException) { return null; }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException) { return null; }
    }

    Person? Find(string personId) => People.FirstOrDefault(person => person.Id == personId);

    // ---- Writing ----

    /// <summary>Writes each sample's embedding and crop.</summary>
    void WriteSamples(string personId, IReadOnlyList<StoredSample> listed, IReadOnlyList<NewSample> given)
    {
        for (var i = 0; i < listed.Count; i++)
        {
            var (sample, face) = (listed[i], given[i]);
            var bytes = EmbeddingBytes(face.Embedding.Values);
            Store(() =>
            {
                data.SetBytes(EmbeddingKey(personId, sample.Id), bytes);
                data.SetBytes(CropKey(personId, sample.Id), face.CropJpeg);
            });
        }
    }

    /// <summary>A write, refused in a plain sentence when the store has no room for it.</summary>
    static void Store(Action write)
    {
        try { write(); }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException) { throw new PeopleRefusal(NoRoomLeft); }
    }

    /// <summary>Removes a person's record and every key of their samples, as far as it can.</summary>
    void Forget(string personId)
    {
        try
        {
            data.Remove(RecordKey(personId));
            foreach (var key in data.Keys($"samples/{personId}/")) data.Remove(key);
        }
        catch (Exception e) when (e is not ObjectDisposedException) { }
    }

    void RemoveSampleKeys(string personId, string sampleId)
    {
        try
        {
            data.Remove(EmbeddingKey(personId, sampleId));
            data.Remove(CropKey(personId, sampleId));
        }
        catch (Exception e) when (e is not ObjectDisposedException) { }
    }

    // ---- Names, samples and ids ----

    /// <exception cref="PeopleRefusal">The name is empty or longer than 64 characters.</exception>
    static string CheckName(string name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) throw new PeopleRefusal("A name cannot be empty.");
        if (trimmed.Length > LongestName) throw new PeopleRefusal($"A name is at most {LongestName} characters.");
        return trimmed;
    }

    static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <exception cref="PeopleRefusal">Someone other than <paramref name="except"/> has the name.</exception>
    static void RefuseTaken(Dictionary<string, string> names, string? except, string name)
    {
        if (names.Any(entry => entry.Key != except && entry.Value != null && Same(entry.Value, name)))
            throw new PeopleRefusal($"{name} is already someone else's name.");
    }

    static PeopleRefusal NoRoom(string name) => new($"{name} has no room for more samples.");

    /// <exception cref="ArgumentException">A sample has no 128 values, no crop, or a crop over 1 MiB.</exception>
    static List<NewSample> CheckSamples(IEnumerable<NewSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var list = samples.ToList();
        foreach (var sample in list)
        {
            if (sample == null) throw new ArgumentException("A sample cannot be null");
            if (sample.Embedding.Values is not { Length: Values }) throw new ArgumentException($"A face's embedding has {Values} values");
            if (sample.CropJpeg == null) throw new ArgumentException("A sample needs its face's crop");
            if (sample.CropJpeg.Length > LargestCrop) throw new ArgumentException("A face's crop takes at most 1 MiB");
            if (sample.Camera == null) throw new ArgumentException("A sample needs its camera");
        }
        return list;
    }

    /// <summary>A new id: 12 lower-case letters and digits, from a cryptographic random source.</summary>
    internal static string NewId() => RandomNumberGenerator.GetString(IdCharacters, IdLength);

    /// <summary>Whether a string can be an id here, and so a part of a key.</summary>
    internal static bool IsId(string? id) => id is { Length: > 0 and <= 32 } && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    static string RecordKey(string personId) => $"people/{personId}";
    static string EmbeddingKey(string personId, string sampleId) => $"samples/{personId}/{sampleId}/embedding";
    static string CropKey(string personId, string sampleId) => $"samples/{personId}/{sampleId}/crop";
    static string UnfinishedKey(string personId) => $"unfinished/{personId}";

    /// <summary>A person as <c>people/&lt;id&gt;</c> keeps them: without their name, embeddings and crops.</summary>
    internal sealed record StoredPerson(DateTimeOffset Added, DateTimeOffset? LastSeen, string? LastSeenAt, List<StoredSample>? Samples);

    /// <summary>A sample as its person's record lists it: its embedding and crop are keys of their own.</summary>
    internal sealed record StoredSample(string Id, DateTimeOffset At, string Camera, bool Learned, float Fit);

    /// <summary>An add, a delete or a merge's removal of a person, as <c>unfinished/&lt;id&gt;</c> notes it while it runs.</summary>
    internal sealed record Unfinished(DateTimeOffset At, bool Removing);
}

/// <summary>A group of faces of someone the people list does not have, as one detector saw them.</summary>
/// <param name="Id">The group's id: short, URL-safe and meaningless.</param>
/// <param name="Sightings">How many checks saw the group; the faces of one check count once.</param>
/// <param name="First">When the group was first seen.</param>
/// <param name="Last">When the group was last seen.</param>
/// <param name="SuggestedPersonId">The id of the person the group might be; null when there is none.</param>
public sealed record FaceGroup(string Id, int Sightings, DateTimeOffset First, DateTimeOffset Last, string? SuggestedPersonId);

/// <summary>
/// The faces one People detector sees that the people list does not know, grouped by likeness so that the user can name
/// them, and kept in the detector's own store. A face joins every group it is at least
/// <see cref="RecognitionRules.MatchScore"/> from a sample of, and the groups it joins become one, the oldest; a face
/// close to none starts a group. A group is shown (<see cref="Shown"/>) from its <see cref="ShownAt"/>th sighting.
/// </summary>
/// <remarks>
/// <para>
/// The store's keys: <c>format</c>, 1; <c>groups</c>, a JSON array of the groups' ids; <c>groups/&lt;id&gt;</c>, a group
/// without its embeddings and crops; <c>groups/&lt;id&gt;/&lt;sampleId&gt;/embedding</c>, 128 little-endian floats; and
/// <c>groups/&lt;id&gt;/&lt;sampleId&gt;/crop</c>, the JPEG.
/// </para>
/// <para>
/// Memory is the truth while the detector runs, and the store follows: a group is written when it starts, gains or loses
/// a sample, is joined with another, is first shown or has its suggestion changed by <see cref="Rematch"/>, and its last
/// seen and count otherwise at most once a minute (<see cref="WrittenEvery"/>). A restart can lose up to a minute of a
/// group's sightings.
/// </para>
/// <para>
/// A new group's samples are written before its record, and its record before the index lists it; a group removed leaves
/// the index first, then its record and samples go. Only this detector writes its store, and a revision of it loads the
/// store only after the one before has stopped, so loading removes every key under <c>groups/</c> that the index and the
/// groups' records do not list: what an interruption left, which nothing is still writing. A group whose record cannot
/// be read, or has no sample left, is dropped with its keys; an index that cannot be read drops every group, as this is
/// a cache of faces the user has not named. A store of another format is neither read nor written, and has no groups.
/// A store cleared while the detector runs is noticed at the next change, and the groups in memory are forgotten, so
/// that none is written back.
/// </para>
/// <para>
/// <see cref="Shown"/> is a snapshot that each change replaces and never changes, read without waiting. One lock guards
/// the groups, and each change holds it across its writes to the store, so that a check's sighting and a page's removal
/// cannot interleave their writes and leave a removed group's sample behind. <see cref="Samples"/> and
/// <see cref="Crop"/> read the store outside it, and <see cref="Rematch"/> matches the samples outside it. Nothing here
/// is logged.
/// </para>
/// </remarks>
public sealed class FaceGroups
{
    /// <summary>The sightings from which a group is shown.</summary>
    public const int ShownAt = 3;
    /// <summary>The most groups a detector keeps; a new one beyond drops the group seen longest ago.</summary>
    public const int MostGroups = 50;
    /// <summary>The most samples a group keeps.</summary>
    public const int MostSamples = 5;
    /// <summary>How long a group has from its first sighting to be shown; one that is not is dropped.</summary>
    public static readonly TimeSpan ShownWithin = TimeSpan.FromHours(1);
    /// <summary>How often at most a group's last seen and count are written when nothing else of it changes.</summary>
    public static readonly TimeSpan WrittenEvery = TimeSpan.FromMinutes(1);

    const int Format = 1;
    const string IndexKey = "groups";

    readonly SharpData data;
    readonly string camera;
    readonly RecognitionRules rules;
    // False for a store of another format, which is neither read nor written.
    readonly bool ours;
    // Guards groups and formatted, and is held across each change's writes to the store.
    readonly Lock gate = new();
    readonly Dictionary<string, Group> groups = new(StringComparer.Ordinal);
    // Whether the store has its format: one found without it afterwards was cleared.
    bool formatted;
    IReadOnlyList<FaceGroup> shown = Array.Empty<FaceGroup>();

    /// <summary>The groups a detector keeps in its store, loaded.</summary>
    /// <param name="data">The detector's own store, its <c>Data</c>.</param>
    /// <param name="camera">The entity id of the detector's camera, which the samples were seen at.</param>
    /// <param name="rules">
    /// The lines to group by. A change of the detector's settings saves its definition, which makes a new detector and
    /// so new groups, read from the same store.
    /// </param>
    public FaceGroups(SharpData data, string camera, RecognitionRules rules)
    {
        this.data = data ?? throw new ArgumentNullException(nameof(data));
        this.camera = camera ?? throw new ArgumentNullException(nameof(camera));
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        var format = ReadFormat();
        ours = format is null or Format;
        if (!ours) return;
        formatted = format == Format;
        Load();
        Publish();
    }

    /// <summary>The groups seen at least <see cref="ShownAt"/> times, the newest sighting first: a snapshot, replaced by each change.</summary>
    public IReadOnlyList<FaceGroup> Shown => Volatile.Read(ref shown);

    /// <summary>Every group, shown or not, by first sighting.</summary>
    internal IReadOnlyList<FaceGroup> All
    {
        get { lock (gate) return groups.Values.OrderBy(group => group.First).Select(group => group.View()).ToList().AsReadOnly(); }
    }

    /// <summary>A group, shown or not; null when there is none of that id.</summary>
    public FaceGroup? Find(string id)
    {
        lock (gate) return id != null && groups.TryGetValue(id, out var group) ? group.View() : null;
    }

    /// <summary>
    /// Notes a face that the people list does not know, or only suggests a person for, seen in a check. It joins every
    /// group it is at least <see cref="RecognitionRules.MatchScore"/> from a sample of, which become one, the group first
    /// seen; it starts a group when there is none. A group counts one sighting a check, so the faces of one check are
    /// given the same <paramref name="now"/>. A group not shown within <see cref="ShownWithin"/> is dropped first.
    /// </summary>
    /// <param name="face">The face's embedding.</param>
    /// <param name="crop">Makes the face's crop, a JPEG of at most 1 MiB; called once when the face is kept as one of the group's samples, and not at all otherwise.</param>
    /// <param name="eyes">The pixels between the face's eyes: a group keeps its larger faces.</param>
    /// <param name="now">When the check saw the face.</param>
    /// <param name="suggestedPersonId">The person the face might be, from a maybe; null for none, which leaves the group's suggestion as it is.</param>
    /// <exception cref="ArgumentException">The embedding has no 128 values, or the crop is null or over 1 MiB.</exception>
    public void Seen(FaceEmbedding face, Func<byte[]> crop, double eyes, DateTimeOffset now, string? suggestedPersonId)
    {
        ArgumentNullException.ThrowIfNull(crop);
        if (face.Values is not { Length: PeopleList.Values }) throw new ArgumentException("A face's embedding has 128 values");
        if (!ours) return;
        var suggested = PeopleList.IsId(suggestedPersonId) ? suggestedPersonId : null;
        lock (gate)
        {
            Cleared();
            Drop(groups.Values.Where(group => !IsShown(group) && now - group.First >= ShownWithin).ToList());
            var close = groups.Values
                .Where(group => group.Samples.Any(sample => sample.Embedding.Similarity(face) >= rules.MatchScore))
                .OrderBy(group => group.First).ThenBy(group => group.Id, StringComparer.Ordinal)
                .ToList();
            if (close.Count == 0) Start(face, crop, eyes, now, suggested);
            else Join(close, face, crop, eyes, now, suggested);
            Publish();
        }
    }

    /// <summary>
    /// Matches every group's samples against the people list again, after it changed: a group a sample of which is now
    /// recognised as someone is removed; one a sample of which is a maybe suggests that person; a suggestion whose person
    /// is gone is cleared.
    /// </summary>
    /// <param name="people">The people list, such as <see cref="PeopleList.People"/>.</param>
    /// <param name="rules">The lines to judge by.</param>
    public void Rematch(IReadOnlyList<Person> people, RecognitionRules rules)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(rules);
        if (!ours) return;
        List<(Group Group, GroupSample[] Samples)> taken;
        lock (gate) taken = groups.Values.Select(group => (group, group.Samples.ToArray())).ToList();
        // Matched outside the lock: it can take a while against a full list, and a check should not wait for it.
        var judged = taken.Select(item => (item.Group, Matches: item.Samples.Select(sample => FaceMatcher.Match(sample.Embedding, people, rules)).ToList())).ToList();
        lock (gate)
        {
            Cleared();
            var recognised = new List<Group>();
            foreach (var (group, matches) in judged)
            {
                // Removed or joined into another meanwhile.
                if (!groups.TryGetValue(group.Id, out var current) || current != group) continue;
                if (matches.Any(match => match.Kind == MatchKind.Recognised))
                {
                    recognised.Add(group);
                    continue;
                }
                var maybe = matches.Where(match => match.Kind == MatchKind.Maybe).MaxBy(match => match.Score);
                var suggested = maybe?.Person?.Id ?? (people.Any(person => person.Id == group.Suggested) ? group.Suggested : null);
                if (suggested == group.Suggested) continue;
                group.Suggested = suggested;
                WriteRecord(group);
            }
            Drop(recognised);
            Publish();
        }
    }

    /// <summary>
    /// Drops the groups that are due: one not shown within <see cref="ShownWithin"/> of its first sighting, and a shown
    /// one not seen for <paramref name="keep"/>.
    /// </summary>
    /// <param name="now">The time now.</param>
    /// <param name="keep">How long a shown group is kept after its last sighting: the detector's <i>Keep unknown faces for</i>.</param>
    public void Expire(DateTimeOffset now, TimeSpan keep)
    {
        if (!ours) return;
        lock (gate)
        {
            Cleared();
            Drop(groups.Values.Where(group => IsShown(group) ? now - group.Last >= keep : now - group.First >= ShownWithin).ToList());
            Publish();
        }
    }

    /// <summary>Removes a group with its samples, once it is named or dismissed. Nothing when it is gone already.</summary>
    public void Remove(string id)
    {
        if (!ours || id == null) return;
        lock (gate)
        {
            Cleared();
            if (!groups.TryGetValue(id, out var group)) return;
            Drop([group]);
            Publish();
        }
    }

    /// <summary>
    /// A group's samples, the largest faces first, with their crops read from the store, as
    /// <see cref="PeopleList.Add"/> and <see cref="PeopleList.AddSamples"/> take them; a sample whose crop is gone is left
    /// out. Empty when there is no such group.
    /// </summary>
    public IReadOnlyList<NewSample> Samples(string id)
    {
        var samples = new List<NewSample>();
        foreach (var sample in Taken(id))
            if (TryBytes(CropKey(id, sample.Id)) is { } crop) samples.Add(new NewSample(sample.Embedding, crop, sample.At, camera));
        return samples.AsReadOnly();
    }

    /// <summary>The crop of a group's largest face, as stored; null when there is no such group or crop.</summary>
    public byte[]? Crop(string groupId)
    {
        foreach (var sample in Taken(groupId))
            if (TryBytes(CropKey(groupId, sample.Id)) is { } crop) return crop;
        return null;
    }

    // ---- Changes; each under the lock ----

    /// <summary>Starts a group of one face, dropping the group seen longest ago when there are <see cref="MostGroups"/>.</summary>
    void Start(FaceEmbedding face, Func<byte[]> crop, double eyes, DateTimeOffset now, string? suggested)
    {
        var bytes = Made(crop);
        if (groups.Count >= MostGroups) Drop([groups.Values.OrderBy(group => group.Last).ThenBy(group => group.Id, StringComparer.Ordinal).First()]);
        var group = new Group(PeopleList.NewId()) { Sightings = 1, First = now, Last = now, Suggested = suggested };
        var sample = new GroupSample(PeopleList.NewId(), face, eyes, now);
        group.Samples.Add(sample);
        groups[group.Id] = group;
        WriteSample(group.Id, sample, bytes);
        WriteRecord(group);
        ChangeIndex(index => index.Add(group.Id));
    }

    /// <summary>
    /// A face joins the groups it is close to: they become the first, with every sighting (this check's once), the best
    /// of their samples, and the face as a sample when it adds to them.
    /// </summary>
    void Join(List<Group> close, FaceEmbedding face, Func<byte[]> crop, double eyes, DateTimeOffset now, string? suggested)
    {
        var (group, others) = (close[0], close.Skip(1).ToList());
        var seenNow = close.Count(g => g.Last == now);
        var sightings = close.Sum(g => g.Sightings) - Math.Max(0, seenNow - 1) + (seenNow == 0 ? 1 : 0);
        // Each sample once: an interrupted join can leave a sample's copy in both groups, which keeps its id.
        var pooled = new List<(Group Owner, GroupSample Sample)>();
        foreach (var g in close)
        foreach (var sample in g.Samples)
            if (!pooled.Any(item => item.Sample.Id == sample.Id)) pooled.Add((g, sample));
        var kept = Best(pooled);
        var (keep, replaced) = Admits(kept.Select(k => k.Sample).ToList(), face, eyes);
        // The crop is made before anything changes, as it may throw.
        var bytes = keep ? Made(crop) : null;

        var added = keep ? new GroupSample(PeopleList.NewId(), face, eyes, now) : null;
        var samples = kept.Select(k => k.Sample).Where(sample => sample != replaced).ToList();
        if (added != null) samples.Add(added);
        var moved = kept.Where(k => k.Owner != group && k.Sample != replaced).Select(k => (k.Owner.Id, k.Sample)).ToList();
        var dropped = group.Samples.Where(sample => !samples.Contains(sample)).ToList();
        var firstShown = !IsShown(group) && sightings >= ShownAt;
        group.Sightings = sightings;
        var latest = close.Max(g => g.Last);
        group.Last = latest > now ? latest : now;
        group.Suggested = suggested ?? group.Suggested ?? others.Select(g => g.Suggested).FirstOrDefault(s => s != null);
        group.Samples = samples;
        foreach (var other in others) groups.Remove(other.Id);

        foreach (var (from, sample) in moved) CopySample(from, group.Id, sample);
        if (added != null) WriteSample(group.Id, added, bytes!);
        if (others.Count > 0 || added != null || dropped.Count > 0 || firstShown || group.Written is not { } written || (now - written).Duration() >= WrittenEvery)
            WriteRecord(group);
        if (others.Count > 0)
        {
            var gone = others.Select(other => other.Id).ToHashSet(StringComparer.Ordinal);
            ChangeIndex(index => index.RemoveAll(gone.Contains));
            foreach (var other in others) Forget(other.Id);
        }
        foreach (var sample in dropped) RemoveSampleKeys(group.Id, sample.Id);
    }

    /// <summary>At most <see cref="MostSamples"/> of the samples: the largest faces first, those unlike the ones chosen before the rest.</summary>
    static List<(Group Owner, GroupSample Sample)> Best(List<(Group Owner, GroupSample Sample)> samples)
    {
        if (samples.Count <= MostSamples) return samples;
        var largest = samples.OrderByDescending(item => item.Sample.Eyes).ToList();
        var chosen = new List<(Group Owner, GroupSample Sample)>();
        foreach (var item in largest)
            if (chosen.Count < MostSamples && chosen.All(c => c.Sample.Embedding.Similarity(item.Sample.Embedding) < FaceMatcher.Alike)) chosen.Add(item);
        foreach (var item in largest)
            if (chosen.Count < MostSamples && !chosen.Contains(item)) chosen.Add(item);
        return chosen;
    }

    /// <summary>
    /// Whether a group keeps a face as a sample, and which sample it takes the place of: a face alike to a sample replaces
    /// it when larger and is not kept otherwise; one unlike every sample is added when there is room, and replaces the
    /// smallest when it is larger.
    /// </summary>
    static (bool Keep, GroupSample? Replaced) Admits(List<GroupSample> samples, FaceEmbedding face, double eyes)
    {
        if (samples.Count == 0) return (true, null);
        var closest = samples.MaxBy(sample => sample.Embedding.Similarity(face))!;
        if (closest.Embedding.Similarity(face) >= FaceMatcher.Alike) return eyes > closest.Eyes ? (true, closest) : (false, null);
        if (samples.Count < MostSamples) return (true, null);
        var smallest = samples.MinBy(sample => sample.Eyes)!;
        return eyes > smallest.Eyes ? (true, smallest) : (false, null);
    }

    /// <summary>Removes groups: from the index first, then their records and samples.</summary>
    void Drop(List<Group> dropped)
    {
        if (dropped.Count == 0) return;
        foreach (var group in dropped) groups.Remove(group.Id);
        var gone = dropped.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        ChangeIndex(index => index.RemoveAll(gone.Contains));
        foreach (var group in dropped) Forget(group.Id);
    }

    /// <summary>Forgets every group when the store was cleared since this last wrote to it, so that none is written back.</summary>
    void Cleared()
    {
        if (!formatted || data.Keys("format").Contains("format")) return;
        groups.Clear();
        formatted = false;
    }

    /// <summary>Replaces <see cref="Shown"/> with the groups shown now.</summary>
    void Publish()
    {
        var list = groups.Values.Where(IsShown).OrderByDescending(group => group.Last).ThenBy(group => group.Id, StringComparer.Ordinal).Select(group => group.View()).ToList();
        Volatile.Write(ref shown, list.AsReadOnly());
    }

    static bool IsShown(Group group) => group.Sightings >= ShownAt;

    /// <exception cref="ArgumentException">The crop is null or over 1 MiB.</exception>
    static byte[] Made(Func<byte[]> crop)
    {
        var bytes = crop() ?? throw new ArgumentException("A face's crop cannot be null");
        if (bytes.Length > PeopleList.LargestCrop) throw new ArgumentException("A face's crop takes at most 1 MiB");
        return bytes;
    }

    /// <summary>A group's samples, the largest first; none when there is no such group.</summary>
    GroupSample[] Taken(string id)
    {
        if (!ours || id == null) return [];
        lock (gate) return groups.TryGetValue(id, out var group) ? [.. group.Samples.OrderByDescending(sample => sample.Eyes)] : [];
    }

    // ---- Loading ----

    /// <summary>Reads the groups, and removes what an interruption left; see the remarks.</summary>
    void Load()
    {
        List<string>? index;
        var rewrite = false;
        try { index = data.Get<List<string>>(IndexKey); }
        catch (InvalidDataException) { (index, rewrite) = (null, true); }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException) { (index, rewrite) = (null, true); }
        foreach (var id in index ?? [])
        {
            if (!PeopleList.IsId(id) || groups.ContainsKey(id) || Read(id) is not { } group)
            {
                rewrite = true;
                continue;
            }
            groups[id] = group;
        }
        foreach (var key in data.Keys("groups/"))
        {
            var parts = key.Split('/');
            var listed = groups.TryGetValue(parts[1], out var group)
                && (parts.Length == 2 || parts.Length == 4 && group.Samples.Any(sample => sample.Id == parts[2]));
            if (listed) continue;
            try { data.Remove(key); }
            catch (Exception e) when (e is not ObjectDisposedException) { }
        }
        if (rewrite) Try(() => data.Set(IndexKey, groups.Keys.Order(StringComparer.Ordinal).ToList()));
    }

    /// <summary>A group as its record and samples have it; null when its record cannot be read or no sample of it can.</summary>
    Group? Read(string id)
    {
        StoredGroup? record;
        try { record = data.Get<StoredGroup>(RecordKey(id)); }
        catch (InvalidDataException) { return null; }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException) { return null; }
        if (record == null) return null;
        var group = new Group(id)
        {
            Sightings = Math.Max(1, record.Sightings),
            First = record.First,
            Last = record.Last,
            Suggested = PeopleList.IsId(record.Suggested) ? record.Suggested : null,
            Written = record.Last,
        };
        foreach (var sample in record.Samples ?? [])
        {
            if (sample == null || !PeopleList.IsId(sample.Id) || group.Samples.Count >= MostSamples || group.Samples.Any(s => s.Id == sample.Id)) continue;
            if (PeopleList.EmbeddingValues(TryBytes(EmbeddingKey(id, sample.Id))) is { } values)
                group.Samples.Add(new GroupSample(sample.Id, new FaceEmbedding(values, sample.Fit), sample.Eyes, sample.At));
        }
        return group.Samples.Count == 0 ? null : group;
    }

    /// <summary>The store's format; null when none is written yet, and -1 when it cannot be read.</summary>
    int? ReadFormat()
    {
        try { return data.Get<int?>("format"); }
        catch (InvalidDataException) { return -1; }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException) { return -1; }
    }

    byte[]? TryBytes(string key)
    {
        try { return data.GetBytes(key); }
        catch (InvalidDataException) { return null; }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException) { return null; }
    }

    // ---- Writing; each under the lock ----

    /// <summary>
    /// A write to the store, after the format when the store has none. One the store refuses (it is full, or a key is of
    /// the wrong kind) is left out: memory stays the truth, and the next load drops what the store lacks.
    /// </summary>
    void Try(Action write)
    {
        try
        {
            if (!formatted)
            {
                data.Update<int?>("format", current => current ?? Format);
                formatted = true;
            }
            write();
        }
        catch (InvalidDataException) { }
        catch (InvalidOperationException e) when (e is not ObjectDisposedException) { }
    }

    void ChangeIndex(Action<List<string>> change) => Try(() => data.Update<List<string>>(IndexKey, index =>
    {
        index ??= [];
        change(index);
        return index;
    }));

    void WriteSample(string groupId, GroupSample sample, byte[] crop) => Try(() =>
    {
        data.SetBytes(EmbeddingKey(groupId, sample.Id), PeopleList.EmbeddingBytes(sample.Embedding.Values));
        data.SetBytes(CropKey(groupId, sample.Id), crop);
    });

    /// <summary>Copies a sample of a group being joined into another, keeping its id; one whose crop is gone is copied without it.</summary>
    void CopySample(string fromId, string intoId, GroupSample sample)
    {
        var crop = TryBytes(CropKey(fromId, sample.Id));
        Try(() =>
        {
            data.SetBytes(EmbeddingKey(intoId, sample.Id), PeopleList.EmbeddingBytes(sample.Embedding.Values));
            if (crop != null) data.SetBytes(CropKey(intoId, sample.Id), crop);
        });
    }

    void WriteRecord(Group group)
    {
        var record = new StoredGroup(group.First, group.Last, group.Sightings, group.Suggested,
            group.Samples.Select(sample => new StoredGroupSample(sample.Id, sample.At, sample.Eyes, sample.Embedding.Fit)).ToList());
        Try(() => data.Set(RecordKey(group.Id), record));
        group.Written = group.Last;
    }

    /// <summary>Removes a group's record and every key of its samples, as far as it can.</summary>
    void Forget(string groupId)
    {
        try
        {
            data.Remove(RecordKey(groupId));
            foreach (var key in data.Keys($"groups/{groupId}/")) data.Remove(key);
        }
        catch (Exception e) when (e is not ObjectDisposedException) { }
    }

    void RemoveSampleKeys(string groupId, string sampleId)
    {
        try
        {
            data.Remove(EmbeddingKey(groupId, sampleId));
            data.Remove(CropKey(groupId, sampleId));
        }
        catch (Exception e) when (e is not ObjectDisposedException) { }
    }

    static string RecordKey(string groupId) => $"groups/{groupId}";
    static string EmbeddingKey(string groupId, string sampleId) => $"groups/{groupId}/{sampleId}/embedding";
    static string CropKey(string groupId, string sampleId) => $"groups/{groupId}/{sampleId}/crop";

    /// <summary>A group in memory; changed only under the lock.</summary>
    sealed class Group(string id)
    {
        public string Id { get; } = id;
        public int Sightings { get; set; }
        public DateTimeOffset First { get; set; }
        public DateTimeOffset Last { get; set; }
        public string? Suggested { get; set; }
        public List<GroupSample> Samples { get; set; } = [];
        /// <summary>The last seen its record was last written with; null when it never was.</summary>
        public DateTimeOffset? Written { get; set; }

        public FaceGroup View() => new(Id, Sightings, First, Last, Suggested);
    }

    /// <summary>A sample of a group: its face, how large it was, and when it was seen; the crop is in the store only.</summary>
    sealed record GroupSample(string Id, FaceEmbedding Embedding, double Eyes, DateTimeOffset At);

    /// <summary>A group as <c>groups/&lt;id&gt;</c> keeps it: without its embeddings and crops.</summary>
    internal sealed record StoredGroup(DateTimeOffset First, DateTimeOffset Last, int Sightings, string? Suggested, List<StoredGroupSample>? Samples);

    /// <summary>A sample as its group's record lists it.</summary>
    internal sealed record StoredGroupSample(string Id, DateTimeOffset At, double Eyes, float Fit);
}

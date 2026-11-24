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

/// <summary>Compares a face's embedding with every sample of every person.</summary>
public static class FaceMatcher
{
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
    const int Values = 128;
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
    float[]? ReadEmbedding(string key)
    {
        if (TryBytes(key) is not { Length: Values * sizeof(float) } bytes) return null;
        var values = new float[Values];
        for (var i = 0; i < Values; i++) values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        return values;
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
            var bytes = new byte[Values * sizeof(float)];
            for (var v = 0; v < Values; v++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(v * sizeof(float)), face.Embedding.Values[v]);
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
    static string NewId() => RandomNumberGenerator.GetString(IdCharacters, IdLength);

    /// <summary>Whether a string can be an id here, and so a part of a key.</summary>
    static bool IsId(string? id) => id is { Length: > 0 and <= 32 } && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

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

//! Calibration: how well SFace tells the same person from different people by face size, on LFW.
//!
//! ```sh
//! cargo run --release --example calibrate -- <lfw dir> <model> [--cache FILE] [--check N]
//! ```
//!
//! `<lfw dir>` holds one folder of JPEGs per person, as LFW's `lfw.tgz` unpacks (`lfw/<name>/<name>_0001.jpg`), and
//! `<model>` is SFace (`face_recognition_sface_2021dec.onnx`). For each picture the detector looks at 640 with a
//! minimum score of 0.7 and takes the face nearest the centre. For each size in [`SIZES`] (pixels between the eyes)
//! the whole picture is shrunk by area averaging until the face is that size, never enlarged, and the face is
//! embedded from the small picture with its landmarks scaled along; the small picture is also put into a camera
//! frame of each of [`FRAMES`] for the detector, to see whether it would still find the face there.
//!
//! The detections and embeddings are kept in a cache file (by default beside `<lfw dir>`, named after it with
//! `.calibrate-cache`), so the statistics run again without embedding again. `--check N` recomputes a seeded sample
//! of N pictures and compares them with the cache bit for bit. Everything drawn at random is drawn from fixed
//! seeds, so a run prints the same tables as the last. The tables go to stdout as Markdown, progress to stderr.
//! Nothing printed names a person or holds a face or an embedding.

use std::fs;
use std::io::{self, BufReader, BufWriter, Read, Write};
use std::path::{Path, PathBuf};
use std::process::ExitCode;
use std::sync::atomic::{AtomicUsize, Ordering};

use hs_vision::align::{fit, similarity};
use hs_vision::detector::Picture;
use hs_vision::recognizer::EMBEDDING;
use hs_vision::{Detector, Face, Recognizer};

/// The face sizes measured, in pixels between the eyes, the largest first.
const SIZES: [usize; 7] = [40, 32, 24, 20, 16, 12, 10];
/// The detector's input size and minimum score, as the People detector runs it.
const INPUT_SIZE: usize = 640;
const MIN_SCORE: f32 = 0.7;
/// The camera frames a shrunk picture is put into for the detector: one as wide as its input, where a face is the
/// size it is, and a 1080p one, which the detector sees at a third of its size.
const FRAMES: [(usize, usize); 2] = [(640, 360), (1920, 1080)];
/// The most pictures of one person taken, so that a few people with hundreds of pictures do not outweigh the rest.
const PER_PERSON: usize = 10;
const DIFFERENT_PAIRS: usize = 1_000_000;
const GALLERIES: usize = 200;
const ENROLLED: usize = 20;
const NAMED: usize = 3;
/// The fewest pictures a person needs to be enrolled: three named and three to probe with.
const ELIGIBLE: usize = 6;
const OUTSIDERS: usize = 200;
const MARGINS: [f32; 5] = [0.0, 0.05, 0.1, 0.15, 0.2];
/// The share of same-person pairs that must match at a size for it to count.
const ENOUGH: f64 = 0.8;
/// The learn size is at least this much above the usable size.
const LEARN_ABOVE: usize = 8;
const SEED: u64 = 0x5eed_2026_1124;
/// The cache's format; a change to what is computed changes it.
const MAGIC: &[u8; 8] = b"HSCALIB1";

/// SplitMix64: small, fast and the same everywhere.
struct Rng(u64);

impl Rng {
    /// The generator for one purpose and one index within it, so that each draws apart from the others.
    fn new(purpose: u64, index: u64) -> Rng {
        let mut rng = Rng(SEED ^ purpose.wrapping_mul(0x9e37_79b9_7f4a_7c15));
        rng.0 ^= rng.next().wrapping_add(index);
        rng
    }

    fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9e37_79b9_7f4a_7c15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
        z ^ (z >> 31)
    }

    /// A number in 0..n.
    fn below(&mut self, n: usize) -> usize {
        ((self.next() as u128 * n as u128) >> 64) as usize
    }

    fn shuffle<T>(&mut self, items: &mut [T]) {
        for i in (1..items.len()).rev() {
            let j = self.below(i + 1);
            items.swap(i, j);
        }
    }
}

/// What the generators are for.
const FOR_CAP: u64 = 1;
const FOR_DIFFERENT: u64 = 2;
const FOR_MIXED: u64 = 3;
const FOR_GALLERY: u64 = 4;
const FOR_CHECK: u64 = 5;

/// The pictures, sorted by person folder and file name, with each one's person.
struct Dataset {
    people: usize,
    images: Vec<(usize, PathBuf)>,
    /// A hash of every picture's path under the folder, which the cache is kept under.
    key: u64,
}

fn fnv(hash: &mut u64, bytes: &[u8]) {
    for &b in bytes {
        *hash ^= b as u64;
        *hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
}

fn sorted_entries(dir: &Path) -> io::Result<Vec<PathBuf>> {
    let mut entries = fs::read_dir(dir)?
        .map(|e| e.map(|e| e.path()))
        .collect::<io::Result<Vec<_>>>()?;
    entries.sort();
    Ok(entries)
}

fn list(dir: &Path, model: &Path) -> io::Result<Dataset> {
    let mut key = 0xcbf2_9ce4_8422_2325;
    fnv(&mut key, MAGIC);
    fnv(&mut key, &fs::metadata(model)?.len().to_le_bytes());
    let mut images = Vec::new();
    let mut people = 0;
    for person in sorted_entries(dir)? {
        if !person.is_dir() {
            continue;
        }
        let mut any = false;
        for file in sorted_entries(&person)? {
            if file.extension().is_some_and(|e| e == "jpg") && file.is_file() {
                let relative = file.strip_prefix(dir).expect("listed under the folder");
                fnv(&mut key, relative.to_string_lossy().as_bytes());
                fnv(&mut key, b"\n");
                images.push((people, file));
                any = true;
            }
        }
        if any {
            people += 1;
        }
    }
    Ok(Dataset {
        people,
        images,
        key,
    })
}

/// A picture of its own.
struct Rgb {
    rgb: Vec<u8>,
    width: usize,
    height: usize,
}

impl Rgb {
    fn open(path: &Path) -> Result<Rgb, String> {
        let img = image::open(path)
            .map_err(|e| format!("{} could not be read: {e}", path.display()))?
            .to_rgb8();
        let (width, height) = (img.width() as usize, img.height() as usize);
        Ok(Rgb {
            rgb: img.into_raw(),
            width,
            height,
        })
    }

    fn picture(&self) -> Picture<'_> {
        Picture {
            rgb: &self.rgb,
            width: self.width,
            height: self.height,
            stride: self.width * 3,
        }
    }

    /// The picture shrunk to `width` × `height` (no larger than it is) by area averaging: each new pixel is the mean
    /// of the old ones it covers, each weighed by how much of it it covers, as a camera's sensor sees a small face.
    fn shrink(&self, width: usize, height: usize) -> Rgb {
        assert!(width <= self.width && height <= self.height && width > 0 && height > 0);
        let (xs, ys) = (area_taps(self.width, width), area_taps(self.height, height));
        // Each row shrunk first, then each column of the result.
        let mut rows = vec![0f64; self.height * width * 3];
        for y in 0..self.height {
            for (x, taps) in xs.iter().enumerate() {
                let out = &mut rows[(y * width + x) * 3..][..3];
                for &(s, w) in taps {
                    let px = &self.rgb[(y * self.width + s) * 3..][..3];
                    for (o, &p) in out.iter_mut().zip(px) {
                        *o += w * p as f64;
                    }
                }
            }
        }
        let mut rgb = vec![0u8; width * height * 3];
        for (y, taps) in ys.iter().enumerate() {
            for x in 0..width {
                let mut px = [0f64; 3];
                for &(s, w) in taps {
                    let row = &rows[(s * width + x) * 3..][..3];
                    for (p, &r) in px.iter_mut().zip(row) {
                        *p += w * r;
                    }
                }
                for (o, p) in rgb[(y * width + x) * 3..][..3].iter_mut().zip(px) {
                    *o = p.round().clamp(0.0, 255.0) as u8;
                }
            }
        }
        Rgb { rgb, width, height }
    }

    /// The picture in the middle of a black `width` × `height` frame, and where its top-left corner went.
    fn framed(&self, width: usize, height: usize) -> (Rgb, (usize, usize)) {
        assert!(self.width <= width && self.height <= height);
        let (left, top) = ((width - self.width) / 2, (height - self.height) / 2);
        let mut rgb = vec![0u8; width * height * 3];
        for y in 0..self.height {
            let from = &self.rgb[y * self.width * 3..][..self.width * 3];
            rgb[((top + y) * width + left) * 3..][..self.width * 3].copy_from_slice(from);
        }
        (Rgb { rgb, width, height }, (left, top))
    }
}

/// For each of `to` pixels along an axis of `from` (`to` ≤ `from`), the old pixels it covers and the share of it
/// each one fills.
fn area_taps(from: usize, to: usize) -> Vec<Vec<(usize, f64)>> {
    let ratio = from as f64 / to as f64;
    (0..to)
        .map(|t| {
            let (start, end) = (t as f64 * ratio, (t + 1) as f64 * ratio);
            (start.floor() as usize..(end.ceil() as usize).min(from))
                .filter_map(|s| {
                    let covered = end.min(s as f64 + 1.0) - start.max(s as f64);
                    (covered > 0.0).then_some((s, covered / ratio))
                })
                .collect()
        })
        .collect()
}

/// The face found in a picture, in its pixels.
#[derive(Clone, Copy)]
struct Original {
    rect: [f32; 4],
    landmarks: [f32; 10],
    /// Pixels between the eyes.
    eyes: f32,
    /// The alignment's fit, in crop pixels.
    fit: f32,
}

/// The face at one size.
#[derive(Clone)]
struct Scaled {
    embedding: Option<Box<[f32; EMBEDDING]>>,
    /// For each of [`FRAMES`], the fit of the face the detector found in the small picture there, if it found it.
    found: [Option<f32>; 2],
}

const NOT_AT_SIZE: Scaled = Scaled {
    embedding: None,
    found: [None, None],
};

struct Record {
    face: Option<Original>,
    /// One per [`SIZES`], empty when no face was found.
    sizes: Vec<Scaled>,
}

fn eyes(landmarks: &[f32; 10]) -> f32 {
    (landmarks[2] - landmarks[0]).hypot(landmarks[3] - landmarks[1])
}

fn landmark_fit(landmarks: &[f32; 10]) -> f32 {
    fit(&similarity(landmarks), landmarks) as f32
}

fn centre(f: &Face) -> (f32, f32) {
    (f.x + f.w / 2.0, f.y + f.h / 2.0)
}

/// The face in `faces` whose centre lies in `rect` and is nearest the point `(cx, cy)`.
fn nearest(faces: &[Face], (cx, cy): (f32, f32), rect: Option<[f32; 4]>) -> Option<&Face> {
    let inside = |f: &&Face| {
        let (x, y) = centre(f);
        rect.is_none_or(|[rx, ry, rw, rh]| x >= rx && x <= rx + rw && y >= ry && y <= ry + rh)
    };
    let away = |f: &Face| {
        let (x, y) = centre(f);
        (x - cx).hypot(y - cy)
    };
    faces
        .iter()
        .filter(inside)
        .min_by(|a, b| away(a).total_cmp(&away(b)))
}

/// Detects, shrinks and embeds one picture.
fn process(path: &Path, detector: &Detector, recognizer: &Recognizer) -> Result<Record, String> {
    let original = Rgb::open(path)?;
    let faces = detector.faces(&original.picture(), INPUT_SIZE, MIN_SCORE)?;
    let middle = (original.width as f32 / 2.0, original.height as f32 / 2.0);
    let Some(face) = nearest(&faces, middle, None) else {
        return Ok(Record {
            face: None,
            sizes: Vec::new(),
        });
    };
    let found = Original {
        rect: [face.x, face.y, face.w, face.h],
        landmarks: face.landmarks,
        eyes: eyes(&face.landmarks),
        fit: landmark_fit(&face.landmarks),
    };
    let mut sizes = Vec::with_capacity(SIZES.len());
    for size in SIZES {
        if found.eyes < size as f32 {
            sizes.push(NOT_AT_SIZE);
            continue;
        }
        let scale = size as f64 / found.eyes as f64;
        let side = |v: usize| ((v as f64 * scale).round() as usize).clamp(1, v);
        let small = original.shrink(side(original.width), side(original.height));
        let (sx, sy) = (
            (small.width as f64 / original.width as f64) as f32,
            (small.height as f64 / original.height as f64) as f32,
        );
        let landmarks: [f32; 10] =
            std::array::from_fn(|i| found.landmarks[i] * if i % 2 == 0 { sx } else { sy });
        let (embedding, _) = recognizer.embed(&small.picture(), &landmarks)?;
        let mut seen = [None, None];
        for (slot, (width, height)) in seen.iter_mut().zip(FRAMES) {
            let (frame, (left, top)) = small.framed(width, height);
            let [x, y, w, h] = found.rect;
            let rect = [x * sx + left as f32, y * sy + top as f32, w * sx, h * sy];
            let faces = detector.faces(&frame.picture(), INPUT_SIZE, MIN_SCORE)?;
            let at = (rect[0] + rect[2] / 2.0, rect[1] + rect[3] / 2.0);
            *slot = nearest(&faces, at, Some(rect)).map(|f| landmark_fit(&f.landmarks));
        }
        sizes.push(Scaled {
            embedding: Some(Box::new(embedding)),
            found: seen,
        });
    }
    Ok(Record {
        face: Some(found),
        sizes,
    })
}

/// The records of the pictures `which` names, computed on every core, in that order.
fn compute(dataset: &Dataset, which: &[usize], model: &Path) -> Result<Vec<Record>, String> {
    let recognizer = Recognizer::open(model)?;
    let threads = std::thread::available_parallelism().map_or(4, |n| n.get());
    let next = AtomicUsize::new(0);
    let done = AtomicUsize::new(0);
    let mut results: Vec<(usize, Record)> = std::thread::scope(|s| {
        let workers: Vec<_> = (0..threads)
            .map(|_| {
                s.spawn(|| -> Result<Vec<(usize, Record)>, String> {
                    // A detector runs its model under a lock, so each thread has its own.
                    let detector = Detector::open()?;
                    let mut out = Vec::new();
                    loop {
                        let n = next.fetch_add(1, Ordering::Relaxed);
                        let Some(&i) = which.get(n) else {
                            return Ok(out);
                        };
                        out.push((i, process(&dataset.images[i].1, &detector, &recognizer)?));
                        let finished = done.fetch_add(1, Ordering::Relaxed) + 1;
                        if finished.is_multiple_of(1000) || finished == which.len() {
                            eprintln!("{finished} of {} pictures", which.len());
                        }
                    }
                })
            })
            .collect();
        let mut all = Vec::new();
        for worker in workers {
            all.extend(worker.join().expect("a worker panicked")?);
        }
        Ok::<_, String>(all)
    })?;
    results.sort_by_key(|(i, _)| *i);
    Ok(results.into_iter().map(|(_, r)| r).collect())
}

fn encode(record: &Record, out: &mut Vec<u8>) {
    let f32s = |out: &mut Vec<u8>, values: &[f32]| {
        for v in values {
            out.extend_from_slice(&v.to_le_bytes());
        }
    };
    let Some(face) = &record.face else {
        out.push(0);
        return;
    };
    out.push(1);
    f32s(out, &face.rect);
    f32s(out, &face.landmarks);
    f32s(out, &[face.eyes, face.fit]);
    for scaled in &record.sizes {
        let mut flags = scaled.embedding.is_some() as u8;
        for (bit, found) in scaled.found.iter().enumerate() {
            flags |= (found.is_some() as u8) << (bit + 1);
        }
        out.push(flags);
        f32s(out, &scaled.found.map(|f| f.unwrap_or(0.0)));
        if let Some(embedding) = &scaled.embedding {
            f32s(out, &embedding[..]);
        }
    }
}

struct Decoder<R: Read>(R);

impl<R: Read> Decoder<R> {
    fn byte(&mut self) -> io::Result<u8> {
        let mut b = [0u8; 1];
        self.0.read_exact(&mut b)?;
        Ok(b[0])
    }

    fn f32s<const N: usize>(&mut self) -> io::Result<[f32; N]> {
        let mut values = [0f32; N];
        for v in &mut values {
            let mut b = [0u8; 4];
            self.0.read_exact(&mut b)?;
            *v = f32::from_le_bytes(b);
        }
        Ok(values)
    }

    fn record(&mut self) -> io::Result<Record> {
        if self.byte()? == 0 {
            return Ok(Record {
                face: None,
                sizes: Vec::new(),
            });
        }
        let rect = self.f32s::<4>()?;
        let landmarks = self.f32s::<10>()?;
        let [eyes, fit] = self.f32s::<2>()?;
        let mut sizes = Vec::with_capacity(SIZES.len());
        for _ in SIZES {
            let flags = self.byte()?;
            let fits = self.f32s::<2>()?;
            let found = std::array::from_fn(|i| (flags & (2 << i) != 0).then_some(fits[i]));
            let embedding = if flags & 1 != 0 {
                Some(Box::new(self.f32s::<EMBEDDING>()?))
            } else {
                None
            };
            sizes.push(Scaled { embedding, found });
        }
        Ok(Record {
            face: Some(Original {
                rect,
                landmarks,
                eyes,
                fit,
            }),
            sizes,
        })
    }
}

fn write_cache(path: &Path, key: u64, records: &[Record]) -> io::Result<()> {
    let partial = path.with_extension("partial");
    let mut out = BufWriter::new(fs::File::create(&partial)?);
    out.write_all(MAGIC)?;
    out.write_all(&key.to_le_bytes())?;
    out.write_all(&(records.len() as u64).to_le_bytes())?;
    let mut bytes = Vec::new();
    for record in records {
        bytes.clear();
        encode(record, &mut bytes);
        out.write_all(&bytes)?;
    }
    out.into_inner().map_err(|e| e.into_error())?.sync_all()?;
    fs::rename(&partial, path)
}

/// The cached records, or None when there is no cache for these pictures and this model.
fn read_cache(path: &Path, key: u64, count: usize) -> io::Result<Option<Vec<Record>>> {
    let file = match fs::File::open(path) {
        Ok(file) => file,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(e),
    };
    let mut input = Decoder(BufReader::new(file));
    let mut header = [0u8; 24];
    input.0.read_exact(&mut header)?;
    if &header[..8] != MAGIC
        || header[8..16] != key.to_le_bytes()
        || header[16..] != (count as u64).to_le_bytes()
    {
        return Ok(None);
    }
    (0..count)
        .map(|_| input.record())
        .collect::<io::Result<Vec<_>>>()
        .map(Some)
}

fn dot(a: &[f32; EMBEDDING], b: &[f32; EMBEDDING]) -> f32 {
    a.iter().zip(b).map(|(x, y)| x * y).sum()
}

/// Scores sorted from lowest to highest.
struct Scores(Vec<f32>);

impl Scores {
    fn new(mut values: Vec<f32>) -> Scores {
        values.sort_by(f32::total_cmp);
        Scores(values)
    }

    fn len(&self) -> usize {
        self.0.len()
    }

    /// The `q` quantile, by nearest rank.
    fn quantile(&self, q: f64) -> f32 {
        let rank = ((q * self.len() as f64).ceil() as usize).clamp(1, self.len());
        self.0[rank - 1]
    }

    /// The lowest line at which at most one score in `per` is at or above it.
    fn line(&self, per: usize) -> f32 {
        let allowed = self.len() / per;
        if allowed >= self.len() {
            return f32::NEG_INFINITY;
        }
        // Above the (allowed + 1)th highest score: only the `allowed` highest can be at or above it.
        self.0[self.len() - 1 - allowed].next_up()
    }

    /// The share of scores at or above `line`.
    fn share(&self, line: f32) -> f64 {
        let below = self.0.partition_point(|&v| v < line);
        (self.len() - below) as f64 / self.len() as f64
    }
}

/// Same-person and different-person scores of one comparison.
struct Pairs {
    same: Scores,
    different: Scores,
}

fn up_to_05(v: f32) -> f32 {
    ((v as f64 * 20.0 - 1e-9).ceil() / 20.0) as f32
}

fn nearest_05(v: f32) -> f32 {
    ((v as f64 * 20.0).round() / 20.0) as f32
}

fn percent(share: f64) -> String {
    format!("{:.1} %", share * 100.0)
}

/// The statistics, from the records.
struct Study<'a> {
    dataset: &'a Dataset,
    records: &'a [Record],
    /// For each person, at most [`PER_PERSON`] of their pictures with a face, drawn at random.
    capped: Vec<Vec<usize>>,
}

impl Study<'_> {
    fn embedding(&self, image: usize, size: usize) -> Option<&[f32; EMBEDDING]> {
        self.records[image]
            .sizes
            .get(size)
            .and_then(|s| s.embedding.as_deref())
    }

    fn person(&self, image: usize) -> usize {
        self.dataset.images[image].0
    }

    /// Each person's capped pictures that have a face at `size`.
    fn members(&self, size: usize) -> Vec<Vec<usize>> {
        self.capped
            .iter()
            .map(|images| {
                images
                    .iter()
                    .copied()
                    .filter(|&i| self.embedding(i, size).is_some())
                    .collect()
            })
            .collect()
    }

    /// Every pair of one person's pictures, the first at size `a` and the second at size `b`, and
    /// [`DIFFERENT_PAIRS`] pairs of two people's, drawn from the capped pictures.
    fn pairs(&self, a: usize, b: usize) -> Pairs {
        let (at_a, at_b) = (self.members(a), self.members(b));
        let mut same = Vec::new();
        for (first, second) in at_a.iter().zip(&at_b) {
            for (n, &i) in first.iter().enumerate() {
                for (m, &j) in second.iter().enumerate() {
                    // One size: each pair once. Two sizes: each picture at each size once.
                    let keep = if a == b { n < m } else { i != j };
                    if keep {
                        same.push(dot(
                            self.embedding(i, a).unwrap(),
                            self.embedding(j, b).unwrap(),
                        ));
                    }
                }
            }
        }
        let (pool_a, pool_b): (Vec<usize>, Vec<usize>) = (at_a.concat(), at_b.concat());
        let purpose = if a == b { FOR_DIFFERENT } else { FOR_MIXED };
        let mut rng = Rng::new(purpose, (a * SIZES.len() + b) as u64);
        let mut different = Vec::with_capacity(DIFFERENT_PAIRS);
        while different.len() < DIFFERENT_PAIRS {
            let (i, j) = (
                pool_a[rng.below(pool_a.len())],
                pool_b[rng.below(pool_b.len())],
            );
            if self.person(i) != self.person(j) {
                different.push(dot(
                    self.embedding(i, a).unwrap(),
                    self.embedding(j, b).unwrap(),
                ));
            }
        }
        Pairs {
            same: Scores::new(same),
            different: Scores::new(different),
        }
    }
}

/// How one gallery probe came out: whether it is an enrolled person's, whether the best person is theirs, and the
/// best and second-best people's scores.
struct Probe {
    genuine: bool,
    right: bool,
    best: f32,
    second: f32,
}

impl Study<'_> {
    /// [`GALLERIES`] galleries at `size`, each of [`ENROLLED`] people with [`NAMED`] named pictures, probed with
    /// their other pictures and with all of [`OUTSIDERS`] people not enrolled.
    fn galleries(&self, size: usize) -> Vec<Probe> {
        let members = self.members(size);
        let eligible: Vec<usize> = (0..members.len())
            .filter(|&p| members[p].len() >= ELIGIBLE)
            .collect();
        let mut probes = Vec::new();
        for g in 0..GALLERIES {
            let mut rng = Rng::new(FOR_GALLERY, g as u64);
            let mut chosen = eligible.clone();
            rng.shuffle(&mut chosen);
            chosen.truncate(ENROLLED);
            let mut named = Vec::new();
            let mut probing = Vec::new();
            for &p in &chosen {
                let mut images = members[p].clone();
                rng.shuffle(&mut images);
                named.push(images[..NAMED].to_vec());
                probing.extend(images[NAMED..].iter().map(|&i| (i, true)));
            }
            let mut others: Vec<usize> = (0..members.len())
                .filter(|p| !members[*p].is_empty() && !chosen.contains(p))
                .collect();
            rng.shuffle(&mut others);
            for &p in others.iter().take(OUTSIDERS) {
                probing.extend(members[p].iter().map(|&i| (i, false)));
            }
            for (image, genuine) in probing {
                let probe = self.embedding(image, size).unwrap();
                let mut scores: Vec<(f32, usize)> = named
                    .iter()
                    .zip(&chosen)
                    .map(|(samples, &p)| {
                        let best = samples
                            .iter()
                            .map(|&s| dot(probe, self.embedding(s, size).unwrap()))
                            .fold(f32::NEG_INFINITY, f32::max);
                        (best, p)
                    })
                    .collect();
                scores.sort_by(|a, b| b.0.total_cmp(&a.0));
                probes.push(Probe {
                    genuine,
                    right: scores[0].1 == self.person(image),
                    best: scores[0].0,
                    second: scores[1].0,
                });
            }
        }
        probes
    }
}

/// For each margin of [`MARGINS`], at the score `line`: genuine probes recognised as their person, all probes
/// recognised as someone they are not, of those the genuine ones and the outsiders.
fn recognition(probes: &[Probe], line: f32) -> Vec<(f32, f64, usize, usize, usize)> {
    let genuine = probes.iter().filter(|p| p.genuine).count();
    MARGINS
        .iter()
        .map(|&margin| {
            let recognised = |p: &&Probe| p.best >= line && p.best - p.second >= margin;
            let right = probes
                .iter()
                .filter(recognised)
                .filter(|p| p.genuine && p.right)
                .count();
            let wrong_genuine = probes
                .iter()
                .filter(recognised)
                .filter(|p| p.genuine && !p.right)
                .count();
            let wrong_outsiders = probes
                .iter()
                .filter(recognised)
                .filter(|p| !p.genuine)
                .count();
            (
                margin,
                right as f64 / genuine as f64,
                wrong_genuine + wrong_outsiders,
                wrong_genuine,
                wrong_outsiders,
            )
        })
        .collect()
}

/// The lowest score line at which, with `margin`, at most one probe in `per` is recognised as someone it is not.
fn gallery_line(probes: &[Probe], margin: f32, per: usize) -> f32 {
    let wrong: Vec<f32> = probes
        .iter()
        .filter(|p| !(p.genuine && p.right) && p.best - p.second >= margin)
        .map(|p| p.best)
        .collect();
    let allowed = probes.len() / per;
    if wrong.len() <= allowed {
        return f32::NEG_INFINITY;
    }
    let wrong = Scores::new(wrong);
    wrong.0[wrong.len() - 1 - allowed].next_up()
}

/// The `qs` quantiles of `values` to two places, a dash for each when there are none.
fn quantiles(values: Vec<f32>, qs: &[f64]) -> Vec<String> {
    let scores = Scores::new(values);
    qs.iter()
        .map(|&q| {
            if scores.len() == 0 {
                "–".into()
            } else {
                format!("{:.2}", scores.quantile(q))
            }
        })
        .collect()
}

/// Quantiles as the cells that end a table row.
fn cells(values: Vec<f32>, qs: &[f64]) -> String {
    quantiles(values, qs)
        .iter()
        .map(|q| format!(" {q} |"))
        .collect()
}

fn print_lines(label: &str, pairs: &[(String, Pairs)]) {
    println!(
        "| {label} | Same-person pairs | Different-person pairs | 1 in 100 | Same ≥ | 1 in 1,000 | Same ≥ | 1 in 10,000 | Same ≥ |"
    );
    println!("|---|---|---|---|---|---|---|---|---|");
    for (name, p) in pairs {
        print!("| {name} | {} | {} |", p.same.len(), p.different.len());
        for per in [100, 1000, 10_000] {
            let line = p.different.line(per);
            print!(" {line:.4} | {} |", percent(p.same.share(line)));
        }
        println!();
    }
}

fn print_quantiles(label: &str, pairs: &[(String, Pairs)]) {
    const SAME: [f64; 7] = [0.01, 0.05, 0.1, 0.2, 0.5, 0.8, 0.9];
    const DIFFERENT: [f64; 6] = [0.5, 0.9, 0.99, 0.999, 0.9999, 1.0];
    println!("Same-person scores, by quantile:");
    println!();
    println!("| {label} | 1 % | 5 % | 10 % | 20 % | 50 % | 80 % | 90 % |");
    println!("|---|---|---|---|---|---|---|---|");
    for (name, p) in pairs {
        println!("| {name} |{}", cells(p.same.0.clone(), &SAME));
    }
    println!();
    println!("Different-person scores, by quantile:");
    println!();
    println!("| {label} | 50 % | 90 % | 99 % | 99.9 % | 99.99 % | highest |");
    println!("|---|---|---|---|---|---|---|");
    for (name, p) in pairs {
        println!("| {name} |{}", cells(p.different.0.clone(), &DIFFERENT));
    }
    println!();
}

fn print_recognition(title: &str, rows: &[(f32, f64, usize, usize, usize)], probes: usize) {
    println!("{title}");
    println!();
    println!("| Margin | Genuine probes recognised right | Probes recognised wrong | Of all probes | Genuine | Not enrolled |");
    println!("|---|---|---|---|---|---|");
    for &(margin, right, wrong, genuine, outsiders) in rows {
        println!(
            "| {margin:.2} | {} | {wrong} | {:.3} % | {genuine} | {outsiders} |",
            percent(right),
            wrong as f64 * 100.0 / probes as f64
        );
    }
    println!();
}

fn study(dataset: &Dataset, records: &[Record]) {
    let mut capped = vec![Vec::new(); dataset.people];
    for (i, (person, _)) in dataset.images.iter().enumerate() {
        if records[i].face.is_some() {
            capped[*person].push(i);
        }
    }
    for (p, images) in capped.iter_mut().enumerate() {
        Rng::new(FOR_CAP, p as u64).shuffle(images);
        images.truncate(PER_PERSON);
        images.sort_unstable();
    }
    let s = Study {
        dataset,
        records,
        capped,
    };

    let faces: Vec<&Original> = records.iter().filter_map(|r| r.face.as_ref()).collect();
    println!("## Faces");
    println!();
    println!(
        "{} pictures of {} people; the detector found a face in {} of them, {} with two or more pictures.",
        dataset.images.len(),
        dataset.people,
        faces.len(),
        s.capped.iter().filter(|c| c.len() >= 2).count()
    );
    println!(
        "Pixels between the eyes in the pictures as they are, by quantile (1 %, 10 %, 50 %, 90 %, 99 %): {}.",
        quantiles(
            faces.iter().map(|f| f.eyes).collect(),
            &[0.01, 0.1, 0.5, 0.9, 0.99]
        )
        .join(", ")
    );
    println!();
    println!("| Size | Faces at the size | Found in a 640 frame | Found in a 1080p frame | Fit when found in a 640 frame (50 %, 90 %, 99 %) |");
    println!("|---|---|---|---|---|");
    for (k, size) in SIZES.iter().enumerate() {
        let at: Vec<&Scaled> = records
            .iter()
            .filter_map(|r| r.sizes.get(k))
            .filter(|s| s.embedding.is_some())
            .collect();
        let found =
            |f: usize| at.iter().filter(|s| s.found[f].is_some()).count() as f64 / at.len() as f64;
        println!(
            "| {size} | {} | {} | {} | {} |",
            at.len(),
            percent(found(0)),
            percent(found(1)),
            quantiles(
                at.iter().filter_map(|s| s.found[0]).collect(),
                &[0.5, 0.9, 0.99]
            )
            .join(", ")
        );
    }
    println!();

    let by_size: Vec<(String, Pairs)> = SIZES
        .iter()
        .enumerate()
        .map(|(k, size)| (size.to_string(), s.pairs(k, k)))
        .collect();
    println!("## Pairs at one size");
    println!();
    println!("The line for 1 in N is the lowest score at which at most one different-person pair in N scores at or above it; Same ≥ is the share of same-person pairs at or above that line.");
    println!();
    print_lines("Size", &by_size);
    println!();
    print_quantiles("Size", &by_size);

    let mixed: Vec<(String, Pairs)> = SIZES
        .iter()
        .enumerate()
        .skip(1)
        .map(|(k, size)| (format!("40 and {size}"), s.pairs(0, k)))
        .collect();
    println!("## Pairs of two sizes");
    println!();
    println!("One picture at 40 px between the eyes and the other at the smaller size; each same-person pair both ways round.");
    println!();
    print_lines("Sizes", &mixed);
    println!();
    print_quantiles("Sizes", &mixed);

    // The defaults, by the rules of the brief.
    let shares: Vec<f64> = by_size
        .iter()
        .map(|(_, p)| p.same.share(p.different.line(1000)))
        .collect();
    let usable = (0..SIZES.len()).rev().find(|&k| shares[k] >= ENOUGH);
    let verdict = if usable.is_some() { "PASS" } else { "STOP" };
    // Without a size that passes, the rest is measured at the one that comes closest, and says so.
    let u = usable.unwrap_or_else(|| {
        (0..SIZES.len())
            .max_by(|&a, &b| shares[a].total_cmp(&shares[b]))
            .unwrap()
    });
    if let Some(k) = usable {
        if (0..k).any(|larger| shares[larger] < ENOUGH) {
            println!(
                "Note: a size larger than {} does not reach 8 in 10.",
                SIZES[k]
            );
            println!();
        }
    }
    let at_u = &by_size[u].1;
    let match_line = at_u.different.line(1000);
    let match_score = up_to_05(match_line);
    let suggest_line = at_u.different.line(100);
    let suggest_score = nearest_05(suggest_line);
    let learn_line = at_u.different.line(10_000);
    let learn_score = up_to_05(learn_line);
    let learn_size = (0..SIZES.len())
        .rev()
        .find(|&k| by_size[k].1.same.share(learn_score) >= ENOUGH)
        .map(|k| SIZES[k]);
    let learn_eyes = learn_size.map(|size| size.max(SIZES[u] + LEARN_ABOVE));

    println!("## At the rounded lines");
    println!();
    println!(
        "The usable size is {} px. Its lines: 1 in 100 at {suggest_line:.4} (suggest, rounded to {suggest_score:.2}), 1 in 1,000 at {match_line:.4} (match, rounded up to {match_score:.2}), 1 in 10,000 at {learn_line:.4} (learn, rounded up to {learn_score:.2}).",
        SIZES[u]
    );
    println!();
    println!("| Size | Same ≥ {suggest_score:.2} | Different ≥ {suggest_score:.2} | Same ≥ {match_score:.2} | Different ≥ {match_score:.2} | Same ≥ {learn_score:.2} | Different ≥ {learn_score:.2} |");
    println!("|---|---|---|---|---|---|---|");
    for (name, p) in &by_size {
        print!("| {name} |");
        for line in [suggest_score, match_score, learn_score] {
            print!(
                " {} | {:.4} % |",
                percent(p.same.share(line)),
                p.different.share(line) * 100.0
            );
        }
        println!();
    }
    println!();

    let probes = s.galleries(u);
    let genuine = probes.iter().filter(|p| p.genuine).count();
    println!("## Galleries at {} px", SIZES[u]);
    println!();
    println!(
        "{GALLERIES} galleries of {ENROLLED} people with {NAMED} named pictures each, drawn from the {} people with at least {ELIGIBLE} pictures at this size (of their {PER_PERSON} drawn); {} probes, {genuine} of them enrolled people's other pictures and {} pictures of {OUTSIDERS} people not enrolled per gallery. A probe is recognised as the best person when that person's score (their best named picture's) is at or above the line and beats the second-best person's by the margin.",
        s.members(u).iter().filter(|m| m.len() >= ELIGIBLE).count(),
        probes.len(),
        probes.len() - genuine,
    );
    println!();
    let at_match = recognition(&probes, match_score);
    let at_learn = recognition(&probes, learn_score);
    print_recognition(
        &format!("At the match score, {match_score:.2}:"),
        &at_match,
        probes.len(),
    );
    print_recognition(
        &format!("At the learn score, {learn_score:.2}:"),
        &at_learn,
        probes.len(),
    );
    let margin = |rows: &[(f32, f64, usize, usize, usize)], per: usize| {
        rows.iter().find(|r| r.2 * per <= probes.len()).map(|r| r.0)
    };
    let match_margin = margin(&at_match, 1000);
    let learn_margin = margin(&at_learn, 10_000);
    for (name, found, rows, per) in [
        ("match", match_margin, &at_match, "1,000"),
        ("learn", learn_margin, &at_learn, "10,000"),
    ] {
        if found.is_none() {
            let fewest = rows.iter().min_by_key(|r| r.2).unwrap();
            println!(
                "No margin up to {:.2} keeps the probes recognised wrong at the {name} score to 1 in {per}; the fewest are at {:.2}, {:.3} %.",
                MARGINS[MARGINS.len() - 1],
                fewest.0,
                fewest.2 as f64 * 100.0 / probes.len() as f64
            );
            println!();
        }
    }
    println!("The lowest score at which, with each margin, at most 1 in 1,000 or 1 in 10,000 probes is recognised wrong, and the genuine probes recognised right there:");
    println!();
    println!("| Margin | 1 in 1,000 | Genuine right | 1 in 10,000 | Genuine right |");
    println!("|---|---|---|---|---|");
    for margin in MARGINS {
        print!("| {margin:.2} |");
        for per in [1000, 10_000] {
            let line = gallery_line(&probes, margin, per);
            let right = recognition(&probes, line)
                .into_iter()
                .find(|r| r.0 == margin)
                .unwrap()
                .1;
            print!(" {line:.4} | {} |", percent(right));
        }
        println!();
    }
    println!();

    // The fit of each capped face at the usable size with another picture of the same person there, by whether its
    // best same-person score reaches the match score.
    let members = s.members(u);
    let (mut matched, mut unmatched) = (Vec::new(), Vec::new());
    for images in &members {
        for &i in images {
            let best = images
                .iter()
                .filter(|&&j| j != i)
                .map(|&j| dot(s.embedding(i, u).unwrap(), s.embedding(j, u).unwrap()))
                .fold(f32::NEG_INFINITY, f32::max);
            if best == f32::NEG_INFINITY {
                continue;
            }
            let fit = records[i].face.as_ref().unwrap().fit;
            if best >= match_score {
                matched.push(fit);
            } else {
                unmatched.push(fit);
            }
        }
    }
    let fit_limit = Scores::new(matched.clone()).quantile(0.99).ceil();
    const FIT: [f64; 6] = [0.1, 0.5, 0.9, 0.95, 0.99, 1.0];
    println!("## Fit");
    println!();
    println!("The alignment's fit in crop pixels, of the faces as detected in the pictures as they are; matched means the face's best same-person score at {} px reaches the match score.", SIZES[u]);
    println!();
    println!("| Faces | Count | 10 % | 50 % | 90 % | 95 % | 99 % | highest |");
    println!("|---|---|---|---|---|---|---|---|");
    println!(
        "| All | {} |{}",
        faces.len(),
        cells(faces.iter().map(|f| f.fit).collect(), &FIT)
    );
    println!(
        "| Matched | {} |{}",
        matched.len(),
        cells(matched.clone(), &FIT)
    );
    println!(
        "| Not matched | {} |{}",
        unmatched.len(),
        cells(unmatched.clone(), &FIT)
    );
    println!();
    let over =
        |fits: &[f32]| fits.iter().filter(|&&f| f > fit_limit).count() as f64 / fits.len() as f64;
    println!(
        "Over {fit_limit:.0}: {} of the matched faces, {} of those not matched.",
        percent(over(&matched)),
        percent(over(&unmatched))
    );
    println!();
    println!("| Size | Found in a 640 frame with a fit over {fit_limit:.0} |");
    println!("|---|---|");
    for (k, size) in SIZES.iter().enumerate() {
        let fits: Vec<f32> = records
            .iter()
            .filter_map(|r| r.sizes.get(k).and_then(|s| s.found[0]))
            .collect();
        println!("| {size} | {} |", percent(over(&fits)));
    }
    println!();

    // Not the defaults themselves: where a rule finds no value this says none, and the defaults need a person's
    // choice.
    let show = |v: Option<String>| v.unwrap_or_else(|| "none".into());
    println!("## What the rules give");
    println!();
    println!("| Rule | Value |");
    println!("|---|---|");
    for (name, value) in [
        ("Usable eyes", show(usable.map(|k| SIZES[k].to_string()))),
        ("Learn eyes", show(learn_eyes.map(|e| e.to_string()))),
        ("Fit limit", format!("{fit_limit:.0}")),
        ("Match score", format!("{match_score:.2}")),
        (
            "Match margin",
            show(match_margin.map(|m| format!("{m:.2}"))),
        ),
        ("Suggest score", format!("{suggest_score:.2}")),
        ("Learn score", format!("{learn_score:.2}")),
        (
            "Learn margin",
            show(learn_margin.map(|m| format!("{m:.2}"))),
        ),
        ("Verdict", verdict.to_string()),
    ] {
        println!("| {name} | {value} |");
    }
    println!();

    turned_faces(&s);
}

/// The sizes the turned faces are measured at, in pixels between the eyes: a near face and the usable size.
const TURN_SIZES: [usize; 2] = [24, 16];
/// The buckets of [`turn`]: each from its value up to the next one's, the last without end.
const TURNS: [f32; 12] = [0.0, 0.1, 0.2, 0.3, 0.4, 0.45, 0.5, 0.55, 0.6, 0.7, 0.8, 1.0];
/// The most a face is turned to be a frontal one, the other face of a pair: the first two buckets.
const FRONTAL_TURN: f32 = 0.2;
/// The limits on [`turn`] looked at for the faces the frontal rule turns away.
const LIMITS: [f32; 8] = [0.4, 0.45, 0.5, 0.55, 0.6, 0.65, 0.7, 0.8];
/// The buckets of [`turn`] the faces turned 0.4 or more are counted in by their fit: each from its value up to the next
/// one's, the last without end.
const FIT_TURNS: [f32; 6] = [0.4, 0.5, 0.55, 0.6, 0.7, 0.8];
/// The buckets of the fit: each over the one before it up to its value, the first from 0, the last without end.
const FITS: [f32; 3] = [7.0, 10.0, 15.0];
/// The People detector's lines at its default match score, and its fit limit.
const MATCH_SCORE: f32 = 0.5;
const SUGGEST_SCORE: f32 = 0.4;
const LEARN_SCORE: f32 = 0.6;
const FIT_LIMIT: f32 = 7.0;
/// The People detector's limits for a face the frontal rule turns away: its turn, and its fit.
const TURN_LIMIT: f32 = 0.7;
const TURNED_FIT_LIMIT: f32 = 10.0;

/// How far a face is turned from the camera: how far its nose tip lies from the midpoint of its eyes along the line
/// between them, in eye distances, as `Vision.Turn` in `nodes/Vision.cs` measures it. 0 for a face looking straight at the
/// camera; 0.5 with the nose in line with one eye; more the further it is turned. A tilt of the head does not change it.
fn turn(l: &[f32; 10]) -> f32 {
    let (dx, dy) = (l[2] - l[0], l[3] - l[1]);
    let squared = dx * dx + dy * dy;
    if squared == 0.0 {
        return f32::INFINITY;
    }
    let (mx, my) = ((l[0] + l[2]) / 2.0, (l[1] + l[3]) / 2.0);
    ((l[4] - mx) * dx + (l[5] - my) * dy).abs() / squared
}

/// The other measure looked at: the nearer eye's distance to the nose tip over the farther one's. 1 for a face looking
/// straight at the camera, less the further it is turned.
fn nose_ratio(l: &[f32; 10]) -> f32 {
    let right = (l[4] - l[0]).hypot(l[5] - l[1]);
    let left = (l[4] - l[2]).hypot(l[5] - l[3]);
    right.min(left) / right.max(left)
}

/// The People detector's rule before turned faces: the eyes at least a fifth of the box's width apart, and the nose tip
/// between them across the picture (`Vision.Frontal`).
fn frontal(face: &Original) -> bool {
    let l = &face.landmarks;
    let (low, high) = (l[0].min(l[2]), l[0].max(l[2]));
    face.eyes >= face.rect[2] / 5.0 && l[4] >= low && l[4] <= high
}

/// The bucket of [`TURNS`] a turn falls in.
fn bucket(turn: f32) -> usize {
    TURNS.iter().rposition(|&from| turn >= from).unwrap_or(0)
}

fn bucket_name(b: usize) -> String {
    match TURNS.get(b + 1) {
        Some(to) => format!("{:.2} to {to:.2}", TURNS[b]),
        None => format!("{:.2} and over", TURNS[b]),
    }
}

/// Spearman's rank correlation of two series of the same length.
fn spearman(a: &[f32], b: &[f32]) -> f64 {
    fn ranks(values: &[f32]) -> Vec<f64> {
        let mut order: Vec<usize> = (0..values.len()).collect();
        order.sort_by(|&i, &j| values[i].total_cmp(&values[j]));
        let mut ranks = vec![0f64; values.len()];
        let mut start = 0;
        while start < order.len() {
            let mut end = start;
            while end + 1 < order.len() && values[order[end + 1]] == values[order[start]] {
                end += 1;
            }
            for &i in &order[start..=end] {
                ranks[i] = (start + end) as f64 / 2.0;
            }
            start = end + 1;
        }
        ranks
    }
    let (ra, rb) = (ranks(a), ranks(b));
    let n = ra.len() as f64;
    let (ma, mb) = (ra.iter().sum::<f64>() / n, rb.iter().sum::<f64>() / n);
    let (mut cov, mut va, mut vb) = (0f64, 0f64, 0f64);
    for (x, y) in ra.iter().zip(&rb) {
        cov += (x - ma) * (y - mb);
        va += (x - ma) * (x - ma);
        vb += (y - mb) * (y - mb);
    }
    cov / (va * vb).sqrt()
}

/// Counts of pairs, and of those at or above the suggest, the match and the learn score.
#[derive(Default, Clone, Copy)]
struct Tally {
    pairs: usize,
    suggest: usize,
    matched: usize,
    learned: usize,
}

impl Tally {
    fn add(&mut self, score: f32) {
        self.pairs += 1;
        self.suggest += (score >= SUGGEST_SCORE) as usize;
        self.matched += (score >= MATCH_SCORE) as usize;
        self.learned += (score >= LEARN_SCORE) as usize;
    }

    fn merge(&mut self, other: &Tally) {
        self.pairs += other.pairs;
        self.suggest += other.suggest;
        self.matched += other.matched;
        self.learned += other.learned;
    }

    /// A share of the pairs, to a tenth of a per cent.
    fn share(&self, n: usize) -> String {
        if self.pairs == 0 {
            "–".into()
        } else {
            percent(n as f64 / self.pairs as f64)
        }
    }

    /// A share of the pairs to four places of a per cent, for the rare ones.
    fn fine(&self, n: usize) -> String {
        if self.pairs == 0 {
            "–".into()
        } else {
            format!("{:.4} %", n as f64 * 100.0 / self.pairs as f64)
        }
    }
}

/// One face at a size, as the turned faces are measured: how far it is turned, whether the frontal rule takes it,
/// whether its eyes are a fifth of its box apart, how well it fits, and its pairs with the frontal pictures of the same
/// person and of everyone else.
struct Turned {
    image: usize,
    turn: f32,
    frontal: bool,
    wide: bool,
    fit: f32,
    same: Tally,
    different: Tally,
    /// The mean score of its pairs with frontal pictures of the same person, when it has one and fits within
    /// [`FIT_LIMIT`].
    mean_same: Option<f32>,
}

impl Turned {
    /// Whether the People detector compares the face, by its rule for frontal faces and for turned ones.
    fn compared(&self) -> bool {
        if self.frontal {
            self.fit <= FIT_LIMIT
        } else {
            self.wide && self.turn <= TURN_LIMIT && self.fit <= TURNED_FIT_LIMIT
        }
    }
}

/// Faces of [`Turned`] together: how many, how many are compared, how many people those are, their pairs, and how many
/// of them score at or above the match score with a frontal picture of someone else.
#[derive(Default)]
struct Group {
    faces: usize,
    compared: usize,
    people: std::collections::BTreeSet<usize>,
    same: Tally,
    different: Tally,
    crossing: usize,
}

impl Group {
    /// Counts a face, and its pairs when it is compared.
    fn add(&mut self, s: &Study, face: &Turned, compared: bool) {
        self.faces += 1;
        if !compared {
            return;
        }
        self.compared += 1;
        self.people.insert(s.person(face.image));
        self.same.merge(&face.same);
        self.different.merge(&face.different);
        self.crossing += (face.different.matched > 0) as usize;
    }

    fn crossing(&self) -> String {
        if self.compared == 0 {
            "–".into()
        } else {
            format!(
                "{} ({})",
                self.crossing,
                percent(self.crossing as f64 / self.compared as f64)
            )
        }
    }
}

/// The name of a bucket of `values`: from its value up to the next one's, the last without end.
fn span_name(values: &[f32], b: usize) -> String {
    match values.get(b + 1) {
        Some(to) => format!("{:.2} to {to:.2}", values[b]),
        None => format!("{:.2} and over", values[b]),
    }
}

/// The bucket of [`FITS`] a fit falls in.
fn fit_bucket(fit: f32) -> usize {
    FITS.iter().position(|&to| fit <= to).unwrap_or(FITS.len())
}

fn fit_bucket_name(b: usize) -> String {
    match b {
        0 => format!("{:.0} or less", FITS[0]),
        _ if b < FITS.len() => format!("over {:.0} to {:.0}", FITS[b - 1], FITS[b]),
        _ => format!("over {:.0}", FITS[FITS.len() - 1]),
    }
}

/// How SFace does with faces turned away from the camera, by how far they are turned ([`turn`]).
fn turned_faces(s: &Study) {
    let records = s.records;
    let faces: Vec<&Original> = records.iter().filter_map(|r| r.face.as_ref()).collect();
    let turns: Vec<f32> = faces.iter().map(|f| turn(&f.landmarks)).collect();
    let rejected: Vec<usize> = (0..faces.len()).filter(|&k| !frontal(faces[k])).collect();
    let narrow = faces.iter().filter(|f| f.eyes < f.rect[2] / 5.0).count();
    let fits = |k: usize| faces[k].fit <= FIT_LIMIT;
    let wide = |k: usize| faces[k].eyes >= faces[k].rect[2] / 5.0;
    println!("## Turned faces");
    println!();
    println!("How far a face is turned is how far its nose tip lies from the midpoint of its eyes, along the line between them, in eye distances: 0 looking straight at the camera, 0.5 with the nose in line with one eye. A frontal face, the other face of each pair below, is one turned less than {FRONTAL_TURN:.1} that fits within {FIT_LIMIT:.0}. The People detector compares a face the frontal rule takes when it fits within {FIT_LIMIT:.0}, and one the rule turns away when its eyes are a fifth of its box apart, it is turned at most {TURN_LIMIT:.2} and it fits within {TURNED_FIT_LIMIT:.0}.");
    println!();
    println!(
        "Of the {} faces found, the frontal rule (eyes a fifth of the box apart, the nose between them across the picture) turns away {} ({}), {} of which fit within {FIT_LIMIT:.0} and {} within {TURNED_FIT_LIMIT:.0}; {narrow} of all faces have their eyes closer than a fifth of the box. Turn of the faces it turns away, by quantile (1 %, 10 %, 50 %, 90 %, 99 %, highest): {}. Turn of all faces, by quantile (50 %, 90 %, 99 %, 99.9 %, highest): {}.",
        faces.len(),
        rejected.len(),
        percent(rejected.len() as f64 / faces.len() as f64),
        rejected.iter().filter(|&&k| fits(k)).count(),
        rejected.iter().filter(|&&k| faces[k].fit <= TURNED_FIT_LIMIT).count(),
        quantiles(rejected.iter().map(|&k| turns[k]).collect(), &[0.01, 0.1, 0.5, 0.9, 0.99, 1.0]).join(", "),
        quantiles(turns.clone(), &[0.5, 0.9, 0.99, 0.999, 1.0]).join(", ")
    );
    println!();
    let tilted = rejected.iter().filter(|&&k| turns[k] < 0.5).count();
    let through: Vec<usize> = (0..faces.len())
        .filter(|&k| turns[k] >= 0.5 && frontal(faces[k]))
        .collect();
    println!(
        "Of the faces it turns away, {tilted} are turned less than 0.5: a tilted head moves the nose across the picture without turning. Of the faces it takes, {} are turned 0.5 or more, {} of which fit within {FIT_LIMIT:.0}.",
        through.len(),
        through.iter().filter(|&&k| fits(k)).count()
    );
    println!();
    let taken: Vec<String> = LIMITS
        .iter()
        .map(|&limit| {
            let within: Vec<usize> = rejected
                .iter()
                .copied()
                .filter(|&k| wide(k) && turns[k] <= limit)
                .collect();
            format!(
                "{limit:.2}, {} ({} fitting within {FIT_LIMIT:.0}, {} within {TURNED_FIT_LIMIT:.0})",
                within.len(),
                within.iter().filter(|&&k| fits(k)).count(),
                within.iter().filter(|&&k| faces[k].fit <= TURNED_FIT_LIMIT).count()
            )
        })
        .collect();
    println!(
        "Of the {} faces it turns away, a limit on the turn takes those with their eyes a fifth of the box apart: at {}.",
        rejected.len(),
        taken.join("; at ")
    );
    println!();
    let worse: Vec<f32> = (0..faces.len())
        .filter(|&k| turns[k] >= FIT_TURNS[0] && !fits(k))
        .map(|k| faces[k].fit)
        .collect();
    println!(
        "The {} faces turned {:.2} or more that do not fit within {FIT_LIMIT:.0} fit, by quantile (50 %, 90 %, highest): {}.",
        worse.len(),
        FIT_TURNS[0],
        quantiles(worse.clone(), &[0.5, 0.9, 1.0]).join(", ")
    );
    println!();
    let further: Vec<f32> = (0..faces.len())
        .filter(|&k| turns[k] >= 0.6 && !fits(k) && faces[k].fit <= TURNED_FIT_LIMIT)
        .map(|k| turns[k])
        .collect();
    println!(
        "The {} faces turned 0.60 or more that fit over {FIT_LIMIT:.0} to {TURNED_FIT_LIMIT:.0} are turned, by quantile (10 %, 50 %, 90 %, highest): {}.",
        further.len(),
        quantiles(further.clone(), &[0.1, 0.5, 0.9, 1.0]).join(", ")
    );
    println!();

    for size in TURN_SIZES {
        let k = SIZES
            .iter()
            .position(|&v| v == size)
            .expect("a size measured");
        let at_size = |i: usize| s.embedding(i, k).is_some();
        // The frontal pictures each face is paired with: each person's drawn ones, at the size and fitting.
        let frontals: Vec<Vec<usize>> = s
            .capped
            .iter()
            .map(|images| {
                images
                    .iter()
                    .copied()
                    .filter(|&i| {
                        let face = records[i].face.as_ref().unwrap();
                        at_size(i) && face.fit <= FIT_LIMIT && turn(&face.landmarks) < FRONTAL_TURN
                    })
                    .collect()
            })
            .collect();
        let all_frontal: Vec<usize> = frontals.concat();
        // Every picture, not only those drawn: turned faces are rare.
        let measured: Vec<Turned> = (0..records.len())
            .filter(|&i| records[i].face.is_some() && at_size(i))
            .map(|i| {
                let face = records[i].face.as_ref().unwrap();
                let mut turned = Turned {
                    image: i,
                    turn: turn(&face.landmarks),
                    frontal: frontal(face),
                    wide: face.eyes >= face.rect[2] / 5.0,
                    fit: face.fit,
                    same: Tally::default(),
                    different: Tally::default(),
                    mean_same: None,
                };
                let (e, person) = (s.embedding(i, k).unwrap(), s.person(i));
                let mut sum = 0f32;
                for &j in frontals[person].iter().filter(|&&j| j != i) {
                    let score = dot(e, s.embedding(j, k).unwrap());
                    turned.same.add(score);
                    sum += score;
                }
                if turned.same.pairs > 0 && turned.fit <= FIT_LIMIT {
                    turned.mean_same = Some(sum / turned.same.pairs as f32);
                }
                for &j in all_frontal.iter().filter(|&&j| s.person(j) != person) {
                    turned.different.add(dot(e, s.embedding(j, k).unwrap()));
                }
                turned
            })
            .collect();

        let mut groups: Vec<Group> = (0..TURNS.len()).map(|_| Group::default()).collect();
        for face in &measured {
            groups[bucket(face.turn)].add(s, face, face.fit <= FIT_LIMIT);
        }
        println!("### Turned faces at {size} px");
        println!();
        println!("Each face of the bucket that fits within {FIT_LIMIT:.0} against every frontal picture of the same person (of their {PER_PERSON} drawn) and of everyone else (of theirs). Same ≥ and Different ≥ are the shares of those pairs at or above the score; Crossing are the faces of the bucket that score at or above {MATCH_SCORE:.2} with at least one frontal picture of someone else, of {} pictures of {} people.", all_frontal.len(), frontals.iter().filter(|f| !f.is_empty()).count());
        println!();
        println!("| Turn | Faces | Fit within {FIT_LIMIT:.0} | People | Same-person pairs | Same ≥ {SUGGEST_SCORE:.2} | Same ≥ {MATCH_SCORE:.2} | Different-person pairs | Different ≥ {SUGGEST_SCORE:.2} | Different ≥ {MATCH_SCORE:.2} | Crossing |");
        println!("|---|---|---|---|---|---|---|---|---|---|---|");
        for (b, g) in groups.iter().enumerate() {
            println!(
                "| {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} |",
                bucket_name(b),
                g.faces,
                g.compared,
                g.people.len(),
                g.same.pairs,
                g.same.share(g.same.suggest),
                g.same.share(g.same.matched),
                g.different.pairs,
                g.different.fine(g.different.suggest),
                g.different.fine(g.different.matched),
                g.crossing()
            );
        }
        println!();

        // Two faces of one bucket: of the same person, and of two people.
        println!("Two faces of the same bucket, both fitting within {FIT_LIMIT:.0}:");
        println!();
        println!("| Turn | Same-person pairs | Same ≥ {MATCH_SCORE:.2} | Different-person pairs | Different ≥ {SUGGEST_SCORE:.2} | Different ≥ {MATCH_SCORE:.2} |");
        println!("|---|---|---|---|---|---|");
        for b in 0..TURNS.len() {
            let inside: Vec<usize> = measured
                .iter()
                .filter(|f| f.fit <= FIT_LIMIT && bucket(f.turn) == b)
                .map(|f| f.image)
                .collect();
            let (mut same, mut different) = (Tally::default(), Tally::default());
            for (n, &i) in inside.iter().enumerate() {
                for &j in &inside[n + 1..] {
                    let score = dot(s.embedding(i, k).unwrap(), s.embedding(j, k).unwrap());
                    if s.person(i) == s.person(j) {
                        same.add(score);
                    } else {
                        different.add(score);
                    }
                }
            }
            println!(
                "| {} | {} | {} | {} | {} | {} |",
                bucket_name(b),
                same.pairs,
                same.share(same.matched),
                different.pairs,
                different.fine(different.suggest),
                different.fine(different.matched)
            );
        }
        println!();

        // The faces turned 0.4 or more by their fit, every one paired whatever its fit.
        println!("The faces turned {:.2} or more by their fit, each paired whatever its fit; Turned away are those of them the frontal rule turns away:", FIT_TURNS[0]);
        println!();
        println!("| Turn | Fit | Faces | Turned away | People | Same-person pairs | Same ≥ {MATCH_SCORE:.2} | Different-person pairs | Different ≥ {SUGGEST_SCORE:.2} | Different ≥ {MATCH_SCORE:.2} |");
        println!("|---|---|---|---|---|---|---|---|---|---|");
        for t in 0..FIT_TURNS.len() {
            for f in 0..=FITS.len() {
                let mut g = Group::default();
                let mut away = 0;
                for face in measured.iter().filter(|face| {
                    FIT_TURNS.iter().rposition(|&from| face.turn >= from) == Some(t)
                        && fit_bucket(face.fit) == f
                }) {
                    g.add(s, face, true);
                    away += !face.frontal as usize;
                }
                println!(
                    "| {} | {} | {} | {away} | {} | {} | {} | {} | {} | {} |",
                    span_name(&FIT_TURNS, t),
                    fit_bucket_name(f),
                    g.faces,
                    g.people.len(),
                    g.same.pairs,
                    g.same.share(g.same.matched),
                    g.different.pairs,
                    g.different.fine(g.different.suggest),
                    g.different.fine(g.different.matched)
                );
            }
        }
        println!();

        // What a limit takes beyond the frontal rule.
        println!("The faces the frontal rule turns away, with their eyes a fifth of the box apart, that a limit on the turn would take, beside those the rule takes; Compared are those that fit within the limit for them ({FIT_LIMIT:.0} for the faces the rule takes, {TURNED_FIT_LIMIT:.0} for the others, unless the row says), whose pairs are counted:");
        println!();
        println!("| Faces | Count | Compared | People | Same-person pairs | Same ≥ {MATCH_SCORE:.2} | Same ≥ {LEARN_SCORE:.2} | Different-person pairs | Different ≥ {SUGGEST_SCORE:.2} | Different ≥ {MATCH_SCORE:.2} | Crossing |");
        println!("|---|---|---|---|---|---|---|---|---|---|---|");
        let row = |name: String, g: &Group| {
            println!(
                "| {name} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} |",
                g.faces,
                g.compared,
                g.people.len(),
                g.same.pairs,
                g.same.share(g.same.matched),
                g.same.share(g.same.learned),
                g.different.pairs,
                g.different.fine(g.different.suggest),
                g.different.fine(g.different.matched),
                g.crossing()
            )
        };
        let group = |pick: &dyn Fn(&Turned) -> bool, compared: &dyn Fn(&Turned) -> bool| {
            let mut g = Group::default();
            for face in measured.iter().filter(|f| pick(f)) {
                g.add(s, face, compared(face));
            }
            g
        };
        let away = |f: &Turned| !f.frontal && f.wide;
        row(
            "Taken by the frontal rule".into(),
            &group(&|f| f.frontal, &|f| f.fit <= FIT_LIMIT),
        );
        for limit in LIMITS {
            row(
                format!("Turned away by it, turned at most {limit:.2}"),
                &group(&|f| away(f) && f.turn <= limit, &|f| {
                    f.fit <= TURNED_FIT_LIMIT
                }),
            );
        }
        for (from, to) in [(0.0, 0.55), (0.55, TURN_LIMIT), (TURN_LIMIT, 0.8)] {
            let name = if from == 0.0 {
                format!("Turned away by it, turned at most {to:.2}")
            } else {
                format!("Turned away by it, turned over {from:.2} to {to:.2}")
            };
            let pick = |f: &Turned| away(f) && f.turn > from && f.turn <= to;
            row(
                format!("{name}, fitting within {FIT_LIMIT:.0}"),
                &group(&pick, &|f| f.fit <= FIT_LIMIT),
            );
            row(
                format!("{name}, fitting over {FIT_LIMIT:.0} to {TURNED_FIT_LIMIT:.0}"),
                &group(&pick, &|f| f.fit > FIT_LIMIT && f.fit <= TURNED_FIT_LIMIT),
            );
        }
        row(
            "Turned away by it, all".into(),
            &group(&away, &|f| f.fit <= TURNED_FIT_LIMIT),
        );
        row(
            "Compared by the People detector".into(),
            &group(&|_| true, &Turned::compared),
        );
        println!();

        let scored: Vec<&Turned> = measured.iter().filter(|f| f.mean_same.is_some()).collect();
        let mean: Vec<f32> = scored.iter().map(|f| f.mean_same.unwrap()).collect();
        let by_turn: Vec<f32> = scored.iter().map(|f| f.turn).collect();
        let by_ratio: Vec<f32> = scored
            .iter()
            .map(|f| nose_ratio(&records[f.image].face.as_ref().unwrap().landmarks))
            .collect();
        println!(
            "Over the {} faces fitting within {FIT_LIMIT:.0} with a frontal picture of the same person, the rank correlation (Spearman's) of each measure with the face's mean score against those pictures: the turn {:.3}, the ratio of the eyes' distances to the nose {:.3}.",
            mean.len(),
            spearman(&by_turn, &mean),
            spearman(&by_ratio, &mean)
        );
        println!();
    }
}

fn main() -> ExitCode {
    match run() {
        Ok(code) => code,
        Err(e) => {
            eprintln!("calibrate: {e}");
            ExitCode::FAILURE
        }
    }
}

fn run() -> Result<ExitCode, String> {
    let mut args = std::env::args_os().skip(1);
    let usage = "usage: calibrate <lfw dir> <model> [--cache FILE] [--check N]";
    let (Some(dir), Some(model)) = (args.next(), args.next()) else {
        return Err(usage.into());
    };
    let (dir, model) = (PathBuf::from(dir), PathBuf::from(model));
    let mut cache = None;
    let mut check = None;
    while let Some(flag) = args.next() {
        let value = args.next().ok_or(usage)?;
        match flag.to_str() {
            Some("--cache") => cache = Some(PathBuf::from(value)),
            Some("--check") => {
                check = Some(
                    value
                        .to_str()
                        .and_then(|v| v.parse::<usize>().ok())
                        .ok_or(usage)?,
                )
            }
            _ => return Err(usage.into()),
        }
    }
    let cache = cache.unwrap_or_else(|| {
        let mut name = dir.file_name().unwrap_or_default().to_os_string();
        name.push(".calibrate-cache");
        dir.with_file_name(name)
    });
    let dataset = list(&dir, &model).map_err(|e| format!("{}: {e}", dir.display()))?;
    if dataset.images.is_empty() {
        return Err(format!("{} holds no pictures", dir.display()));
    }
    let cached = read_cache(&cache, dataset.key, dataset.images.len())
        .map_err(|e| format!("{}: {e}", cache.display()))?;

    if let Some(n) = check {
        let records =
            cached.ok_or_else(|| format!("{} is no cache of these pictures", cache.display()))?;
        let mut which: Vec<usize> = (0..dataset.images.len()).collect();
        Rng::new(FOR_CHECK, 0).shuffle(&mut which);
        which.truncate(n);
        which.sort_unstable();
        let fresh = compute(&dataset, &which, &model)?;
        let (mut a, mut b) = (Vec::new(), Vec::new());
        let differ = which
            .iter()
            .zip(&fresh)
            .filter(|(&i, record)| {
                a.clear();
                b.clear();
                encode(&records[i], &mut a);
                encode(record, &mut b);
                a != b
            })
            .count();
        println!(
            "{} pictures computed again: {} match the cache bit for bit, {differ} differ.",
            which.len(),
            which.len() - differ
        );
        return Ok(if differ == 0 {
            ExitCode::SUCCESS
        } else {
            ExitCode::FAILURE
        });
    }

    let records = match cached {
        Some(records) => {
            eprintln!("read {} pictures from {}", records.len(), cache.display());
            records
        }
        None => {
            let all: Vec<usize> = (0..dataset.images.len()).collect();
            let records = compute(&dataset, &all, &model)?;
            write_cache(&cache, dataset.key, &records)
                .map_err(|e| format!("{}: {e}", cache.display()))?;
            eprintln!("wrote {}", cache.display());
            records
        }
    };
    study(&dataset, &records);
    Ok(ExitCode::SUCCESS)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rgb(width: usize, height: usize, at: impl Fn(usize, usize) -> [u8; 3]) -> Rgb {
        let rgb = (0..width * height)
            .flat_map(|i| at(i % width, i / width))
            .collect();
        Rgb { rgb, width, height }
    }

    #[test]
    fn each_new_pixel_takes_all_of_its_share() {
        for (from, to) in [(250, 89), (250, 250), (7, 3), (100, 1)] {
            for taps in area_taps(from, to) {
                let sum: f64 = taps.iter().map(|t| t.1).sum();
                assert!((sum - 1.0).abs() < 1e-9, "{from} to {to}: {taps:?}");
            }
        }
    }

    #[test]
    fn halving_averages_each_square_of_four() {
        let big = rgb(4, 2, |x, y| [(x * 40 + y * 100) as u8, 7, 255]);
        let small = big.shrink(2, 1);
        // Pixels 0, 40, 100, 140 average to 70; 80, 120, 180, 220 to 150.
        assert_eq!(small.rgb, [70, 7, 255, 150, 7, 255]);
    }

    #[test]
    fn a_third_of_a_pixel_weighs_a_third() {
        // Three pixels into two: the middle one is split between them.
        let big = rgb(3, 1, |x, _| [[0, 90, 180][x]; 3]);
        let small = big.shrink(2, 1);
        assert_eq!(small.rgb, [30, 30, 30, 150, 150, 150]);
    }

    #[test]
    fn shrinking_to_the_same_size_changes_nothing() {
        let big = rgb(5, 3, |x, y| [x as u8 * 50, y as u8 * 80, 3]);
        assert_eq!(big.shrink(5, 3).rgb, big.rgb);
    }

    #[test]
    fn a_framed_picture_sits_in_the_middle() {
        let small = rgb(2, 1, |_, _| [9, 9, 9]);
        let (frame, (left, top)) = small.framed(4, 3);
        assert_eq!((left, top), (1, 1));
        let lit: Vec<usize> = (0..12).filter(|&i| frame.rgb[i * 3] == 9).collect();
        assert_eq!(lit, [5, 6]);
    }

    #[test]
    fn the_line_lets_through_at_most_one_in_n() {
        let scores = Scores::new((0..1000).map(|i| i as f32 / 1000.0).collect());
        let line = scores.line(100);
        // 10 of 1,000 may be at or above it: 0.990 to 0.999, and 0.989 is below it.
        assert!(line > 0.989 && line <= 0.990, "{line}");
        assert_eq!(scores.share(line), 0.01);
        // Ties: with many equal top scores, none of them may pass.
        let tied = Scores::new([vec![0.5; 990], vec![0.9; 10]].concat());
        assert!(tied.line(1000) > 0.9);
        assert_eq!(tied.share(tied.line(1000)), 0.0);
    }

    #[test]
    fn a_gallery_line_counts_every_probe_but_only_the_wrong_ones_against_it() {
        let probe = |genuine, right, best| Probe {
            genuine,
            right,
            best,
            second: best - 0.3,
        };
        let mut probes: Vec<Probe> = (0..997).map(|_| probe(true, true, 0.9)).collect();
        probes.extend([probe(false, false, 0.5), probe(true, false, 0.6)]);
        probes.push(probe(false, false, 0.7));
        // One in 1,000 of the 1,000 probes may be wrong: the line lets only 0.7 through.
        let line = gallery_line(&probes, 0.1, 1000);
        assert!(line > 0.6 && line <= 0.7, "{line}");
        // A margin none of the wrong ones beats leaves no line at all.
        assert_eq!(gallery_line(&probes, 0.35, 1000), f32::NEG_INFINITY);
    }

    #[test]
    fn quantiles_take_the_nearest_rank() {
        let scores = Scores::new((1..=10).map(|i| i as f32).collect());
        assert_eq!(scores.quantile(0.5), 5.0);
        assert_eq!(scores.quantile(0.99), 10.0);
        assert_eq!(scores.quantile(0.01), 1.0);
    }

    #[test]
    fn lines_round_to_twentieths() {
        assert_eq!(up_to_05(0.3612), 0.4);
        assert_eq!(up_to_05(0.35), 0.35);
        assert_eq!(up_to_05(0.3501), 0.4);
        assert_eq!(nearest_05(0.3612), 0.35);
        assert_eq!(nearest_05(0.3762), 0.4);
    }

    #[test]
    fn a_seed_draws_the_same_and_a_shuffle_keeps_everything() {
        let draw = || {
            let mut rng = Rng::new(FOR_GALLERY, 3);
            let mut items: Vec<usize> = (0..50).collect();
            rng.shuffle(&mut items);
            items
        };
        let (a, b) = (draw(), draw());
        assert_eq!(a, b);
        assert_ne!(a, (0..50).collect::<Vec<_>>());
        let mut sorted = a.clone();
        sorted.sort_unstable();
        assert_eq!(sorted, (0..50).collect::<Vec<_>>());
        assert_ne!(
            Rng::new(FOR_GALLERY, 3).next(),
            Rng::new(FOR_GALLERY, 4).next()
        );
    }

    /// Landmarks with the eyes and the nose where given, the mouth corners below the nose.
    fn landmarks(right: (f32, f32), left: (f32, f32), nose: (f32, f32)) -> [f32; 10] {
        [
            right.0,
            right.1,
            left.0,
            left.1,
            nose.0,
            nose.1,
            right.0,
            nose.1 + 20.0,
            left.0,
            nose.1 + 20.0,
        ]
    }

    #[test]
    fn the_turn_is_the_noses_offset_along_the_eyes_in_eye_distances() {
        // Straight on, a quarter of the eye distance aside, in line with an eye, and beyond it.
        assert_eq!(
            turn(&landmarks((30.0, 40.0), (70.0, 40.0), (50.0, 60.0))),
            0.0
        );
        assert_eq!(
            turn(&landmarks((30.0, 40.0), (70.0, 40.0), (60.0, 60.0))),
            0.25
        );
        assert_eq!(
            turn(&landmarks((30.0, 40.0), (70.0, 40.0), (30.0, 60.0))),
            0.5
        );
        assert_eq!(
            turn(&landmarks((30.0, 40.0), (70.0, 40.0), (90.0, 60.0))),
            1.0
        );
        // A head tilted 45° with its nose straight below the eyes' midpoint is not turned; the eyes' order does not matter.
        assert!(turn(&landmarks((30.0, 30.0), (60.0, 60.0), (35.0, 55.0))).abs() < 1e-6);
        assert_eq!(
            turn(&landmarks((70.0, 40.0), (30.0, 40.0), (60.0, 60.0))),
            0.25
        );
        assert_eq!(
            turn(&landmarks((50.0, 40.0), (50.0, 40.0), (50.0, 60.0))),
            f32::INFINITY
        );
    }

    #[test]
    fn the_frontal_rule_is_vision_frontal() {
        let face = |right, left, nose| Original {
            rect: [0.0, 0.0, 100.0, 100.0],
            landmarks: landmarks(right, left, nose),
            eyes: eyes(&landmarks(right, left, nose)),
            fit: 1.0,
        };
        assert!(frontal(&face((30.0, 40.0), (70.0, 40.0), (50.0, 60.0))));
        assert!(frontal(&face((40.0, 40.0), (60.0, 40.0), (50.0, 60.0))));
        assert!(!frontal(&face((41.0, 40.0), (60.0, 40.0), (50.0, 60.0))));
        assert!(!frontal(&face((30.0, 40.0), (70.0, 40.0), (75.0, 60.0))));
        // A tilted head turned little, whose nose is left of both eyes across the picture.
        let tilted = face((30.0, 30.0), (60.0, 60.0), (25.0, 60.0));
        assert!(!frontal(&tilted) && turn(&tilted.landmarks) < 0.1);
    }

    #[test]
    fn a_face_is_compared_as_the_people_detector_compares_it() {
        let face = |frontal, wide, turn, fit| Turned {
            image: 0,
            turn,
            frontal,
            wide,
            fit,
            same: Tally::default(),
            different: Tally::default(),
            mean_same: None,
        };
        // A face the frontal rule takes: fitting within 7, however it is turned.
        assert!(face(true, true, 0.9, 7.0).compared());
        assert!(!face(true, true, 0.1, 7.5).compared());
        // One it turns away: its eyes a fifth of its box apart, turned at most 0.70 and fitting within 10.
        assert!(face(false, true, 0.70, 10.0).compared());
        assert!(face(false, true, 0.65, 9.0).compared());
        assert!(!face(false, true, 0.75, 1.0).compared());
        assert!(!face(false, true, 0.30, 10.5).compared());
        assert!(!face(false, false, 0.30, 1.0).compared());
    }

    #[test]
    fn a_fit_falls_in_the_bucket_up_to_its_value() {
        let names: Vec<String> = [1.0, 7.0, 7.5, 10.0, 12.0, 15.0, 15.5]
            .iter()
            .map(|&fit| fit_bucket_name(fit_bucket(fit)))
            .collect();
        assert_eq!(
            names,
            [
                "7 or less",
                "7 or less",
                "over 7 to 10",
                "over 7 to 10",
                "over 10 to 15",
                "over 10 to 15",
                "over 15"
            ]
        );
        assert_eq!(span_name(&FIT_TURNS, 0), "0.40 to 0.50");
        assert_eq!(span_name(&FIT_TURNS, FIT_TURNS.len() - 1), "0.80 and over");
    }

    #[test]
    fn a_record_reads_back_as_it_was_written() {
        let mut embedding = [0f32; EMBEDDING];
        embedding[5] = 0.25;
        let record = Record {
            face: Some(Original {
                rect: [1.0, 2.0, 3.0, 4.0],
                landmarks: [5.5; 10],
                eyes: 44.0,
                fit: 2.5,
            }),
            sizes: (0..SIZES.len())
                .map(|k| {
                    if k < 3 {
                        Scaled {
                            embedding: Some(Box::new(embedding)),
                            found: [Some(1.5), None],
                        }
                    } else {
                        NOT_AT_SIZE
                    }
                })
                .collect(),
        };
        let none = Record {
            face: None,
            sizes: Vec::new(),
        };
        let mut bytes = Vec::new();
        encode(&record, &mut bytes);
        encode(&none, &mut bytes);
        let mut input = Decoder(&bytes[..]);
        let (again, nothing) = (input.record().unwrap(), input.record().unwrap());
        let (mut a, mut b) = (Vec::new(), Vec::new());
        encode(&record, &mut a);
        encode(&again, &mut b);
        assert_eq!(a, b);
        assert_eq!(again.sizes[0].found, [Some(1.5), None]);
        assert_eq!(again.sizes[0].embedding.as_ref().unwrap()[5], 0.25);
        assert!(again.sizes[3].embedding.is_none());
        assert!(nothing.face.is_none());
    }
}

//! The recogniser: SFace, read from a file outside the library and planned once per process for every recogniser
//! open on that file, turning a face aligned from its landmarks into an embedding of length 1.

use std::collections::BTreeMap;
use std::fs::File;
use std::io::{BufReader, ErrorKind};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Mutex, MutexGuard, PoisonError};

use tract_onnx::prelude::*;

use crate::align::{align, SIDE};
use crate::detector::{Picture, Plan};

/// The model's file name, as `fetch-model.sh` downloads it.
pub const MODEL_FILE: &str = "face_recognition_sface_2021dec.onnx";

/// How many values an embedding has.
pub const EMBEDDING: usize = 128;

/// The plan of each model file a recogniser is open on, by the file's canonical path, with how many recognisers
/// share it.
static PLANS: Mutex<BTreeMap<PathBuf, (Plan, usize)>> = Mutex::new(BTreeMap::new());

/// How many plans have been made, which the tests count.
static PLANNED: AtomicUsize = AtomicUsize::new(0);

fn plans() -> MutexGuard<'static, BTreeMap<PathBuf, (Plan, usize)>> {
    // The map changes only once a plan is made, so a panic under the lock leaves it whole and later calls go on.
    PLANS.lock().unwrap_or_else(PoisonError::into_inner)
}

/// Why the file at `path` could not be opened.
fn unreadable(path: &Path, error: std::io::Error) -> String {
    if error.kind() == ErrorKind::NotFound {
        format!(
            "the face recognition model {} does not exist",
            path.display()
        )
    } else {
        format!(
            "the face recognition model {} could not be read: {error}",
            path.display()
        )
    }
}

/// SFace from the file at `path`, planned for one 1×3×112×112 input.
fn plan(path: &Path) -> Result<Plan, String> {
    let not_the_model = |detail: String| {
        format!(
            "the file {} is not the face recognition model: {detail}",
            path.display()
        )
    };
    let file = File::open(path).map_err(|e| unreadable(path, e))?;
    let plan = tract_onnx::onnx()
        .model_for_read(&mut BufReader::new(file))
        .and_then(|model| model.with_input_fact(0, f32::fact([1, 3, SIDE, SIDE]).into()))
        .and_then(|model| model.into_optimized())
        .and_then(|model| model.into_runnable())
        .map_err(|e| not_the_model(format!("{e:#}")))?;
    let output = plan
        .model()
        .output_fact(0)
        .map_err(|e| not_the_model(format!("{e:#}")))?;
    if output.datum_type != f32::datum_type() || output.shape.as_concrete() != Some(&[1, EMBEDDING])
    {
        return Err(not_the_model(format!(
            "it gives {output:?}, not 1×{EMBEDDING} floats"
        )));
    }
    PLANNED.fetch_add(1, Ordering::SeqCst);
    Ok(plan)
}

/// SFace open on one model file. Every recogniser open on the same file shares one plan, made by the first and
/// freed with the last; embedding runs on it from any thread at once, without a lock.
pub struct Recognizer {
    /// The model file's canonical path, which its plan is kept under.
    key: PathBuf,
    plan: Plan,
}

// The ABI lets any thread call a recogniser.
const _: fn() = || {
    fn shared<T: Send + Sync>() {}
    shared::<Recognizer>();
};

impl Recognizer {
    /// A recogniser on the model file at `path`, which is planned unless another recogniser has it open already: an
    /// error when the file is missing, cannot be read, or is not SFace.
    pub fn open(path: impl AsRef<Path>) -> Result<Recognizer, String> {
        let path = path.as_ref();
        let key = std::fs::canonicalize(path).map_err(|e| unreadable(path, e))?;
        let mut plans = plans();
        if let Some((plan, users)) = plans.get_mut(&key) {
            *users += 1;
            let plan = plan.clone();
            return Ok(Recognizer { key, plan });
        }
        // Planned under the lock, so that recognisers opened together on one file plan it once.
        let plan = plan(path)?;
        plans.insert(key.clone(), (plan.clone(), 1));
        Ok(Recognizer { key, plan })
    }

    /// The embedding of the face whose five landmarks (each x, y in the picture's pixels, in the detector's order)
    /// are given, of length 1, and the alignment's fit ([`crate::align::fit`]).
    pub fn embed(
        &self,
        picture: &Picture,
        landmarks: &[f32; 10],
    ) -> Result<([f32; EMBEDDING], f32), String> {
        if !landmarks.iter().all(|v| v.is_finite()) {
            return Err("the landmarks are not all finite".into());
        }
        let (crop, fit) = align(picture, landmarks);
        // R, G and B planes of 0–255: the model normalises its input itself.
        let plane = SIDE * SIDE;
        let mut input = vec![0f32; 3 * plane];
        for (i, px) in crop.as_chunks::<3>().0.iter().enumerate() {
            for (c, &v) in px.iter().enumerate() {
                input[c * plane + i] = v as f32;
            }
        }
        let input = Tensor::from_shape(&[1, 3, SIDE, SIDE], &input)
            .expect("the crop fills a 1×3×112×112 tensor");
        let outs = self
            .plan
            .run(tvec!(input.into()))
            .map_err(|e| format!("the face recognition model failed: {e:#}"))?;
        let values = outs
            .first()
            .ok_or("the face recognition model gave no output")?
            .try_as_plain_ram()
            .and_then(|plain| plain.as_slice::<f32>())
            .map_err(|e| format!("the face recognition model gave an unreadable output: {e:#}"))?;
        let values: [f32; EMBEDDING] = values.try_into().map_err(|_| {
            format!(
                "the face recognition model gave {} values, not {EMBEDDING}",
                values.len()
            )
        })?;
        let length = values
            .iter()
            .map(|&v| v as f64 * v as f64)
            .sum::<f64>()
            .sqrt();
        if !(length.is_finite() && length > 0.0) {
            return Err("the face recognition model gave an embedding of length 0".into());
        }
        Ok((values.map(|v| (v as f64 / length) as f32), fit))
    }
}

impl Drop for Recognizer {
    fn drop(&mut self) {
        let mut plans = plans();
        if let Some((_, users)) = plans.get_mut(&self.key) {
            *users -= 1;
            if *users == 0 {
                plans.remove(&self.key);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Detector;

    /// The tests that open the model take turns, so that the plans each counts are its own.
    static SERIAL: Mutex<()> = Mutex::new(());

    fn serial() -> MutexGuard<'static, ()> {
        SERIAL.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn model() -> PathBuf {
        let path = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("model")
            .join(MODEL_FILE);
        assert!(
            path.is_file(),
            "{} is missing: run native/fetch-model.sh",
            path.display()
        );
        path
    }

    fn planned() -> usize {
        PLANNED.load(Ordering::SeqCst)
    }

    /// How many recognisers share the plan for `path`, or None when none is kept.
    fn users(path: &Path) -> Option<usize> {
        let key = std::fs::canonicalize(path).unwrap();
        plans().get(&key).map(|(_, users)| *users)
    }

    struct Rgb {
        rgb: Vec<u8>,
        width: usize,
        height: usize,
    }

    impl Rgb {
        fn group() -> Rgb {
            let path = format!("{}/tests/fixtures/group.jpg", env!("CARGO_MANIFEST_DIR"));
            let img = image::open(path).unwrap().to_rgb8();
            let (width, height) = (img.width() as usize, img.height() as usize);
            Rgb {
                rgb: img.into_raw(),
                width,
                height,
            }
        }

        fn picture(&self) -> Picture<'_> {
            Picture {
                rgb: &self.rgb,
                width: self.width,
                height: self.height,
                stride: self.width * 3,
            }
        }
    }

    /// The landmarks of the largest face the detector finds in the group photo at 640 with `min_score` 0.7: the
    /// man second from the right, his box about 67×88 at (480, 78).
    fn largest_face(picture: &Picture) -> [f32; 10] {
        let faces = Detector::open().unwrap().faces(picture, 640, 0.7).unwrap();
        let face = faces
            .iter()
            .max_by(|a, b| (a.w * a.h).total_cmp(&(b.w * b.h)))
            .unwrap();
        assert!(
            (face.x - 480.0).abs() < 5.0 && (face.y - 78.0).abs() < 5.0,
            "{face:?}"
        );
        face.landmarks
    }

    #[test]
    fn the_embedding_is_a_unit_vector_and_pinned() {
        let _serial = serial();
        let group = Rgb::group();
        let picture = group.picture();
        let landmarks = largest_face(&picture);
        let rec = Recognizer::open(model()).unwrap();
        let (embedding, fit) = rec.embed(&picture, &landmarks).unwrap();
        let length = embedding.iter().map(|v| v * v).sum::<f32>().sqrt();
        assert!((length - 1.0).abs() < 1e-5, "length {length}");
        assert!(fit > 0.0 && fit < 10.0, "fit {fit}");
        // What this build gives on macOS arm64.
        let pinned = [-0.009479362f32, -0.08494233, -0.0237523, 0.07168374];
        for (got, want) in embedding.iter().zip(pinned) {
            assert!(
                (got - want).abs() < 1e-4,
                "the first values are {:?}, not {pinned:?}",
                &embedding[..4]
            );
        }
    }

    #[test]
    fn one_model_file_is_planned_once() {
        let _serial = serial();
        assert_eq!(users(&model()), None);
        let before = planned();
        let a = Recognizer::open(model()).unwrap();
        // Another spelling of the same file shares its plan too.
        let other = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("model/../model")
            .join(MODEL_FILE);
        let b = Recognizer::open(&other).unwrap();
        assert_eq!(planned() - before, 1);
        assert!(Arc::ptr_eq(&a.plan, &b.plan));
        assert_eq!(users(&model()), Some(2));
    }

    #[test]
    fn closing_the_last_frees_and_reopening_works() {
        let _serial = serial();
        let group = Rgb::group();
        let picture = group.picture();
        let landmarks = largest_face(&picture);
        let a = Recognizer::open(model()).unwrap();
        let b = Recognizer::open(model()).unwrap();
        let first = a.embed(&picture, &landmarks).unwrap();
        let plan = Arc::downgrade(&a.plan);
        drop(a);
        assert_eq!(users(&model()), Some(1));
        assert!(plan.upgrade().is_some());
        drop(b);
        assert_eq!(users(&model()), None);
        assert!(
            plan.upgrade().is_none(),
            "the plan outlived its recognisers"
        );
        let before = planned();
        let c = Recognizer::open(model()).unwrap();
        assert_eq!(planned() - before, 1);
        assert_eq!(c.embed(&picture, &landmarks).unwrap(), first);
    }

    #[test]
    fn a_missing_model_is_an_error() {
        let path = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("model")
            .join("no_such_model.onnx");
        assert_eq!(
            Recognizer::open(&path).err(),
            Some(format!(
                "the face recognition model {} does not exist",
                path.display()
            ))
        );
    }

    #[test]
    fn a_file_that_is_not_the_model_is_an_error() {
        let _serial = serial();
        let dir = Path::new(env!("CARGO_MANIFEST_DIR"));
        // A file that is no ONNX model, and the face detection model, which is one but not this one.
        for path in [
            dir.join("Cargo.toml"),
            dir.join("model/face_detection_yunet_2023mar.onnx"),
        ] {
            let error = Recognizer::open(&path).err().unwrap();
            let start = format!(
                "the file {} is not the face recognition model",
                path.display()
            );
            assert!(error.starts_with(&start), "{error}");
            assert_eq!(users(&path), None);
        }
    }

    #[test]
    fn embedding_from_four_threads_gives_one_answer() {
        let _serial = serial();
        let group = Rgb::group();
        let picture = group.picture();
        let landmarks = largest_face(&picture);
        let (a, b) = (
            Recognizer::open(model()).unwrap(),
            Recognizer::open(model()).unwrap(),
        );
        let want = a.embed(&picture, &landmarks).unwrap();
        std::thread::scope(|s| {
            for t in 0..4 {
                let rec = if t % 2 == 0 { &a } else { &b };
                let (picture, landmarks, want) = (&picture, &landmarks, &want);
                s.spawn(move || {
                    for _ in 0..5 {
                        assert_eq!(&rec.embed(picture, landmarks).unwrap(), want);
                    }
                });
            }
        });
    }
}

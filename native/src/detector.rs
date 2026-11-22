//! The detector: its models, their plans per input shape, and the picture turned into a network input.

use std::collections::VecDeque;
use std::io::Cursor;
use std::sync::{Mutex, MutexGuard, PoisonError};

use tract_onnx::prelude::*;

use crate::nms::best;
use crate::{yunet, Face};

/// The plans kept per model, one per padded input shape; the oldest is dropped for a new one.
pub const PLANS_KEPT: usize = 4;

/// YuNet's boxes overlapping by more than this are one face.
pub const FACE_IOU: f32 = 0.3;

static YUNET: &[u8] = include_bytes!("../model/face_detection_yunet_2023mar.onnx");

/// A picture as the caller lends it: RGB8 rows `stride` bytes apart.
pub struct Picture<'a> {
    pub rgb: &'a [u8],
    pub width: usize,
    pub height: usize,
    pub stride: usize,
}

/// Where a picture goes in the network input: resized to `dw` × `dh` at the top-left of a `pw` × `ph` input
/// (multiples of 32), about `scale` input pixels to one picture pixel. Results map back by [`Letterbox::back`],
/// each axis by its own ratio, because rounding `dw` and `dh` can make the axes differ (8192×8 at 64 is 64×1).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Letterbox {
    pub dw: usize,
    pub dh: usize,
    pub pw: usize,
    pub ph: usize,
    pub scale: f32,
}

impl Letterbox {
    /// The picture pixels per input pixel along x and along y, for the `width` × `height` picture it was made for.
    pub fn back(&self, width: usize, height: usize) -> (f32, f32) {
        (
            width as f32 / self.dw as f32,
            height as f32 / self.dh as f32,
        )
    }
}

/// A face found in the network input, in the picture's pixels by [`Letterbox::back`]'s factors.
pub fn to_picture(mut face: Face, (bx, by): (f32, f32)) -> Face {
    face.x *= bx;
    face.y *= by;
    face.w *= bx;
    face.h *= by;
    for k in face.landmarks.chunks_mut(2) {
        k[0] *= bx;
        k[1] *= by;
    }
    face
}

/// The multiple of 32 at or above `v`, and at least 32.
pub fn pad32(v: usize) -> usize {
    v.max(1).div_ceil(32) * 32
}

/// The letterbox that makes a `width` × `height` picture's long side `input_size`, keeping its aspect.
pub fn letterbox(width: usize, height: usize, input_size: usize) -> Letterbox {
    let scale = input_size as f64 / width.max(height) as f64;
    let side = |v: usize| ((v as f64 * scale).round() as usize).clamp(1, input_size);
    let (dw, dh) = (side(width), side(height));
    Letterbox {
        dw,
        dh,
        pw: pad32(dw),
        ph: pad32(dh),
        scale: scale as f32,
    }
}

/// The source pixels, with their weights, that each of `to` target pixels takes from `from` source pixels: the
/// mean of those it covers when shrinking, and bilinear between pixel centres when enlarging.
fn taps(from: usize, to: usize) -> Vec<Vec<(usize, f32)>> {
    (0..to)
        .map(|t| {
            if to <= from {
                let start = t * from / to;
                let end = ((t + 1) * from / to).clamp(start + 1, from);
                let weight = 1.0 / (end - start) as f32;
                (start..end).map(|s| (s, weight)).collect()
            } else {
                let at = ((t as f64 + 0.5) * from as f64 / to as f64 - 0.5)
                    .clamp(0.0, (from - 1) as f64);
                let below = at.floor() as usize;
                let frac = (at - below as f64) as f32;
                if frac == 0.0 || below + 1 >= from {
                    vec![(below, 1.0)]
                } else {
                    vec![(below, 1.0 - frac), (below + 1, frac)]
                }
            }
        })
        .collect()
}

/// The 1×3×`ph`×`pw` BGR float input (0–255): the picture resized into the letterbox's top-left `dw` × `dh` and
/// every other value `pad`.
pub fn fill(
    rgb: &[u8],
    width: usize,
    height: usize,
    stride: usize,
    lb: &Letterbox,
    pad: f32,
) -> Tensor {
    let plane = lb.pw * lb.ph;
    // Written in place, so the input is not copied once more into a tensor.
    let mut tensor =
        Tensor::zero::<f32>(&[1, 3, lb.ph, lb.pw]).expect("an f32 tensor of the input's shape");
    let mut view = tensor
        .try_as_plain_ram_mut()
        .expect("a new tensor is in plain memory");
    let data = view.as_slice_mut::<f32>().expect("the tensor holds f32");
    data.fill(pad);
    let (xs, ys) = (taps(width, lb.dw), taps(height, lb.dh));
    for (ty, row_taps) in ys.iter().enumerate() {
        for (tx, col_taps) in xs.iter().enumerate() {
            let mut px = [0f32; 3];
            for &(sy, wy) in row_taps {
                let row = &rgb[sy * stride..];
                for &(sx, wx) in col_taps {
                    let w = wy * wx;
                    let p = &row[sx * 3..sx * 3 + 3];
                    px[0] += w * p[0] as f32;
                    px[1] += w * p[1] as f32;
                    px[2] += w * p[2] as f32;
                }
            }
            let o = ty * lb.pw + tx;
            data[o] = px[2];
            data[plane + o] = px[1];
            data[2 * plane + o] = px[0];
        }
    }
    tensor
}

pub type Plan = Arc<TypedRunnableModel>;

/// A model parsed once, and its optimised plans for the input shapes used last.
pub struct Model {
    parsed: InferenceModel,
    plans: VecDeque<((usize, usize), Plan)>,
}

impl Model {
    /// Parses an ONNX model for 1×3×H×W input with its outputs chosen by name.
    pub fn parse(bytes: &[u8], outputs: &[&str]) -> TractResult<Model> {
        let mut parsed = tract_onnx::onnx()
            // The file's value_info and output shapes are for one input size: drop them, so that the input fact
            // each plan sets drives shape inference.
            .with_ignore_output_shapes(true)
            .with_ignore_value_info(true)
            .model_for_read(&mut Cursor::new(bytes))?;
        parsed.select_outputs_by_name(outputs)?;
        Ok(Model {
            parsed,
            plans: VecDeque::new(),
        })
    }

    /// The plan for a `pw` × `ph` input, made and kept the first time that shape is asked for.
    pub fn plan(&mut self, pw: usize, ph: usize) -> TractResult<Plan> {
        if let Some((_, plan)) = self.plans.iter().find(|(shape, _)| *shape == (pw, ph)) {
            return Ok(plan.clone());
        }
        let plan = self
            .parsed
            .clone()
            .with_input_fact(0, f32::fact([1, 3, ph, pw]).into())?
            .into_optimized()?
            .into_runnable()?;
        if self.plans.len() >= PLANS_KEPT {
            self.plans.pop_front();
        }
        self.plans.push_back(((pw, ph), plan.clone()));
        Ok(plan)
    }

    pub fn plans(&self) -> usize {
        self.plans.len()
    }
}

struct State {
    faces: Model,
}

/// The models behind one handle. Their plans and runs are behind one lock, so concurrent calls run them one at a
/// time; each call prepares its input and decodes its outputs outside it.
pub struct Detector {
    state: Mutex<State>,
}

// The ABI lets any thread call a detector.
const _: fn() = || {
    fn shared<T: Send + Sync>() {}
    shared::<Detector>();
};

impl Detector {
    /// Parses YuNet.
    pub fn open() -> Result<Detector, String> {
        let faces = Model::parse(YUNET, &yunet::OUTPUTS)
            .map_err(|e| format!("the face model could not load: {e:#}"))?;
        Ok(Detector {
            state: Mutex::new(State { faces }),
        })
    }

    fn state(&self) -> MutexGuard<'_, State> {
        // A panic under the lock is reported as an error; the state is still whole, so later calls go on.
        self.state.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// The faces in a picture scoring at least `min_score`, the best first, in the picture's pixels.
    pub fn faces(
        &self,
        picture: &Picture,
        input_size: usize,
        min_score: f32,
    ) -> Result<Vec<Face>, String> {
        let lb = letterbox(picture.width, picture.height, input_size);
        let input = fill(
            picture.rgb,
            picture.width,
            picture.height,
            picture.stride,
            &lb,
            0.0,
        );
        let mut state = self.state();
        let plan = state.faces.plan(lb.pw, lb.ph).map_err(|e| {
            format!(
                "the face model could not be planned for {}×{}: {e:#}",
                lb.pw, lb.ph
            )
        })?;
        let outs = plan
            .run(tvec!(input.into()))
            .map_err(|e| format!("the face model failed: {e:#}"))?;
        drop(state);
        let mut views: [&[f32]; 12] = [&[]; 12];
        for (i, view) in views.iter_mut().enumerate() {
            let name = yunet::OUTPUTS[i];
            let values = outs
                .get(i)
                .ok_or_else(|| format!("the face model gave no {name}"))?
                .try_as_plain_ram()
                .and_then(|plain| plain.as_slice::<f32>())
                .map_err(|e| format!("the face model gave an unreadable {name}: {e:#}"))?;
            let want = yunet::output_len(i, lb.pw, lb.ph);
            if values.len() != want {
                return Err(format!(
                    "the face model gave {} values for {name}, not {want}",
                    values.len()
                ));
            }
            *view = values;
        }
        let back = lb.back(picture.width, picture.height);
        let faces = best(yunet::decode(&views, lb.pw, lb.ph, min_score), FACE_IOU);
        Ok(faces.into_iter().map(|f| to_picture(f, back)).collect())
    }

    /// How many face plans are kept.
    pub fn face_plans(&self) -> usize {
        self.state().faces.plans()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn results_map_back_per_axis() {
        // 8192×8 at 64: the width shrinks 128 times but the height, rounded up to one row, only 8 times.
        let lb = letterbox(8192, 8, 64);
        assert_eq!((lb.dw, lb.dh), (64, 1));
        let back = lb.back(8192, 8);
        assert_eq!(back, (128.0, 8.0));
        let mut landmarks = [0.0; 10];
        landmarks.copy_from_slice(&[8.0, 0.5, 56.0, 0.5, 32.0, 0.5, 16.0, 1.0, 48.0, 1.0]);
        let in_input = Face {
            x: 0.0,
            y: 0.0,
            w: 64.0,
            h: 1.0,
            score: 0.9,
            landmarks,
        };
        let f = to_picture(in_input, back);
        assert_eq!([f.x, f.y, f.w, f.h, f.score], [0.0, 0.0, 8192.0, 8.0, 0.9]);
        assert!(f
            .landmarks
            .chunks(2)
            .all(|k| (0.0..=8192.0).contains(&k[0]) && (0.0..=8.0).contains(&k[1])));
        assert_eq!(&f.landmarks[..4], &[1024.0, 4.0, 7168.0, 4.0]);
        // Elsewhere each axis maps back by its own rounded side: 1080×720 at 320 is 320×213.
        let lb = letterbox(1080, 720, 320);
        let (bx, by) = lb.back(1080, 720);
        assert!((bx - 1.0 / lb.scale).abs() < 1e-4 && (by - 720.0 / 213.0).abs() < 1e-4);
    }

    #[test]
    fn letterbox_keeps_the_aspect() {
        for ((w, h, size), want) in [
            ((1080, 720, 320), (320, 213, 320, 224)),
            ((720, 1080, 320), (213, 320, 224, 320)),
            ((100, 50, 640), (640, 320, 640, 320)),
            ((1, 1, 64), (64, 64, 64, 64)),
        ] {
            let lb = letterbox(w, h, size);
            assert_eq!((lb.dw, lb.dh, lb.pw, lb.ph), want, "{w}×{h} at {size}");
            assert!((lb.scale - size as f32 / w.max(h) as f32).abs() < 1e-6);
        }
    }

    #[test]
    fn fill_pads_with_the_given_value() {
        // A 2×2 red picture in a 64×64 input: the picture at the top-left, the rest padding.
        let red = [255, 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0];
        let lb = Letterbox {
            dw: 2,
            dh: 2,
            pw: 64,
            ph: 64,
            scale: 1.0,
        };
        let t = fill(&red, 2, 2, 6, &lb, 114.0);
        assert_eq!(t.shape(), &[1, 3, 64, 64]);
        let v = t.to_plain_array_view::<f32>().unwrap();
        assert_eq!(
            (v[[0, 0, 0, 0]], v[[0, 1, 0, 0]], v[[0, 2, 0, 0]]),
            (0.0, 0.0, 255.0)
        );
        assert_eq!(v[[0, 2, 1, 1]], 255.0);
        for c in 0..3 {
            assert_eq!(v[[0, c, 63, 63]], 114.0);
            assert_eq!(v[[0, c, 0, 2]], 114.0);
            assert_eq!(v[[0, c, 2, 0]], 114.0);
        }
    }

    #[test]
    fn fill_enlarges_a_tiny_picture_over_its_letterbox() {
        // A 1×2 red picture at input 64 is enlarged to 32×64, which is already a multiple of 32.
        let red = [255, 0, 0, 255, 0, 0];
        let lb = letterbox(1, 2, 64);
        assert_eq!((lb.dw, lb.dh, lb.pw, lb.ph), (32, 64, 32, 64));
        let t = fill(&red, 1, 2, 3, &lb, 114.0);
        let v = t.to_plain_array_view::<f32>().unwrap();
        for (y, x) in [(0, 0), (63, 31), (31, 16)] {
            assert_eq!(
                (v[[0, 0, y, x]], v[[0, 1, y, x]], v[[0, 2, y, x]]),
                (0.0, 0.0, 255.0),
                "({x}, {y})"
            );
        }
    }

    #[test]
    fn fill_shrinks_by_the_mean_and_enlarges_bilinearly() {
        // 4×1 grey ramp 0, 100, 200, 40 shrunk to 2×1 gives the means 50 and 120.
        let row: Vec<u8> = [0u8, 100, 200, 40]
            .iter()
            .flat_map(|&g| [g, g, g])
            .collect();
        let lb = Letterbox {
            dw: 2,
            dh: 1,
            pw: 32,
            ph: 32,
            scale: 0.5,
        };
        let v = fill(&row, 4, 1, 12, &lb, 0.0);
        let v = v.to_plain_array_view::<f32>().unwrap();
        assert_eq!((v[[0, 0, 0, 0]], v[[0, 0, 0, 1]]), (50.0, 120.0));
        // 2×1, 0 and 200, enlarged to 4×1: half-pixel centres give 0, 50, 150, 200.
        let row = [0u8, 0, 0, 200, 200, 200];
        let lb = Letterbox {
            dw: 4,
            dh: 1,
            pw: 32,
            ph: 32,
            scale: 2.0,
        };
        let v = fill(&row, 2, 1, 6, &lb, 0.0);
        let v = v.to_plain_array_view::<f32>().unwrap();
        let got: Vec<f32> = (0..4).map(|x| v[[0, 1, 0, x]]).collect();
        assert_eq!(got, vec![0.0, 50.0, 150.0, 200.0]);
    }
}

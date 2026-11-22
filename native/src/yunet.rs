//! YuNet (`face_detection_yunet_2023mar.onnx`): its outputs and their decoding.
//!
//! Ported from OpenCV's `FaceDetectorYNImpl::postProcess` (modules/objdetect/src/face_detect.cpp): every anchor at
//! strides 8, 16 and 32 scores sqrt(clamp(cls) · clamp(obj)), and its box and landmarks are offsets from its cell.

use crate::Face;

/// The model's outputs, in the order [`decode`] takes them.
pub const OUTPUTS: [&str; 12] = [
    "cls_8", "cls_16", "cls_32", "obj_8", "obj_16", "obj_32", "bbox_8", "bbox_16", "bbox_32",
    "kps_8", "kps_16", "kps_32",
];

/// The values each anchor has in the cls, obj, bbox and kps outputs.
pub const PER_ANCHOR: [usize; 4] = [1, 1, 4, 10];

pub const STRIDES: [usize; 3] = [8, 16, 32];

/// How many values output `i` of [`OUTPUTS`] has for a `pw` × `ph` input.
pub fn output_len(i: usize, pw: usize, ph: usize) -> usize {
    let s = STRIDES[i % 3];
    (pw / s) * (ph / s) * PER_ANCHOR[i / 3]
}

/// The faces of every anchor scoring at least `min_score`, unsorted and before NMS, in the `pw` × `ph` input's
/// pixels. Each output must be [`output_len`] long.
pub fn decode(outs: &[&[f32]; 12], pw: usize, ph: usize, min_score: f32) -> Vec<Face> {
    let mut faces = Vec::new();
    for (i, &s) in STRIDES.iter().enumerate() {
        let (cols, rows) = (pw / s, ph / s);
        let (cls, obj, bbox, kps) = (outs[i], outs[i + 3], outs[i + 6], outs[i + 9]);
        let sf = s as f32;
        for r in 0..rows {
            for c in 0..cols {
                let a = r * cols + c;
                let score = (cls[a].clamp(0.0, 1.0) * obj[a].clamp(0.0, 1.0)).sqrt();
                // A NaN output gives a NaN score, which is dropped too.
                if score.is_nan() || score < min_score {
                    continue;
                }
                let b = &bbox[a * 4..a * 4 + 4];
                let cx = (c as f32 + b[0]) * sf;
                let cy = (r as f32 + b[1]) * sf;
                let w = b[2].exp() * sf;
                let h = b[3].exp() * sf;
                let k = &kps[a * 10..a * 10 + 10];
                let mut landmarks = [0f32; 10];
                for n in 0..5 {
                    landmarks[2 * n] = (k[2 * n] + c as f32) * sf;
                    landmarks[2 * n + 1] = (k[2 * n + 1] + r as f32) * sf;
                }
                faces.push(Face {
                    x: cx - w / 2.0,
                    y: cy - h / 2.0,
                    w,
                    h,
                    score,
                    landmarks,
                });
            }
        }
    }
    faces
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A 64×64 padded input: 8×8, 4×4 and 2×2 anchors at strides 8, 16 and 32.
    fn outputs() -> Vec<Vec<f32>> {
        let mut outs = Vec::new();
        for per_anchor in [1, 1, 4, 10] {
            for cells in [64, 16, 4] {
                outs.push(vec![0.0; cells * per_anchor]);
            }
        }
        // Stride 8, row 2, col 3.
        let i = 2 * 8 + 3;
        outs[0][i] = 0.81;
        outs[3][i] = 1.0;
        outs[6][i * 4..i * 4 + 4].copy_from_slice(&[0.5, 0.5, 2f32.ln(), 3f32.ln()]);
        outs[9][i * 10..i * 10 + 10].fill(0.25);
        outs
    }

    fn slices(outs: &[Vec<f32>]) -> [&[f32]; 12] {
        std::array::from_fn(|i| outs[i].as_slice())
    }

    #[test]
    fn decodes_one_anchor() {
        let outs = outputs();
        let faces = decode(&slices(&outs), 64, 64, 0.5);
        assert_eq!(faces.len(), 1);
        let f = faces[0];
        assert!((f.score - 0.9).abs() <= 1e-6, "score {}", f.score);
        for (got, want) in [f.x, f.y, f.w, f.h]
            .into_iter()
            .zip([20.0, 8.0, 16.0, 24.0])
        {
            assert!((got - want).abs() <= 1e-4, "box {:?}", [f.x, f.y, f.w, f.h]);
        }
        assert!((f.landmarks[0] - 26.0).abs() <= 1e-4 && (f.landmarks[1] - 18.0).abs() <= 1e-4);
        assert!(decode(&slices(&outs), 64, 64, 0.95).is_empty());
    }
}

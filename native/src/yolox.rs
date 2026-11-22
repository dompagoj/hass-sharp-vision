//! YOLOX-nano (`yolox_nano.onnx`, YOLOX release 0.1.1rc0): its output and its decoding, COCO class 0 only.
//!
//! As YOLOX's `demo_postprocess` (yolox/utils/demo_utils.py): every anchor at strides 8, 16 and 32, each grid row
//! by row, is an offset from its cell's corner and a log size in strides; it scores objectness · class score.

use crate::nms::{best, Boxed};
use crate::Person;

/// The model's one output, `[1, N, 85]`.
pub const OUTPUT: &str = "output";

/// The values each anchor has: dx, dy, log w, log h, objectness and 80 class scores, the scores already sigmoided.
pub const PER_ANCHOR: usize = 85;

pub const STRIDES: [usize; 3] = [8, 16, 32];

/// People's boxes overlapping by more than this are one person.
pub const PERSON_IOU: f32 = 0.45;

/// How many values the output has for a `pw` × `ph` input.
pub fn output_len(pw: usize, ph: usize) -> usize {
    STRIDES.iter().map(|s| (pw / s) * (ph / s)).sum::<usize>() * PER_ANCHOR
}

/// The people of every anchor scoring at least `min_score`, unsorted and before NMS, in the `pw` × `ph` input's
/// pixels. `out` must be [`output_len`] long.
pub fn decode(out: &[f32], pw: usize, ph: usize, min_score: f32) -> Vec<Person> {
    let mut people = Vec::new();
    let mut anchors = out.as_chunks::<PER_ANCHOR>().0.iter();
    for &s in &STRIDES {
        let sf = s as f32;
        for gy in 0..ph / s {
            for gx in 0..pw / s {
                let Some(a) = anchors.next() else {
                    return people;
                };
                // Person is class 0, whichever class scores best.
                let score = a[4].clamp(0.0, 1.0) * a[5].clamp(0.0, 1.0);
                // A NaN output gives a NaN score, which is dropped too.
                if score.is_nan() || score < min_score {
                    continue;
                }
                let cx = (a[0] + gx as f32) * sf;
                let cy = (a[1] + gy as f32) * sf;
                let w = a[2].exp() * sf;
                let h = a[3].exp() * sf;
                let person = Person {
                    x: cx - w / 2.0,
                    y: cy - h / 2.0,
                    w,
                    h,
                    score,
                };
                // A box that is not finite cannot be compared in NMS or drawn, so it is no one.
                if person.rect().iter().all(|v| v.is_finite()) {
                    people.push(person);
                }
            }
        }
    }
    people
}

/// The people in the model's output for a `pw` × `ph` input, the best first, in the input's pixels; an error when
/// the output is not [`output_len`] long.
pub fn people(out: &[f32], pw: usize, ph: usize, min_score: f32) -> Result<Vec<Person>, String> {
    let want = output_len(pw, ph);
    if out.len() != want {
        return Err(format!(
            "the person model gave {} values, not {want}",
            out.len()
        ));
    }
    Ok(best(decode(out, pw, ph, min_score), PERSON_IOU))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A 64×64 padded input: 8×8, 4×4 and 2×2 anchors at strides 8, 16 and 32, 84 in all.
    fn output() -> Vec<f32> {
        vec![0.0; 84 * PER_ANCHOR]
    }

    fn set(out: &mut [f32], anchor: usize, values: &[(usize, f32)]) {
        for &(k, v) in values {
            out[anchor * PER_ANCHOR + k] = v;
        }
    }

    /// Sets an anchor to dx 0.5, dy 0.5, lw ln 2, lh ln 3, `obj` and cls[0] 1.0.
    fn put(out: &mut [f32], anchor: usize, obj: f32) {
        let values = [0.5, 0.5, 2f32.ln(), 3f32.ln(), obj, 1.0];
        out[anchor * PER_ANCHOR..anchor * PER_ANCHOR + 6].copy_from_slice(&values);
    }

    /// The output with one anchor put, with obj 0.9.
    fn one(anchor: usize) -> Vec<f32> {
        let mut out = output();
        put(&mut out, anchor, 0.9);
        out
    }

    fn assert_box(p: &Person, want: [f32; 4]) {
        for (got, want) in [p.x, p.y, p.w, p.h].into_iter().zip(want) {
            assert!((got - want).abs() <= 1e-4, "box {:?}", [p.x, p.y, p.w, p.h]);
        }
    }

    #[test]
    fn a_64_input_has_84_anchors() {
        assert_eq!(output_len(64, 64), 84 * 85);
        assert_eq!(output_len(416, 416), 3549 * 85);
        assert_eq!(output_len(416, 256), 2184 * 85);
    }

    #[test]
    fn decodes_one_anchor() {
        // Stride 8, anchor 19: gx 3, gy 2.
        let people = decode(&one(19), 64, 64, 0.45);
        assert_eq!(people.len(), 1);
        let p = people[0];
        assert!((p.score - 0.9).abs() <= 1e-6, "score {}", p.score);
        assert_box(&p, [20.0, 8.0, 16.0, 24.0]);
    }

    #[test]
    fn the_grid_has_no_half_offset() {
        // Stride 16, its anchor 0, after stride 8's 64.
        let people = decode(&one(64), 64, 64, 0.45);
        assert_eq!(people.len(), 1);
        let p = people[0];
        assert!((p.x + p.w / 2.0 - 8.0).abs() <= 1e-4, "{p:?}");
        assert!((p.y + p.h / 2.0 - 8.0).abs() <= 1e-4, "{p:?}");
        assert_box(&p, [-8.0, -16.0, 32.0, 48.0]);
    }

    #[test]
    fn the_last_stride_follows_the_others() {
        // Stride 32, its last anchor (gx 1, gy 1), the 84th.
        let people = decode(&one(83), 64, 64, 0.45);
        assert_eq!(people.len(), 1);
        // Centred at (1.5 · 32, 1.5 · 32), 2 · 32 wide and 3 · 32 tall.
        assert_box(&people[0], [16.0, 0.0, 64.0, 96.0]);
    }

    #[test]
    fn scores_person_only() {
        // Its best class is 5 (a bus), so it scores obj · cls[0] = 0.1.
        let mut out = output();
        set(&mut out, 19, &[(4, 1.0), (5, 0.1), (10, 1.0)]);
        assert!(decode(&out, 64, 64, 0.45).is_empty());
        assert_eq!(decode(&out, 64, 64, 0.05).len(), 1);
    }

    #[test]
    fn min_score_filters() {
        assert!(decode(&one(19), 64, 64, 0.95).is_empty());
        assert_eq!(decode(&one(19), 64, 64, 0.9).len(), 1);
    }

    #[test]
    fn a_nan_scores_nothing() {
        let mut out = one(19);
        set(&mut out, 19, &[(4, f32::NAN)]);
        assert!(decode(&out, 64, 64, 0.01).is_empty());
    }

    #[test]
    fn a_box_that_is_not_finite_is_no_one() {
        // Its width overflows to infinity.
        let mut out = one(19);
        set(&mut out, 20, &[(2, 200.0), (4, 1.0), (5, 1.0)]);
        let people = people(&out, 64, 64, 0.45).unwrap();
        assert_eq!(people.len(), 1);
        assert!((people[0].score - 0.9).abs() <= 1e-6);
    }

    #[test]
    fn a_wrong_length_is_refused() {
        let out = output();
        assert_eq!(
            people(&out[..84 * 85 - 1], 64, 64, 0.45),
            Err("the person model gave 7139 values, not 7140".to_string())
        );
        assert_eq!(people(&one(19), 64, 64, 0.45).map(|p| p.len()), Ok(1));
    }

    #[test]
    fn overlapping_people_are_one() {
        // Anchors 19 and 20 (gx 4) give boxes 8 px apart: IoU 8·24 / (2·16·24 − 8·24) = 1/3, kept; with lw ln 4
        // they are 32 wide and overlap by 24 of 40: IoU 0.6, one.
        let mut out = one(19);
        put(&mut out, 20, 0.8);
        assert_eq!(people(&out, 64, 64, 0.45).unwrap().len(), 2);
        set(&mut out, 19, &[(2, 4f32.ln())]);
        set(&mut out, 20, &[(2, 4f32.ln())]);
        let kept = people(&out, 64, 64, 0.45).unwrap();
        assert_eq!(kept.len(), 1);
        assert!((kept[0].score - 0.9).abs() <= 1e-6);
    }
}

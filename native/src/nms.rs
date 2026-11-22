//! Greedy non-maximum suppression, as OpenCV's `cv::dnn::NMSBoxes` with `eta = 1`.

/// Something found with a box and a score.
pub trait Boxed {
    /// The box: x, y, w, h.
    fn rect(&self) -> [f32; 4];
    fn score(&self) -> f32;
}

/// Intersection over union of two x, y, w, h boxes; 0 when they have no area.
pub fn intersection_over_union(a: [f32; 4], b: [f32; 4]) -> f32 {
    let x1 = a[0].max(b[0]);
    let y1 = a[1].max(b[1]);
    let x2 = (a[0] + a[2]).min(b[0] + b[2]);
    let y2 = (a[1] + a[3]).min(b[1] + b[3]);
    let inter = (x2 - x1).max(0.0) * (y2 - y1).max(0.0);
    let union = a[2] * a[3] + b[2] * b[3] - inter;
    if union > 0.0 {
        inter / union
    } else {
        0.0
    }
}

/// Keeps the best of each group of boxes that overlap by more than `iou`, the best first.
pub fn nms<T: Boxed>(mut found: Vec<T>, iou: f32) -> Vec<T> {
    // Stable, so equal scores keep the order they were found in.
    found.sort_by(|a, b| b.score().total_cmp(&a.score()));
    let mut kept: Vec<T> = Vec::new();
    for f in found {
        let r = f.rect();
        if kept
            .iter()
            .all(|k| intersection_over_union(k.rect(), r) <= iou)
        {
            kept.push(f);
        }
    }
    kept
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Clone, Copy, Debug, PartialEq)]
    struct B([f32; 4], f32);

    impl Boxed for B {
        fn rect(&self) -> [f32; 4] {
            self.0
        }
        fn score(&self) -> f32 {
            self.1
        }
    }

    // Their intersection is 50 and their union 100: IoU 0.5.
    const WIDE: [f32; 4] = [0.0, 0.0, 10.0, 10.0];
    const HALF: [f32; 4] = [0.0, 0.0, 10.0, 5.0];

    #[test]
    fn nms_keeps_the_better_of_two_overlapping() {
        let kept = nms(vec![B(WIDE, 0.6), B(HALF, 0.8)], 0.3);
        assert_eq!(kept, vec![B(HALF, 0.8)]);
    }

    #[test]
    fn nms_keeps_both_at_or_below_the_threshold() {
        assert_eq!(
            nms(vec![B(WIDE, 0.6), B(HALF, 0.8)], 0.5),
            vec![B(HALF, 0.8), B(WIDE, 0.6)]
        );
        assert_eq!(
            nms(vec![B(WIDE, 0.6), B(HALF, 0.8)], 0.6),
            vec![B(HALF, 0.8), B(WIDE, 0.6)]
        );
        let apart = [20.0, 20.0, 5.0, 5.0];
        assert_eq!(
            nms(vec![B(WIDE, 0.9), B(apart, 0.7)], 0.0),
            vec![B(WIDE, 0.9), B(apart, 0.7)]
        );
    }
}

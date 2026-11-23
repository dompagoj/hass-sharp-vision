//! A face aligned to SFace's 112×112 input from its five landmarks, as OpenCV's `FaceRecognizerSF::alignCrop` aligns
//! it: a similarity onto five fixed points, then a bilinear warp of the full picture.

use crate::detector::Picture;

/// The side of the aligned crop.
pub const SIDE: usize = 112;

/// Where `alignCrop` puts the five landmarks in the crop, in the order the detector gives them: the right eye, the
/// left eye, the nose tip, the right mouth corner and the left mouth corner (the face's right is the picture's left).
pub const REFERENCE: [[f64; 2]; 5] = [
    [38.2946, 51.6963],
    [73.5318, 51.5014],
    [56.0252, 71.7366],
    [41.5493, 92.3655],
    [70.7299, 92.2041],
];

/// The mean of [`REFERENCE`], to four places as `alignCrop` has it.
const REFERENCE_MEAN: [f64; 2] = [56.0262, 71.9008];

/// The similarity `[a, −b, tx, b, a, ty]` (a 2×3 matrix, by rows) that takes the landmarks closest to
/// [`REFERENCE`] by least squares: a turn and one scale for both axes, never a reflection, and a shift. In two
/// dimensions this closed form is Umeyama's estimate, which `alignCrop` reaches through an SVD. Landmarks that all lie
/// on one point give the matrix that takes every point to the reference's mean.
pub fn similarity(landmarks: &[f32; 10]) -> [f64; 6] {
    let points: [[f64; 2]; 5] =
        std::array::from_fn(|i| [landmarks[2 * i] as f64, landmarks[2 * i + 1] as f64]);
    // alignCrop takes the landmarks' mean in f32.
    let mean = |axis: usize| points.iter().map(|p| p[axis] as f32).sum::<f32>() as f64 / 5.0;
    let (mx, my) = (mean(0), mean(1));
    let (mut p, mut q, mut spread) = (0.0, 0.0, 0.0);
    for (point, to) in points.iter().zip(REFERENCE) {
        let (sx, sy) = (point[0] - mx, point[1] - my);
        let (dx, dy) = (to[0] - REFERENCE_MEAN[0], to[1] - REFERENCE_MEAN[1]);
        p += dx * sx + dy * sy;
        q += dy * sx - dx * sy;
        spread += sx * sx + sy * sy;
    }
    let (a, b) = if spread > 0.0 {
        (p / spread, q / spread)
    } else {
        (0.0, 0.0)
    };
    let tx = REFERENCE_MEAN[0] - (a * mx - b * my);
    let ty = REFERENCE_MEAN[1] - (b * mx + a * my);
    [a, -b, tx, b, a, ty]
}

/// How far `m` puts the landmarks from [`REFERENCE`]: the root mean square of the five distances, in crop pixels.
pub fn fit(m: &[f64; 6], landmarks: &[f32; 10]) -> f64 {
    let sum: f64 = REFERENCE
        .iter()
        .enumerate()
        .map(|(i, to)| {
            let (x, y) = (landmarks[2 * i] as f64, landmarks[2 * i + 1] as f64);
            let u = m[0] * x + m[1] * y + m[2];
            let v = m[3] * x + m[4] * y + m[5];
            (u - to[0]).powi(2) + (v - to[1]).powi(2)
        })
        .sum();
    (sum / 5.0).sqrt()
}

/// The 112×112 RGB crop that `m` takes the picture to, as OpenCV's `warpAffine` with `INTER_LINEAR` and a black
/// border makes it: each crop pixel is the picture bilinearly sampled at the point `m` takes there, pixels outside
/// the picture black, rounded to the nearest grey level. A matrix that takes the picture to a point gives a black crop.
pub fn warp(picture: &Picture, m: &[f64; 6]) -> Vec<u8> {
    let mut crop = vec![0u8; SIDE * SIDE * 3];
    let det = m[0] * m[4] - m[1] * m[3];
    if !(det.is_finite() && det != 0.0) {
        return crop;
    }
    let (ia, ib, ic, id) = (m[4] / det, -m[1] / det, -m[3] / det, m[0] / det);
    let (itx, ity) = (-(ia * m[2] + ib * m[5]), -(ic * m[2] + id * m[5]));
    let (width, height) = (picture.width as i64, picture.height as i64);
    let at = |x: i64, y: i64| -> Option<&[u8]> {
        (x >= 0 && y >= 0 && x < width && y < height)
            .then(|| &picture.rgb[y as usize * picture.stride + x as usize * 3..][..3])
    };
    for y in 0..SIDE {
        for x in 0..SIDE {
            let sx = ia * x as f64 + ib * y as f64 + itx;
            let sy = ic * x as f64 + id * y as f64 + ity;
            let (x0, y0) = (sx.floor(), sy.floor());
            let (fx, fy) = (sx - x0, sy - y0);
            let (x0, y0) = (x0 as i64, y0 as i64);
            let mut px = [0f64; 3];
            for (dx, dy, w) in [
                (0, 0, (1.0 - fx) * (1.0 - fy)),
                (1, 0, fx * (1.0 - fy)),
                (0, 1, (1.0 - fx) * fy),
                (1, 1, fx * fy),
            ] {
                if let Some(p) = at(x0.saturating_add(dx), y0.saturating_add(dy)) {
                    for c in 0..3 {
                        px[c] += w * p[c] as f64;
                    }
                }
            }
            let o = (y * SIDE + x) * 3;
            for c in 0..3 {
                crop[o + c] = px[c].round().clamp(0.0, 255.0) as u8;
            }
        }
    }
    crop
}

/// The face whose five landmarks (each x, y in the picture's pixels, in [`REFERENCE`]'s order) are given, aligned to
/// a 112×112 RGB crop, and how far the landmarks are from the reference once aligned ([`fit`]): a few crop pixels for
/// a face seen from the front, more the further it is turned away.
pub fn align(picture: &Picture, landmarks: &[f32; 10]) -> (Vec<u8>, f32) {
    let m = similarity(landmarks);
    (warp(picture, &m), fit(&m, landmarks) as f32)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn picture(rgb: &[u8], width: usize, height: usize) -> Picture<'_> {
        Picture {
            rgb,
            width,
            height,
            stride: width * 3,
        }
    }

    fn reference() -> [f32; 10] {
        std::array::from_fn(|i| REFERENCE[i / 2][i % 2] as f32)
    }

    #[test]
    fn the_reference_points_map_to_themselves() {
        let (w, h) = (SIDE, SIDE);
        let rgb: Vec<u8> = (0..w * h)
            .flat_map(|i| {
                let (x, y) = (i % w, i / w);
                [(2 * x) as u8, (2 * y) as u8, (x + y) as u8]
            })
            .collect();
        let (crop, fit) = align(&picture(&rgb, w, h), &reference());
        assert!(fit < 0.01, "fit {fit}");
        assert_eq!(crop.len(), SIDE * SIDE * 3);
        let worst = crop
            .iter()
            .zip(&rgb)
            .map(|(a, b)| (*a as i32 - *b as i32).abs())
            .max()
            .unwrap();
        assert!(worst <= 1, "a pixel moved by {worst} grey levels");
    }

    #[test]
    fn a_scaled_rotated_face_aligns() {
        // The crop drawn into a 500×400 picture twice as large, turned by 20° and moved by (150, 50): each picture
        // pixel holds (2u, 2v, 100) for the crop point (u, v) it shows, so the aligned crop is (2x, 2y, 100) again.
        let (s, (sin, cos), (tx, ty)) = (2.0f64, 20f64.to_radians().sin_cos(), (150.0, 50.0));
        let to_picture =
            |u: f64, v: f64| (s * (cos * u - sin * v) + tx, s * (sin * u + cos * v) + ty);
        let to_crop = |x: f64, y: f64| {
            let (x, y) = ((x - tx) / s, (y - ty) / s);
            (cos * x + sin * y, -sin * x + cos * y)
        };
        let (w, h) = (500, 400);
        let rgb: Vec<u8> = (0..w * h)
            .flat_map(|i| {
                let (u, v) = to_crop((i % w) as f64, (i / w) as f64);
                let grey = |g: f64| (2.0 * g).round().clamp(0.0, 255.0) as u8;
                [grey(u), grey(v), 100]
            })
            .collect();
        let landmarks: [f32; 10] = std::array::from_fn(|i| {
            let (x, y) = to_picture(REFERENCE[i / 2][0], REFERENCE[i / 2][1]);
            (if i % 2 == 0 { x } else { y }) as f32
        });
        let (crop, fit) = align(&picture(&rgb, w, h), &landmarks);
        assert!(fit < 0.01, "fit {fit}");
        for y in 1..SIDE - 1 {
            for x in 1..SIDE - 1 {
                let got = &crop[(y * SIDE + x) * 3..][..3];
                let want = [2 * x, 2 * y, 100].map(|g| g as i32);
                for c in 0..3 {
                    assert!(
                        (got[c] as i32 - want[c]).abs() <= 2,
                        "({x}, {y}): {got:?}, not {want:?}"
                    );
                }
            }
        }
    }

    #[test]
    fn a_profile_fits_badly() {
        // A face turned to the side: both eyes 1.6 px apart, the nose beside them and a narrow mouth.
        let landmarks = [60.0, 50.0, 61.6, 50.0, 44.0, 68.0, 56.0, 86.0, 58.0, 86.0];
        let rgb = vec![128; 112 * 112 * 3];
        let (_, fit) = align(&picture(&rgb, 112, 112), &landmarks);
        assert!(fit > 10.0, "fit {fit}");
    }

    #[test]
    fn outside_the_picture_is_black() {
        // The face centred on the white picture's top-left corner: only the crop's bottom-right shows the picture.
        let rgb = vec![255; 112 * 112 * 3];
        let landmarks: [f32; 10] = std::array::from_fn(|i| {
            REFERENCE[i / 2][i % 2] as f32 - if i % 2 == 0 { 56.0 } else { 72.0 }
        });
        let (crop, fit) = align(&picture(&rgb, 112, 112), &landmarks);
        assert!(fit < 0.01, "fit {fit}");
        assert_eq!(&crop[..3], &[0, 0, 0]);
        assert_eq!(&crop[crop.len() - 3..], &[255, 255, 255]);
    }

    #[test]
    fn landmarks_on_one_point_give_a_black_crop_and_a_bad_fit() {
        let rgb = vec![255; 64 * 64 * 3];
        let (crop, fit) = align(&picture(&rgb, 64, 64), &[30.0; 10]);
        assert!(crop.iter().all(|&v| v == 0));
        assert!(fit > 10.0, "fit {fit}");
    }
}

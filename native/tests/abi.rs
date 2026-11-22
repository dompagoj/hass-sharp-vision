//! The C ABI, driven as a caller would drive it.

use std::ffi::CStr;
use std::ptr;

use hs_vision::*;

const NO_FACE: Face = Face {
    x: 0.0,
    y: 0.0,
    w: 0.0,
    h: 0.0,
    score: 0.0,
    landmarks: [0.0; 10],
};

/// The faces found in the group photo at 640 and at 320, with `min_score` 0.7: all eight men at 640, and all but
/// the one half hidden behind the raised hands at 320. The C# tests (Task 9) assert the same counts.
const N640: usize = 8;
const N320: usize = 7;

struct Picture {
    rgb: Vec<u8>,
    width: usize,
    height: usize,
    stride: usize,
}

impl Picture {
    fn group() -> Picture {
        let bytes = std::fs::read(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/tests/fixtures/group.jpg"
        ))
        .unwrap();
        let img = image::load_from_memory(&bytes).unwrap().to_rgb8();
        let (width, height) = (img.width() as usize, img.height() as usize);
        Picture {
            rgb: img.into_raw(),
            width,
            height,
            stride: width * 3,
        }
    }

    fn black(width: usize, height: usize) -> Picture {
        Picture {
            rgb: vec![0; width * height * 3],
            width,
            height,
            stride: width * 3,
        }
    }

    /// The same picture with each row `extra` bytes apart from the next, the gap filled with noise.
    fn padded(&self, extra: usize) -> Picture {
        let stride = self.width * 3 + extra;
        let mut rgb = vec![0xA5; stride * self.height];
        for y in 0..self.height {
            let row = &self.rgb[y * self.stride..y * self.stride + self.width * 3];
            rgb[y * stride..y * stride + self.width * 3].copy_from_slice(row);
        }
        Picture {
            rgb,
            width: self.width,
            height: self.height,
            stride,
        }
    }
}

struct Open(*mut Detector);

impl Open {
    fn new() -> Open {
        let det = hs_vision_open();
        assert!(!det.is_null(), "open failed: {:?}", last_error());
        Open(det)
    }

    fn faces(
        &self,
        p: &Picture,
        input_size: i32,
        min_score: f32,
        max_faces: usize,
    ) -> Result<Vec<Face>, String> {
        faces_on(self.0, p, input_size, min_score, max_faces)
    }
}

impl Drop for Open {
    fn drop(&mut self) {
        unsafe { hs_vision_close(self.0) }
    }
}

fn faces_on(
    det: *mut Detector,
    p: &Picture,
    input_size: i32,
    min_score: f32,
    max_faces: usize,
) -> Result<Vec<Face>, String> {
    let mut out = vec![NO_FACE; max_faces];
    let n = unsafe {
        hs_vision_faces(
            det,
            p.rgb.as_ptr(),
            p.width as i32,
            p.height as i32,
            p.stride as i32,
            input_size,
            min_score,
            out.as_mut_ptr(),
            max_faces as i32,
        )
    };
    if n < 0 {
        return Err(last_error().expect("−1 without an error"));
    }
    assert_eq!(last_error(), None, "a call that succeeded left an error");
    assert!(n as usize <= max_faces);
    out.truncate(n as usize);
    Ok(out)
}

fn last_error() -> Option<String> {
    let p = hs_vision_last_error();
    if p.is_null() {
        None
    } else {
        Some(unsafe { CStr::from_ptr(p) }.to_str().unwrap().to_owned())
    }
}

#[test]
fn abi_version_is_1() {
    assert_eq!(hs_vision_abi_version(), 1);
}

#[test]
fn face_is_60_bytes() {
    assert_eq!(std::mem::size_of::<Face>(), 60);
    assert_eq!(std::mem::align_of::<Face>(), 4);
}

#[test]
fn close_null_is_a_no_op() {
    let p = Picture::black(8, 8);
    assert_eq!(
        faces_on(ptr::null_mut(), &p, 64, 0.5, 4),
        Err("no detector".to_string())
    );
    unsafe { hs_vision_close(ptr::null_mut()) };
    assert_eq!(last_error(), None);
}

#[test]
fn refusals_set_the_error_and_a_valid_call_clears_it() {
    let det = Open::new();
    let p = Picture::black(64, 48);
    let mut out = [NO_FACE; 4];
    let call = |det: *mut Detector,
                rgb: *const u8,
                w: i32,
                h: i32,
                stride: i32,
                size: i32,
                min: f32,
                out: *mut Face,
                max: i32| {
        let n = unsafe { hs_vision_faces(det, rgb, w, h, stride, size, min, out, max) };
        (n, last_error())
    };
    let rgb = p.rgb.as_ptr();
    let o = out.as_mut_ptr();
    let refused = |n_err: (i32, Option<String>), text: &str| {
        assert_eq!(n_err, (-1, Some(text.to_owned())));
        // A valid call afterwards clears the error.
        assert_eq!(call(det.0, rgb, 64, 48, 192, 64, 0.5, o, 4), (0, None));
    };
    refused(
        call(ptr::null_mut(), rgb, 64, 48, 192, 64, 0.5, o, 4),
        "no detector",
    );
    refused(
        call(det.0, ptr::null(), 64, 48, 192, 64, 0.5, o, 4),
        "no pixels",
    );
    for (w, h) in [(0, 48), (64, 0), (-1, 48), (8193, 48), (64, 8193)] {
        refused(
            call(det.0, rgb, w, h, 192, 64, 0.5, o, 4),
            "the picture is 1 to 8192 pixels a side",
        );
    }
    for stride in [191, 0, -192] {
        refused(
            call(det.0, rgb, 64, 48, stride, 64, 0.5, o, 4),
            "the stride is less than a row",
        );
    }
    for size in [0, 32, 63, 65, 100, 1281, 1312, -64] {
        refused(
            call(det.0, rgb, 64, 48, 192, size, 0.5, o, 4),
            "the input size is a multiple of 32 from 64 to 1280",
        );
    }
    for min in [f32::NAN, -0.01, 1.01, f32::INFINITY] {
        refused(
            call(det.0, rgb, 64, 48, 192, 64, min, o, 4),
            "the minimum score is from 0 to 1",
        );
    }
    refused(
        call(det.0, rgb, 64, 48, 192, 64, 0.5, o, -1),
        "no room for faces",
    );
    refused(
        call(det.0, rgb, 64, 48, 192, 64, 0.5, ptr::null_mut(), 1),
        "no room for faces",
    );
    // No room and no faces asked for is fine, as are the ends of every range.
    assert_eq!(
        call(det.0, rgb, 64, 48, 192, 64, 0.5, ptr::null_mut(), 0),
        (0, None)
    );
    assert_eq!(call(det.0, rgb, 64, 48, 192, 1280, 0.0, o, 4).1, None);
    assert_eq!(call(det.0, rgb, 64, 48, 192, 64, 1.0, o, 4), (0, None));
}

#[test]
fn finds_faces_in_the_group_photo() {
    let det = Open::new();
    let p = Picture::group();
    assert_eq!((p.width, p.height), (640, 426));
    for (size, at_least, pinned) in [(640, 7, N640), (320, 6, N320)] {
        let faces = det.faces(&p, size, 0.7, 64).unwrap();
        eprintln!("{size}: {} faces", faces.len());
        assert!(faces.len() >= at_least, "{} faces at {size}", faces.len());
        assert_eq!(faces.len(), pinned, "faces at {size}");
        let (w, h) = (p.width as f32, p.height as f32);
        for f in &faces {
            assert!(f.score >= 0.7 && f.score <= 1.0, "{f:?}");
            assert!(f.w > 0.0 && f.h > 0.0, "{f:?}");
            assert!(f.x >= -0.05 * w && f.y >= -0.05 * h, "{f:?}");
            assert!(f.x + f.w <= 1.05 * w && f.y + f.h <= 1.05 * h, "{f:?}");
            // The landmarks lie in the box grown by 20 %, a tenth of its size on each side.
            for k in f.landmarks.chunks(2) {
                assert!(k[0] >= f.x - 0.1 * f.w && k[0] <= f.x + 1.1 * f.w, "{f:?}");
                assert!(k[1] >= f.y - 0.1 * f.h && k[1] <= f.y + 1.1 * f.h, "{f:?}");
            }
        }
        assert!(
            faces.windows(2).all(|a| a[0].score >= a[1].score),
            "not sorted at {size}"
        );
    }
}

#[test]
fn plans_are_cached_per_shape() {
    let det = Open::new();
    let p = Picture::group();
    let first = det.faces(&p, 640, 0.7, 64).unwrap();
    let small = det.faces(&p, 320, 0.7, 64).unwrap();
    let again = det.faces(&p, 640, 0.7, 64).unwrap();
    assert!(!first.is_empty() && !small.is_empty());
    assert_eq!(first, again);
    assert_eq!(unsafe { &*det.0 }.face_plans(), 2);
}

#[test]
fn at_most_four_plans_are_kept() {
    let det = Open::new();
    let p = Picture::black(64, 64);
    for size in [64, 96, 128, 160, 192, 64] {
        assert_eq!(det.faces(&p, size, 0.5, 4).unwrap(), vec![]);
        assert!(unsafe { &*det.0 }.face_plans() <= 4);
    }
    assert_eq!(unsafe { &*det.0 }.face_plans(), 4);
}

#[test]
fn stride_padding_changes_nothing() {
    let det = Open::new();
    let p = Picture::group();
    let padded = p.padded(16);
    assert_eq!(padded.stride, p.width * 3 + 16);
    for size in [640, 320] {
        assert_eq!(
            det.faces(&p, size, 0.7, 64).unwrap(),
            det.faces(&padded, size, 0.7, 64).unwrap()
        );
    }
}

#[test]
fn a_picture_lent_without_slack_after_its_last_row_reads() {
    let det = Open::new();
    let p = Picture::group();
    let padded = p.padded(16);
    // Exactly (h − 1) · stride + w · 3 bytes: the last row has no gap after it.
    let exact = padded.stride * (padded.height - 1) + padded.width * 3;
    let lent = Picture {
        rgb: padded.rgb[..exact].to_vec(),
        ..padded
    };
    assert_eq!(lent.rgb.len(), exact);
    assert_eq!(lent.rgb.capacity(), exact);
    assert_eq!(
        det.faces(&lent, 640, 0.7, 64).unwrap(),
        det.faces(&p, 640, 0.7, 64).unwrap()
    );
}

#[test]
fn every_anchor_at_min_score_0_stays_bounded() {
    // At 1280 and min_score 0 every one of the 33,600 anchors is a candidate: the 5000 best go to NMS.
    let det = Open::new();
    let p = Picture::group();
    let faces = det.faces(&p, 1280, 0.0, 64).unwrap();
    assert!(!faces.is_empty() && faces.len() <= 64);
    assert!(faces.windows(2).all(|a| a[0].score >= a[1].score));
    assert_eq!(det.faces(&p, 1280, 0.0, 0).unwrap(), vec![]);
}

#[test]
fn max_faces_caps_and_keeps_the_best() {
    let det = Open::new();
    let p = Picture::group();
    let all = det.faces(&p, 640, 0.7, 64).unwrap();
    let two = det.faces(&p, 640, 0.7, 2).unwrap();
    assert_eq!(two, all[..2]);
}

#[test]
fn blank_finds_nothing() {
    let det = Open::new();
    let p = Picture::black(640, 480);
    assert_eq!(det.faces(&p, 640, 0.7, 64).unwrap(), vec![]);
}

#[test]
fn tiny_and_extreme_sizes_find_nothing() {
    let det = Open::new();
    for (w, h) in [(1, 1), (20, 20), (8192, 8), (8, 8192)] {
        let p = Picture::black(w, h);
        for size in [64, 320, 640] {
            assert_eq!(
                det.faces(&p, size, 0.7, 64),
                Ok(vec![]),
                "{w}×{h} at {size}"
            );
        }
    }
}

#[test]
fn concurrent_calls_agree() {
    let det = Open::new();
    let p = Picture::group();
    let expected = det.faces(&p, 320, 0.7, 64).unwrap();
    assert!(!expected.is_empty());
    let shared = det.0 as usize;
    std::thread::scope(|s| {
        for _ in 0..4 {
            s.spawn(|| {
                for _ in 0..10 {
                    assert_eq!(
                        faces_on(shared as *mut Detector, &p, 320, 0.7, 64).unwrap(),
                        expected
                    );
                }
            });
        }
    });
}

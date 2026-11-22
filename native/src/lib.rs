//! hs_vision: face detection with YuNet and person detection with YOLOX-nano on tract, behind a small C ABI.
//!
//! ABI version 1. Every export catches panics, which come back as an error, and records why a call failed in a
//! thread-local error that `hs_vision_last_error` reads.

use std::any::Any;
use std::cell::RefCell;
use std::ffi::{c_char, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;

pub mod detector;
pub mod nms;
pub mod yolox;
pub mod yunet;

pub use detector::Detector;
use detector::Picture;

/// The version of the C ABI, which the caller checks before using anything else.
pub const ABI_VERSION: u32 = 1;

/// The longest side a picture can have.
pub const MAX_SIDE: i32 = 8192;

/// A face, in the input picture's pixels: its box, its score (0–1) and five landmarks, each (x, y): the right eye,
/// the left eye, the nose tip, the right mouth corner and the left mouth corner.
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Face {
    pub x: f32,
    pub y: f32,
    pub w: f32,
    pub h: f32,
    pub score: f32,
    pub landmarks: [f32; 10],
}

impl nms::Boxed for Face {
    fn rect(&self) -> [f32; 4] {
        [self.x, self.y, self.w, self.h]
    }

    fn score(&self) -> f32 {
        self.score
    }
}

/// A person, in the input picture's pixels: their box and its score (0–1).
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Person {
    pub x: f32,
    pub y: f32,
    pub w: f32,
    pub h: f32,
    pub score: f32,
}

impl nms::Boxed for Person {
    fn rect(&self) -> [f32; 4] {
        [self.x, self.y, self.w, self.h]
    }

    fn score(&self) -> f32 {
        self.score
    }
}

thread_local! {
    static LAST_ERROR: RefCell<Option<CString>> = const { RefCell::new(None) };
}

fn set_last_error(message: Option<String>) {
    let message = message.map(|m| CString::new(m.replace('\0', " ")).expect("NULs are replaced"));
    // Ignored only while the thread is being torn down.
    let _ = LAST_ERROR.try_with(|e| *e.borrow_mut() = message);
}

pub(crate) fn panic_message(panic: &(dyn Any + Send)) -> &str {
    if let Some(s) = panic.downcast_ref::<&str>() {
        s
    } else if let Some(s) = panic.downcast_ref::<String>() {
        s
    } else {
        "a panic without a message"
    }
}

/// Runs an export: clears the thread's error, then gives `f`'s value, or `failed` with the error set when `f` fails
/// or panics.
fn export<T>(failed: T, f: impl FnOnce() -> Result<T, String>) -> T {
    set_last_error(None);
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(value)) => value,
        Ok(Err(message)) => {
            set_last_error(Some(message));
            failed
        }
        Err(panic) => {
            set_last_error(Some(format!("internal error: {}", panic_message(&*panic))));
            failed
        }
    }
}

/// Checks a picture's arguments, in the order the refusals are documented, and borrows its pixels.
///
/// # Safety
/// A non-null `rgb` must point to `height` rows `stride` bytes apart, each with `width` RGB8 pixels, that stay
/// valid and unchanged for `'a`.
unsafe fn picture<'a>(
    rgb: *const u8,
    width: i32,
    height: i32,
    stride: i32,
    input_size: i32,
    min_score: f32,
) -> Result<Picture<'a>, String> {
    if rgb.is_null() {
        return Err("no pixels".into());
    }
    if !(1..=MAX_SIDE).contains(&width) || !(1..=MAX_SIDE).contains(&height) {
        return Err(format!("the picture is 1 to {MAX_SIDE} pixels a side"));
    }
    if stride < width * 3 {
        return Err("the stride is less than a row".into());
    }
    if !(64..=1280).contains(&input_size) || input_size % 32 != 0 {
        return Err("the input size is a multiple of 32 from 64 to 1280".into());
    }
    if !(0.0..=1.0).contains(&min_score) {
        return Err("the minimum score is from 0 to 1".into());
    }
    let (width, height, stride) = (width as usize, height as usize, stride as usize);
    let len = (height as u64 - 1) * stride as u64 + width as u64 * 3;
    // A slice may span at most isize::MAX bytes, which a 32-bit target can reach.
    let len = isize::try_from(len).map_err(|_| "the picture does not fit in memory".to_string())?
        as usize;
    // SAFETY: the caller lends `len` bytes at `rgb`, which is not null.
    let rgb = unsafe { std::slice::from_raw_parts(rgb, len) };
    Ok(Picture {
        rgb,
        width,
        height,
        stride,
    })
}

/// Copies the first `max` of `found` to `out`, giving how many it wrote.
///
/// # Safety
/// `out` must have room for `max` values, and may be null only when `max` is 0.
unsafe fn write_out<T: Copy>(found: &[T], out: *mut T, max: usize) -> i32 {
    let n = found.len().min(max);
    if n > 0 {
        // SAFETY: `out` has room for `max` ≥ `n` values.
        unsafe { std::slice::from_raw_parts_mut(out, n) }.copy_from_slice(&found[..n]);
    }
    n as i32
}

/// The ABI version, 1.
#[no_mangle]
pub extern "C" fn hs_vision_abi_version() -> u32 {
    export(0, || Ok(ABI_VERSION))
}

/// A new detector with YuNet parsed, or null with the error set.
#[no_mangle]
pub extern "C" fn hs_vision_open() -> *mut Detector {
    export(ptr::null_mut(), || {
        Detector::open().map(|d| Box::into_raw(Box::new(d)))
    })
}

/// Why this thread's last call failed, NUL-terminated and valid until the thread's next call; null when it
/// succeeded.
#[no_mangle]
pub extern "C" fn hs_vision_last_error() -> *const c_char {
    catch_unwind(|| {
        LAST_ERROR
            .try_with(|e| e.borrow().as_ref().map_or(ptr::null(), |m| m.as_ptr()))
            .unwrap_or(ptr::null())
    })
    .unwrap_or(ptr::null())
}

/// Finds the faces in a `width` × `height` RGB8 picture whose rows are `stride` bytes apart, letterboxed so that its
/// long side is `input_size`. Writes those scoring at least `min_score`, at most `max_faces` and the best first, to
/// `out`, and gives how many it wrote, or −1 with the error set.
///
/// # Safety
/// `det` is null or a detector from `hs_vision_open` that is not closed. `rgb` is null or points to the picture,
/// and `out` is null or has room for `max_faces` faces.
#[no_mangle]
pub unsafe extern "C" fn hs_vision_faces(
    det: *mut Detector,
    rgb: *const u8,
    width: i32,
    height: i32,
    stride: i32,
    input_size: i32,
    min_score: f32,
    out: *mut Face,
    max_faces: i32,
) -> i32 {
    export(-1, || {
        // SAFETY: a non-null `det` is a live detector, which serialises its own calls.
        let det = unsafe { det.as_ref() }.ok_or("no detector")?;
        // SAFETY: the caller lends the picture for this call.
        let picture = unsafe { picture(rgb, width, height, stride, input_size, min_score) }?;
        if max_faces < 0 || (max_faces > 0 && out.is_null()) {
            return Err("no room for faces".into());
        }
        let faces = det.faces(&picture, input_size as usize, min_score)?;
        // SAFETY: `out` has room for `max_faces` faces, and is not null unless that is 0.
        Ok(unsafe { write_out(&faces, out, max_faces as usize) })
    })
}

/// Finds the people in a `width` × `height` RGB8 picture whose rows are `stride` bytes apart, letterboxed so that
/// its long side is `input_size`. Writes those scoring at least `min_score`, at most `max_people` and the best first,
/// to `out`, and gives how many it wrote, or −1 with the error set. The first call parses the person model, which
/// the detector keeps; when it cannot load, this and every later call give −1.
///
/// # Safety
/// `det` is null or a detector from `hs_vision_open` that is not closed. `rgb` is null or points to the picture,
/// and `out` is null or has room for `max_people` people.
#[no_mangle]
pub unsafe extern "C" fn hs_vision_people(
    det: *mut Detector,
    rgb: *const u8,
    width: i32,
    height: i32,
    stride: i32,
    input_size: i32,
    min_score: f32,
    out: *mut Person,
    max_people: i32,
) -> i32 {
    export(-1, || {
        // SAFETY: a non-null `det` is a live detector, which serialises its own calls.
        let det = unsafe { det.as_ref() }.ok_or("no detector")?;
        // SAFETY: the caller lends the picture for this call.
        let picture = unsafe { picture(rgb, width, height, stride, input_size, min_score) }?;
        if max_people < 0 || (max_people > 0 && out.is_null()) {
            return Err("no room for people".into());
        }
        let people = det.people(&picture, input_size as usize, min_score)?;
        // SAFETY: `out` has room for `max_people` people, and is not null unless that is 0.
        Ok(unsafe { write_out(&people, out, max_people as usize) })
    })
}

/// Frees a detector. Null is a no-op.
///
/// # Safety
/// `det` is null or a detector from `hs_vision_open` that is not closed and that no other call is using.
#[no_mangle]
pub unsafe extern "C" fn hs_vision_close(det: *mut Detector) {
    export((), || {
        if !det.is_null() {
            // SAFETY: the caller gives the detector back, and nothing uses it any more.
            drop(unsafe { Box::from_raw(det) });
        }
        Ok(())
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::CStr;

    fn last_error() -> Option<String> {
        let p = hs_vision_last_error();
        (!p.is_null()).then(|| unsafe { CStr::from_ptr(p) }.to_str().unwrap().to_owned())
    }

    #[test]
    fn a_panic_is_an_internal_error() {
        assert_eq!(
            export(-1, || -> Result<i32, String> { panic!("boom {}", 7) }),
            -1
        );
        assert_eq!(last_error().as_deref(), Some("internal error: boom 7"));
        assert_eq!(
            export(-1, || -> Result<i32, String> { std::panic::panic_any(3u8) }),
            -1
        );
        assert_eq!(
            last_error().as_deref(),
            Some("internal error: a panic without a message")
        );
        assert_eq!(export(-1, || Ok(2)), 2);
        assert_eq!(last_error(), None);
    }

    #[test]
    fn an_error_with_a_nul_still_reads() {
        assert_eq!(export(-1, || Err("a\0b".to_string())), -1);
        assert_eq!(last_error().as_deref(), Some("a b"));
    }
}

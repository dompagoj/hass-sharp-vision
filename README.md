# hass-sharp-vision

Face and person detection for [HassSharp](https://github.com/dompagoj/hass-sharp).

## What it is

Two node libraries for HassSharp:

- **People detector** (`nodes/PeopleDetector.cs`): a device for entity definitions that watches a Home Assistant
  camera and reports the faces and people it sees.
- **Detect faces** (`nodes/Vision.cs`): a step for an entity definition's graph that draws the faces in a picture.

Both run on `hs_vision`, a Rust library in `native/` that runs two models on [tract](https://github.com/sonos/tract):
YuNet for faces and YOLOX-nano for people. The models are embedded in the library. HassSharp downloads the library
once from this repository's releases, checks it by its SHA-256 and keeps it with the definition's files.

With them comes an entity definition, **Front door people** (`nodes/front_door_people.json`): a People detector on
`camera.front_door`. [`hass_sharp.toml`](hass_sharp.toml) lists the three for HassSharp's **Add external**.

## Install

The files need a HassSharp newer than 0.0.48-beta: they use `[NodeSetup]`, `NodeSetupContext` and `DrawCircle`, and
the last seen sensors need its fix for timestamp sensors. **Add external** is just as new. No HassSharp release has
them yet: until one does, they are only on hass-sharp's branch `face-detection-and-external-import` on GitHub.

1. In HassSharp's panel, open **Node libraries** (or the entity definitions list) and choose **Add external**. Paste
   `https://github.com/dompagoj/hass-sharp-vision` and choose **Inspect**.
2. It lists what this repository offers: the node libraries `Vision.cs` and `PeopleDetector.cs`, and the entity
   definition **Front door people**. Leave all three checked and choose **Import**. The libraries are imported first,
   together: Detect faces needs only `Vision.cs`, and `PeopleDetector.cs` uses it.
3. The definition comes in turned off. Open **Front door people**, pick your camera in the **Front door** device's
   **Camera**, save it, and turn it on.

To update, run **Add external** again: a file you already have starts unchecked, and checking it offers to overwrite
it. Overwriting **Front door people** keeps the camera you picked and the other values you typed into the **Front door**
device, such as **Check every** or **Face score**, whether or not the new file sets them; a setting whose type changed
takes the file's.

A HassSharp without **Add external** takes the files pasted in by hand:

1. In HassSharp's panel, open **Node libraries** and choose **Add file**. Name it `Vision.cs`, paste in
   [`nodes/Vision.cs`](nodes/Vision.cs) and save it. Detect faces needs only this file.
2. Add another file, `PeopleDetector.cs`, with [`nodes/PeopleDetector.cs`](nodes/PeopleDetector.cs). It uses
   `Vision.cs`, so save `Vision.cs` first.
3. In an entity definition, add a **C# device** node whose code is

   ```csharp
   public class FrontDoor : PeopleDetector { }
   ```

   choose its camera, and save the definition.

To update these, paste both files from `main` in again and save them. Either way, a new library is downloaded into a
folder of its own checksum, and the one loaded before stays loaded until Home Assistant restarts.

## People detector

A C# device whose class derives from `PeopleDetector` has five settings:

| Setting | Default | What it is |
| --- | --- | --- |
| Camera | none (required) | The Home Assistant camera to watch. |
| Check every | 0.5 | Seconds between checks of the camera's newest frame, from 0.1 to 60. |
| Stay on for | 10 | Seconds that Face seen and Person seen stay on after the last frame with one, from 0 to 3600. |
| Face score | 0.7 | The lowest score a face counts with, from 0.1 to 1. Lower finds more, and more that are not faces. |
| Person score | 0.45 | The lowest score a person counts with, from 0.1 to 1. Lower finds more, and more that are not people. |

The device (manufacturer HassSharp, model People detector) has seven entities, in this order:

| Entity | Key | What it shows |
| --- | --- | --- |
| Camera | `camera` | Each frame checked, with the faces and people found drawn on it. It has no name of its own, so Home Assistant shows the device's. |
| Face seen | `face_seen` | Binary sensor (occupancy): on while a face is seen. |
| Person seen | `person_seen` | Binary sensor (occupancy): on while a person is seen. |
| Faces | `faces` | Sensor (measurement): how many faces the newest frame checked has. |
| People | `people` | Sensor (measurement): how many people the newest frame checked has. |
| Last face seen | `last_face_seen` | Timestamp: when a face was last seen. |
| Last person seen | `last_person_seen` | Timestamp: when a person was last seen. |

**Stay on for** is a hold. Face seen turns on at the first check that finds a face, and turns off only once
**Stay on for** seconds have passed since the last check that found one. A check that finds none within that time
leaves it on, so a frame without a face between two checks that find one does not turn it off. Person seen works the
same way. While one stays on, its last seen is written at most once a minute; when it turns off, last seen is the
time of the last check that found one. The last seen sensors keep their state across restarts; the others start
unknown.

The device opens the camera's live feed when the definition starts, and checks it on its interval whether or not
anyone watches the camera. Each check takes the newest frame, finds the faces at an input size of 640 and the people
at 416, shows the frame with what it found on the device's camera, and sets the entities. Measured in the Home
Assistant container on one arm64 host, faces at 640 take about 29 ms and people at 416 about 35 ms.

The native library is downloaded once, the first time the definition starts, and loaded from the kept file after
that. There are builds for Linux on arm64 and x64, with musl (the Home Assistant container) and with glibc:
`linux-musl-arm64`, `linux-musl-x64`, `linux-arm64` and `linux-x64`. On another platform the definition does not
start, and says why.

A check fails when the camera gives no frame within 10 seconds, for instance because it is not there. Its first
failure for a reason is logged as an error, and each one after that at debug level. While checks fail, Faces and
People are unavailable, and Face seen and Person seen go off once their hold has passed. A camera that has never
given a frame leaves them unknown.

## Detect faces

Detect faces is a value step, in the **Vision** category, for cameras made in an entity definition's graph:
**Camera frame → Detect faces →** the camera's **Image**. Its inputs are:

- **Image**: the picture, such as a camera's frame.
- **Minimum score**: the lowest score a face is drawn with, from 0 to 1; 0.7 by default.
- **Input size**: the side the picture is scaled to for the model, a multiple of 32 from 64 to 1280; 320 by default.
  A larger size finds smaller faces, more slowly.

Its output, **Image**, is a copy of the picture with each face drawn on it, the best first: a green box, a dot on each
of five points (right eye blue, left eye red, nose tip yellow, right mouth corner magenta, left mouth corner orange)
and the score above the box. A picture carries at most 500 drawings, so only as many faces as fit are drawn. The
picture given is left as it was.

Detect faces works only in entity definitions: its class's `[NodeSetup]` downloads the library and opens the detector
as the definition starts, and the detector is closed when the definition stops.

## Building

`native/build.sh` builds `libhs_vision.so` for Home Assistant's Linux runtimes:

```sh
native/build.sh [--out DIR] [RID ...]   # the RIDs given, or all four, into DIR (default native/dist)
native/build.sh --licenses              # rewrites native/THIRD-PARTY-LICENSES and builds nothing
```

The RIDs are `linux-musl-arm64`, `linux-musl-x64`, `linux-arm64` and `linux-x64`. Each build is written as
`DIR/libhs_vision-<rid>.so`, with `DIR/SHA256SUMS` for the ones built.

The only prerequisites are bash and a Docker engine (on macOS, colima). Nothing is installed on the host: each build
runs in a container with the pinned toolchain, Rust 1.99.1. A musl build is made in `rust:1.99.1-alpine`, checked to
need only `libc.musl` and `libgcc_s`, and loaded in Home Assistant's image, `homeassistant/home-assistant:2026.11.1`;
if that image cannot be pulled, the build fails. A glibc build is made in Debian bullseye, checked to use no symbol
newer than `GLIBC_2.31`, and loaded there.

A RID of another architecture than the Docker engine's needs emulation (colima: `colima start --vz-rosetta`, or
install binfmt). Under QEMU a build runs one job at a time, so a full run on macOS takes about 2.5 hours. A build on
a machine of the RID's own architecture is fast.

`native/dist/` is ignored by git, and no binary is ever committed: only the release workflow publishes them.
`native/SHA256SUMS` is a copy of a full run's `SHA256SUMS`.

`--licenses` writes `native/THIRD-PARTY-LICENSES` from cargo-about's report on the linked crates, followed by the
two models' licences. Run it after a dependency changes: CI fails when the file is not current.

## Testing

The crate's tests, from `native/`, with the toolchain `native/rust-toolchain.toml` names (`rustup toolchain install`):

```sh
cargo test --release --locked
```

The C# tests build `nodes/Vision.cs` and `nodes/PeopleDetector.cs` against a hass-sharp checkout, with every file of
its runtime suite:

```sh
dotnet run --project tests/Vision.Tests --disable-build-servers -p:HassSharpRoot=/path/to/hass-sharp -- --vision --people-detector --vision-manifest
```

`HASS_SHARP_ROOT` may name the checkout instead of `-p:HassSharpRoot`. `--vision` runs the tests of `Vision.cs`,
`--people-detector` those of `PeopleDetector.cs`, and `--vision-manifest` imports both as **Add external** does and
previews `nodes/front_door_people.json`. The checkout must be on hass-sharp's branch
`face-detection-and-external-import`, which has `[NodeSetup]`, the node library import and the still picture of the
runtime suite's fake ffmpeg. The tests need the .NET 11 SDK, Python 3 (the fake ffmpeg is a Python script) and
libjpeg-turbo. CI's C# job runs them against the hass-sharp commit that `HASS_SHARP_REF` in `ci.yaml` pins by its
full sha, now one on that branch; once HassSharp releases these APIs, the pin moves to the commit of that release's
tag.

The tests that run the detector load a local build of the library: `HS_VISION_LIBRARY` when it is set (the file must
exist), else `native/dist/libhs_vision-<rid>.so` from `build.sh`, else `native/target/release/libhs_vision.dylib` or
`.so` from `cargo build --release --lib` in `native/`. Without one they are skipped, unless
`HS_VISION_REQUIRE_LIBRARY=1`, which fails them instead. The recogniser's tests also need the SFace model:
`HS_VISION_MODEL` when it is set (the file must exist), else `native/model/face_recognition_sface_2021dec.onnx` from
`native/fetch-model.sh`. Without it they are skipped too, and `HS_VISION_REQUIRE_LIBRARY=1` fails them as well.

The pins, from the repository root:

```sh
python3 -m unittest discover -s tests
```

They check that the table in `Vision.cs` is `native/SHA256SUMS` and that its `Source` is the release of
`native/Cargo.toml`'s version. With `HS_VISION_CHECK_RELEASE=1`, as in CI, they also compare that release's
`SHA256SUMS` with `native/SHA256SUMS`, once it is released. With `HASS_SHARP_ROOT` set to a hass-sharp checkout, the
same command also reads `hass_sharp.toml` and the files it lists by the rules of that checkout's
`custom_components/hass_sharp/external.py`; without it, that test is skipped.

## Releasing

A change to `nodes/Vision.cs` or `nodes/PeopleDetector.cs` alone needs no release: users import the files, or copy
them, from `main`. A change to `PeopleDetector.cs`'s settings changes the signature `nodes/front_door_people.json`
keeps: `--vision-manifest` then fails, and prints the signature `PeopleDetector.cs` derives, to put in the file.
A change to the library is released as a new version:

1. Set the new version in `native/Cargo.toml`, and in `Cargo.lock` with `cargo update -p hs_vision --offline` in
   `native/`. If a dependency changed, run `native/build.sh --licenses` too.
2. Run `native/build.sh` for all four RIDs and copy `native/dist/SHA256SUMS` into `native/SHA256SUMS`. In `Vision.cs`,
   set `Source` to `https://github.com/dompagoj/hass-sharp-vision/releases/download/v<version>/` and the pinned table
   (between `// pinned:begin` and `// pinned:end`) to those checksums. In both node files, set the version in the first
   line's comment. `python3 -m unittest discover -s tests` checks all of these. Commit, push, and wait for CI to pass.
3. The owner tags that commit `v<version>` and pushes the tag.
4. `release.yaml` runs every test of `ci.yaml`, checks that the tag is the crate's version and is not released yet,
   builds each RID on a runner of its own architecture, and creates the release with the four libraries,
   `SHA256SUMS`, `NOTICE` and `THIRD-PARTY-LICENSES`. It compares the release's `SHA256SUMS` with
   `native/SHA256SUMS` and says whether they differ.
5. If they differ, copy the release's `SHA256SUMS` into `native/SHA256SUMS` and its checksums into `Vision.cs`'s
   table, and commit that: until then, a library downloaded from the release fails `Vision.cs`'s checksum check.

A release is never uploaded to again: a rebuild is a new version.

## Licence

MIT; see [`LICENSE`](LICENSE). [`native/NOTICE`](native/NOTICE) names the models, the linked crates and the test
photos, which are not this repository's own, and their licences.
[`native/THIRD-PARTY-LICENSES`](native/THIRD-PARTY-LICENSES) has the licence texts of the linked crates and the
models.

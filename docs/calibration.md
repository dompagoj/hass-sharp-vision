# Calibration of face recognition

How well SFace (`face_recognition_sface_2021dec.onnx`, run by `hs_vision`'s recogniser) tells the same person from
different people, by the size of the face in pixels between the eyes, measured on the public benchmark LFW. The
numbers set the People detector's recognition rules: when a face is usable, the match, suggest and learn scores,
their margins, the fit limits, and how far a face may be turned. **Defaults**, after the results, has them.

**These numbers are an upper bound.** LFW's faces are posed press photos: well lit, sharp, mostly from the front and
at eye level. The tool shrinks them to door-camera sizes with an area-averaging filter, which looks like a small face
on a sensor, but adds none of what a door camera adds: video compression, noise, night infrared, motion blur, a view
from above, a face half turned away or half in shadow. A door camera's faces will score lower for the same person
and as low or lower for different people, so the share of faces recognised at a size will be lower than here.

## The dataset

- LFW, the original pictures (not the funnelled or deep-funnelled variants): 13,233 pictures of 5,749 people, 1,680
  of them with two or more, each 250 × 250.
- Downloaded from scikit-learn's mirror on figshare, `https://ndownloader.figshare.com/files/5976018` (`lfw.tgz`), the
  URL that `sklearn/datasets/_lfw.py` lists. 180,566,744 bytes, SHA-256
  `055f7d9c632d7370e6fb4afc7468d40f970c34a80d4c6f50ffec63f5a8d536c0`, which is the checksum scikit-learn's loader
  publishes for it (in 1.9.1 and on its `main` branch on 24 November 2026).
- The dataset, the embeddings and the cache are kept outside this repository and are never committed. No picture,
  name or embedding is printed by the tool.

## How it was measured

`native/examples/calibrate.rs`, run from `native/` with the LFW folder (the one holding a folder per person) and the
model:

```sh
cargo run --release --example calibrate -- <lfw dir> model/face_recognition_sface_2021dec.onnx
```

- **Detection.** Each picture goes through the detector at 640 with a minimum score of 0.7, as the People detector
  runs it; the face nearest the centre is kept. Its eye distance and alignment fit are recorded.
- **Sizes.** For each size of 40, 32, 24, 20, 16, 12 and 10 px between the eyes, the whole picture is shrunk by
  area averaging until the face is that size (never enlarged: a face already smaller is left out at that size) and
  the face is embedded from the small picture, with the landmarks of the detection on the original scaled along.
- **Detection at the size.** The small picture is also put, unscaled, in the middle of a black 640 × 360 frame and of
  a black 1920 × 1080 frame, and the detector runs on each at 640 as it runs on a camera frame. In the 640 frame it
  sees the face at its size; in the 1080p frame, at a third of it. A face the detector does not find is never
  embedded in real use. (The detector is not run on the small picture alone: at 640 it would enlarge it to fill its
  input, and see a large blurred face that no camera frame gives it.)
- **Pairs.** At most 10 pictures a person are taken, drawn at random with a fixed seed, so that the few people with
  hundreds of pictures do not outweigh the rest. Same-person pairs are every pair among them; different-person pairs
  are 1,000,000 pairs drawn with a fixed seed. The line for 1 in N is the lowest score at which at most one
  different-person pair in N scores at or above it.
- **Two sizes.** The same with one picture at 40 px and the other at a smaller size, since a sample named from a
  near face is compared with faces further away.
- **Galleries.** At the usable size, 200 galleries of 20 people with 3 named pictures each, drawn with fixed seeds
  from the people with at least 6 pictures; the probes are each enrolled person's other pictures and the pictures of
  200 people not enrolled. A person's score is their best named picture's; a probe is recognised when the best
  person's score is at or above the line and beats the second's by the margin. Wrong means recognised as someone it
  is not, whether it is an enrolled person's or not.
- **Fit.** The alignment's fit (how far the landmarks are from the reference once aligned, in crop pixels) over all
  faces, and over the faces whose best same-person score at the usable size does or does not reach the match score.
- **Turned faces.** How far each face is turned (`Vision.Turn`: how far its nose tip lies from the midpoint of its eyes,
  along the line between them, in eye distances), from the landmarks of its detection in the picture as it is; and
  whether the frontal rule (`Vision.Frontal`) takes it. At 24 and 16 px, every face is paired with every frontal
  picture (turned less than 0.2, fitting within 7) of the same person among their drawn ones, and of everyone else, all
  of them, not drawn at random; a table counts a face's pairs when the People detector would compare it, or as the
  table says, and the faces of one bucket are also paired with each other. The turned faces are every picture's, not
  only the drawn ones', as there are few. The lines are the People detector's defaults: 0.40, 0.50, and 0.60 to learn.

### Reproducing it

The detections and embeddings are kept in a cache beside the LFW folder (`<lfw dir>.calibrate-cache`, about 47 MB),
so the statistics run again from it without embedding again. On 24 November 2026, on macOS arm64:

- The first run computed everything and wrote the cache. The runs from the cache after it printed the same numbers
  as the first run in every line it printed (the table of gallery lines, and the last table's layout, came later),
  and the last two printed the same output as each other byte for byte.
- `--check 500` computed 500 pictures drawn with a fixed seed again, detection and embeddings, and compared them with
  the cache: all 500 match bit for bit.
- Later that day the tool gained the turned faces, which need nothing the cache lacks (the landmarks are in it), and
  then their counts by fit and under the limits for turned faces. Two runs from the cache printed the same output byte
  for byte, and every line before the turned faces as before (the code before them did not change).

The tables below are the output of a run from the cache, with its headings one level down.

## Results

### Faces

13233 pictures of 5749 people; the detector found a face in 13233 of them, 1680 with two or more pictures.
Pixels between the eyes in the pictures as they are, by quantile (1 %, 10 %, 50 %, 90 %, 99 %): 30.17, 37.01, 41.44, 45.85, 50.91.

| Size | Faces at the size | Found in a 640 frame | Found in a 1080p frame | Fit when found in a 640 frame (50 %, 90 %, 99 %) |
|---|---|---|---|---|
| 40 | 8906 | 100.0 % | 99.6 % | 2.87, 3.89, 5.31 |
| 32 | 13017 | 100.0 % | 96.7 % | 2.91, 3.93, 5.30 |
| 24 | 13205 | 100.0 % | 82.9 % | 2.83, 4.03, 5.80 |
| 20 | 13222 | 100.0 % | 55.0 % | 3.02, 4.03, 5.73 |
| 16 | 13230 | 100.0 % | 19.3 % | 2.68, 3.91, 5.97 |
| 12 | 13233 | 98.9 % | 1.1 % | 2.45, 3.86, 6.46 |
| 10 | 13233 | 95.2 % | 0.2 % | 2.41, 3.90, 6.27 |

### Pairs at one size

The line for 1 in N is the lowest score at which at most one different-person pair in N scores at or above it; Same ≥ is the share of same-person pairs at or above that line.

| Size | Same-person pairs | Different-person pairs | 1 in 100 | Same ≥ | 1 in 1,000 | Same ≥ | 1 in 10,000 | Same ≥ |
|---|---|---|---|---|---|---|---|---|
| 40 | 6618 | 1000000 | 0.3000 | 99.5 % | 0.3654 | 99.2 % | 0.4177 | 98.5 % |
| 32 | 14065 | 1000000 | 0.3002 | 99.6 % | 0.3669 | 99.2 % | 0.4189 | 98.4 % |
| 24 | 14452 | 1000000 | 0.2993 | 99.6 % | 0.3655 | 99.2 % | 0.4213 | 98.2 % |
| 20 | 14487 | 1000000 | 0.2990 | 99.6 % | 0.3657 | 99.1 % | 0.4237 | 97.9 % |
| 16 | 14508 | 1000000 | 0.2967 | 99.6 % | 0.3652 | 98.9 % | 0.4214 | 97.6 % |
| 12 | 14508 | 1000000 | 0.2961 | 99.3 % | 0.3675 | 98.2 % | 0.4246 | 95.7 % |
| 10 | 14508 | 1000000 | 0.2974 | 98.8 % | 0.3720 | 96.3 % | 0.4348 | 90.8 % |

Same-person scores, by quantile:

| Size | 1 % | 5 % | 10 % | 20 % | 50 % | 80 % | 90 % |
|---|---|---|---|---|---|---|---|
| 40 | 0.38 | 0.50 | 0.55 | 0.60 | 0.69 | 0.76 | 0.80 |
| 32 | 0.38 | 0.50 | 0.54 | 0.60 | 0.69 | 0.76 | 0.80 |
| 24 | 0.38 | 0.49 | 0.54 | 0.59 | 0.68 | 0.76 | 0.79 |
| 20 | 0.37 | 0.48 | 0.53 | 0.58 | 0.68 | 0.75 | 0.79 |
| 16 | 0.36 | 0.47 | 0.52 | 0.57 | 0.66 | 0.74 | 0.78 |
| 12 | 0.33 | 0.43 | 0.48 | 0.53 | 0.63 | 0.72 | 0.76 |
| 10 | 0.29 | 0.39 | 0.44 | 0.50 | 0.60 | 0.69 | 0.73 |

Different-person scores, by quantile:

| Size | 50 % | 90 % | 99 % | 99.9 % | 99.99 % | highest |
|---|---|---|---|---|---|---|
| 40 | 0.10 | 0.21 | 0.30 | 0.37 | 0.42 | 0.66 |
| 32 | 0.10 | 0.21 | 0.30 | 0.37 | 0.42 | 0.53 |
| 24 | 0.10 | 0.21 | 0.30 | 0.37 | 0.42 | 0.72 |
| 20 | 0.09 | 0.21 | 0.30 | 0.37 | 0.42 | 0.72 |
| 16 | 0.09 | 0.20 | 0.30 | 0.37 | 0.42 | 0.55 |
| 12 | 0.08 | 0.20 | 0.30 | 0.37 | 0.42 | 0.55 |
| 10 | 0.08 | 0.20 | 0.30 | 0.37 | 0.43 | 0.63 |

### Pairs of two sizes

One picture at 40 px between the eyes and the other at the smaller size; each same-person pair both ways round.

| Sizes | Same-person pairs | Different-person pairs | 1 in 100 | Same ≥ | 1 in 1,000 | Same ≥ | 1 in 10,000 | Same ≥ |
|---|---|---|---|---|---|---|---|---|
| 40 and 32 | 18923 | 1000000 | 0.3001 | 99.5 % | 0.3661 | 99.2 % | 0.4228 | 98.3 % |
| 40 and 24 | 19156 | 1000000 | 0.2992 | 99.5 % | 0.3642 | 99.2 % | 0.4191 | 98.3 % |
| 40 and 20 | 19177 | 1000000 | 0.2984 | 99.5 % | 0.3658 | 99.1 % | 0.4234 | 98.0 % |
| 40 and 16 | 19191 | 1000000 | 0.2963 | 99.5 % | 0.3632 | 99.0 % | 0.4226 | 97.7 % |
| 40 and 12 | 19191 | 1000000 | 0.2915 | 99.5 % | 0.3593 | 98.7 % | 0.4185 | 96.7 % |
| 40 and 10 | 19191 | 1000000 | 0.2881 | 99.2 % | 0.3560 | 97.9 % | 0.4095 | 94.9 % |

Same-person scores, by quantile:

| Sizes | 1 % | 5 % | 10 % | 20 % | 50 % | 80 % | 90 % |
|---|---|---|---|---|---|---|---|
| 40 and 32 | 0.38 | 0.50 | 0.54 | 0.60 | 0.69 | 0.76 | 0.80 |
| 40 and 24 | 0.38 | 0.49 | 0.54 | 0.59 | 0.68 | 0.76 | 0.79 |
| 40 and 20 | 0.37 | 0.49 | 0.53 | 0.59 | 0.68 | 0.75 | 0.79 |
| 40 and 16 | 0.36 | 0.48 | 0.52 | 0.58 | 0.67 | 0.74 | 0.78 |
| 40 and 12 | 0.33 | 0.45 | 0.49 | 0.55 | 0.64 | 0.71 | 0.75 |
| 40 and 10 | 0.30 | 0.41 | 0.46 | 0.51 | 0.60 | 0.68 | 0.72 |

Different-person scores, by quantile:

| Sizes | 50 % | 90 % | 99 % | 99.9 % | 99.99 % | highest |
|---|---|---|---|---|---|---|
| 40 and 32 | 0.10 | 0.21 | 0.30 | 0.37 | 0.42 | 0.71 |
| 40 and 24 | 0.10 | 0.21 | 0.30 | 0.36 | 0.42 | 0.81 |
| 40 and 20 | 0.10 | 0.21 | 0.30 | 0.37 | 0.42 | 0.81 |
| 40 and 16 | 0.09 | 0.21 | 0.30 | 0.36 | 0.42 | 0.77 |
| 40 and 12 | 0.09 | 0.20 | 0.29 | 0.36 | 0.42 | 0.77 |
| 40 and 10 | 0.08 | 0.20 | 0.29 | 0.36 | 0.41 | 0.54 |

### At the rounded lines

The usable size is 10 px. Its lines: 1 in 100 at 0.2974 (suggest, rounded to 0.30), 1 in 1,000 at 0.3720 (match, rounded up to 0.40), 1 in 10,000 at 0.4348 (learn, rounded up to 0.45).

| Size | Same ≥ 0.30 | Different ≥ 0.30 | Same ≥ 0.40 | Different ≥ 0.40 | Same ≥ 0.45 | Different ≥ 0.45 |
|---|---|---|---|---|---|---|
| 40 | 99.5 % | 1.0020 % | 98.7 % | 0.0230 % | 97.7 % | 0.0018 % |
| 32 | 99.6 % | 1.0068 % | 98.8 % | 0.0256 % | 97.6 % | 0.0016 % |
| 24 | 99.6 % | 0.9773 % | 98.7 % | 0.0257 % | 97.4 % | 0.0031 % |
| 20 | 99.6 % | 0.9718 % | 98.5 % | 0.0262 % | 97.1 % | 0.0037 % |
| 16 | 99.5 % | 0.9004 % | 98.2 % | 0.0247 % | 96.3 % | 0.0040 % |
| 12 | 99.3 % | 0.8999 % | 96.9 % | 0.0280 % | 93.5 % | 0.0041 % |
| 10 | 98.8 % | 0.9291 % | 94.4 % | 0.0384 % | 88.8 % | 0.0060 % |

### Galleries at 10 px

200 galleries of 20 people with 3 named pictures each, drawn from the 311 people with at least 6 pictures at this size (of their 10 drawn); 94892 probes, 22515 of them enrolled people's other pictures and 72377 pictures of 200 people not enrolled per gallery. A probe is recognised as the best person when that person's score (their best named picture's) is at or above the line and beats the second-best person's by the margin.

At the match score, 0.40:

| Margin | Genuine probes recognised right | Probes recognised wrong | Of all probes | Genuine | Not enrolled |
|---|---|---|---|---|---|
| 0.00 | 99.3 % | 1498 | 1.579 % | 11 | 1487 |
| 0.05 | 99.2 % | 1379 | 1.453 % | 3 | 1376 |
| 0.10 | 98.8 % | 1067 | 1.124 % | 2 | 1065 |
| 0.15 | 97.6 % | 526 | 0.554 % | 2 | 524 |
| 0.20 | 94.3 % | 129 | 0.136 % | 2 | 127 |

At the learn score, 0.45:

| Margin | Genuine probes recognised right | Probes recognised wrong | Of all probes | Genuine | Not enrolled |
|---|---|---|---|---|---|
| 0.00 | 98.1 % | 218 | 0.230 % | 7 | 211 |
| 0.05 | 98.1 % | 212 | 0.223 % | 3 | 209 |
| 0.10 | 97.8 % | 200 | 0.211 % | 2 | 198 |
| 0.15 | 97.0 % | 148 | 0.156 % | 2 | 146 |
| 0.20 | 94.1 % | 72 | 0.076 % | 2 | 70 |

No margin up to 0.20 keeps the probes recognised wrong at the match score to 1 in 1,000; the fewest are at 0.20, 0.136 %.

No margin up to 0.20 keeps the probes recognised wrong at the learn score to 1 in 10,000; the fewest are at 0.20, 0.076 %.

The lowest score at which, with each margin, at most 1 in 1,000 or 1 in 10,000 probes is recognised wrong, and the genuine probes recognised right there:

| Margin | 1 in 1,000 | Genuine right | 1 in 10,000 | Genuine right |
|---|---|---|---|---|
| 0.00 | 0.4701 | 97.0 % | 0.5395 | 89.4 % |
| 0.05 | 0.4698 | 97.0 % | 0.5395 | 89.4 % |
| 0.10 | 0.4694 | 96.8 % | 0.5395 | 89.4 % |
| 0.15 | 0.4646 | 96.4 % | 0.5395 | 89.2 % |
| 0.20 | 0.4348 | 94.2 % | 0.5202 | 90.7 % |

### Fit

The alignment's fit in crop pixels, of the faces as detected in the pictures as they are; matched means the face's best same-person score at 10 px reaches the match score.

| Faces | Count | 10 % | 50 % | 90 % | 95 % | 99 % | highest |
|---|---|---|---|---|---|---|---|
| All | 13233 | 1.33 | 2.62 | 4.15 | 4.70 | 6.55 | 11.15 |
| Matched | 6285 | 1.40 | 2.64 | 4.14 | 4.68 | 6.39 | 10.22 |
| Not matched | 135 | 1.28 | 2.73 | 4.58 | 4.93 | 5.66 | 5.86 |

Over 7: 0.6 % of the matched faces, 0.0 % of those not matched.

| Size | Found in a 640 frame with a fit over 7 |
|---|---|
| 40 | 0.1 % |
| 32 | 0.2 % |
| 24 | 0.3 % |
| 20 | 0.5 % |
| 16 | 0.6 % |
| 12 | 0.6 % |
| 10 | 0.6 % |

### What the rules give

| Rule | Value |
|---|---|
| Usable eyes | 10 |
| Learn eyes | 18 |
| Fit limit | 7 |
| Match score | 0.40 |
| Match margin | none |
| Suggest score | 0.30 |
| Learn score | 0.45 |
| Learn margin | none |
| Verdict | PASS |

### Turned faces

How far a face is turned is how far its nose tip lies from the midpoint of its eyes, along the line between them, in eye distances: 0 looking straight at the camera, 0.5 with the nose in line with one eye. A frontal face, the other face of each pair below, is one turned less than 0.2 that fits within 7. The People detector compares a face the frontal rule takes when it fits within 7, and one the rule turns away when its eyes are a fifth of its box apart, it is turned at most 0.70 and it fits within 10.

Of the 13233 faces found, the frontal rule (eyes a fifth of the box apart, the nose between them across the picture) turns away 112 (0.8 %), 50 of which fit within 7 and 106 within 10; 1 of all faces have their eyes closer than a fifth of the box. Turn of the faces it turns away, by quantile (1 %, 10 %, 50 %, 90 %, 99 %, highest): 0.33, 0.40, 0.50, 0.76, 1.07, 1.38. Turn of all faces, by quantile (50 %, 90 %, 99 %, 99.9 %, highest): 0.09, 0.24, 0.48, 0.76, 1.38.

Of the faces it turns away, 55 are turned less than 0.5: a tilted head moves the nose across the picture without turning. Of the faces it takes, 42 are turned 0.5 or more, 20 of which fit within 7.

Of the 112 faces it turns away, a limit on the turn takes those with their eyes a fifth of the box apart: at 0.40, 12 (12 fitting within 7, 12 within 10); at 0.45, 31 (28 fitting within 7, 31 within 10); at 0.50, 55 (47 fitting within 7, 55 within 10); at 0.55, 65 (50 fitting within 7, 65 within 10); at 0.60, 74 (50 fitting within 7, 74 within 10); at 0.65, 81 (50 fitting within 7, 81 within 10); at 0.70, 92 (50 fitting within 7, 91 within 10); at 0.80, 106 (50 fitting within 7, 103 within 10).

The 84 faces turned 0.40 or more that do not fit within 7 fit, by quantile (50 %, 90 %, highest): 7.83, 9.78, 11.15.

The 41 faces turned 0.60 or more that fit over 7 to 10 are turned, by quantile (10 %, 50 %, 90 %, highest): 0.63, 0.69, 0.80, 1.07.

#### Turned faces at 24 px

Each face of the bucket that fits within 7 against every frontal picture of the same person (of their 10 drawn) and of everyone else (of theirs). Same ≥ and Different ≥ are the shares of those pairs at or above the score; Crossing are the faces of the bucket that score at or above 0.50 with at least one frontal picture of someone else, of 8732 pictures of 5044 people.

| Turn | Faces | Fit within 7 | People | Same-person pairs | Same ≥ 0.40 | Same ≥ 0.50 | Different-person pairs | Different ≥ 0.40 | Different ≥ 0.50 | Crossing |
|---|---|---|---|---|---|---|---|---|---|---|
| 0.00 to 0.10 | 6949 | 6949 | 3646 | 24442 | 98.8 % | 94.8 % | 60648674 | 0.0247 % | 0.0005 % | 236 (3.4 %) |
| 0.10 to 0.20 | 4039 | 4039 | 2332 | 14835 | 99.2 % | 96.0 % | 35250533 | 0.0312 % | 0.0006 % | 148 (3.7 %) |
| 0.20 to 0.30 | 1537 | 1537 | 1066 | 5513 | 99.2 % | 96.3 % | 13415571 | 0.0330 % | 0.0006 % | 68 (4.4 %) |
| 0.30 to 0.40 | 461 | 461 | 366 | 1602 | 98.9 % | 96.4 % | 4023850 | 0.0335 % | 0.0006 % | 13 (2.8 %) |
| 0.40 to 0.45 | 80 | 78 | 72 | 224 | 100.0 % | 97.8 % | 680872 | 0.0251 % | 0.0007 % | 4 (5.1 %) |
| 0.45 to 0.50 | 63 | 59 | 54 | 178 | 100.0 % | 96.6 % | 515010 | 0.0309 % | 0.0004 % | 1 (1.7 %) |
| 0.50 to 0.55 | 29 | 19 | 18 | 83 | 100.0 % | 97.6 % | 165825 | 0.0470 % | 0.0006 % | 1 (5.3 %) |
| 0.55 to 0.60 | 20 | 4 | 4 | 22 | 68.2 % | 68.2 % | 34906 | 0.0430 % | 0.0258 % | 1 (25.0 %) |
| 0.60 to 0.70 | 15 | 0 | 0 | 0 | – | – | 0 | – | – | – |
| 0.70 to 0.80 | 10 | 0 | 0 | 0 | – | – | 0 | – | – | – |
| 0.80 to 1.00 | 2 | 0 | 0 | 0 | – | – | 0 | – | – | – |
| 1.00 and over | 0 | 0 | 0 | 0 | – | – | 0 | – | – | – |

Two faces of the same bucket, both fitting within 7:

| Turn | Same-person pairs | Same ≥ 0.50 | Different-person pairs | Different ≥ 0.40 | Different ≥ 0.50 |
|---|---|---|---|---|---|
| 0.00 to 0.10 | 56221 | 98.3 % | 24084605 | 0.0269 % | 0.0006 % |
| 0.10 to 0.20 | 25692 | 98.7 % | 8129049 | 0.0412 % | 0.0013 % |
| 0.20 to 0.30 | 4394 | 98.9 % | 1176022 | 0.0625 % | 0.0006 % |
| 0.30 to 0.40 | 340 | 99.7 % | 105690 | 0.1202 % | 0.0170 % |
| 0.40 to 0.45 | 7 | 100.0 % | 2996 | 0.1335 % | 0.0000 % |
| 0.45 to 0.50 | 5 | 100.0 % | 1706 | 0.0586 % | 0.0000 % |
| 0.50 to 0.55 | 1 | 100.0 % | 170 | 0.0000 % | 0.0000 % |
| 0.55 to 0.60 | 0 | – | 6 | 0.0000 % | 0.0000 % |
| 0.60 to 0.70 | 0 | – | 0 | – | – |
| 0.70 to 0.80 | 0 | – | 0 | – | – |
| 0.80 to 1.00 | 0 | – | 0 | – | – |
| 1.00 and over | 0 | – | 0 | – | – |

The faces turned 0.40 or more by their fit, each paired whatever its fit; Turned away are those of them the frontal rule turns away:

| Turn | Fit | Faces | Turned away | People | Same-person pairs | Same ≥ 0.50 | Different-person pairs | Different ≥ 0.40 | Different ≥ 0.50 |
|---|---|---|---|---|---|---|---|---|---|
| 0.40 to 0.50 | 7 or less | 137 | 33 | 123 | 402 | 97.3 % | 1195882 | 0.0276 % | 0.0006 % |
| 0.40 to 0.50 | over 7 to 10 | 6 | 6 | 6 | 24 | 95.8 % | 52368 | 0.0286 % | 0.0000 % |
| 0.40 to 0.50 | over 10 to 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.40 to 0.50 | over 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.50 to 0.55 | 7 or less | 19 | 3 | 18 | 83 | 97.6 % | 165825 | 0.0470 % | 0.0006 % |
| 0.50 to 0.55 | over 7 to 10 | 10 | 6 | 10 | 29 | 100.0 % | 87291 | 0.0229 % | 0.0011 % |
| 0.50 to 0.55 | over 10 to 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.50 to 0.55 | over 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.55 to 0.60 | 7 or less | 4 | 0 | 4 | 22 | 68.2 % | 34906 | 0.0430 % | 0.0258 % |
| 0.55 to 0.60 | over 7 to 10 | 16 | 7 | 15 | 44 | 97.7 % | 139668 | 0.0401 % | 0.0000 % |
| 0.55 to 0.60 | over 10 to 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.55 to 0.60 | over 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.60 to 0.70 | 7 or less | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.60 to 0.70 | over 7 to 10 | 15 | 9 | 14 | 59 | 93.2 % | 130921 | 0.0321 % | 0.0000 % |
| 0.60 to 0.70 | over 10 to 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.60 to 0.70 | over 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.70 to 0.80 | 7 or less | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.70 to 0.80 | over 7 to 10 | 10 | 9 | 10 | 54 | 92.6 % | 87266 | 0.0160 % | 0.0011 % |
| 0.70 to 0.80 | over 10 to 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.70 to 0.80 | over 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.80 and over | 7 or less | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.80 and over | over 7 to 10 | 2 | 1 | 2 | 2 | 100.0 % | 17462 | 0.0115 % | 0.0000 % |
| 0.80 and over | over 10 to 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.80 and over | over 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |

The faces the frontal rule turns away, with their eyes a fifth of the box apart, that a limit on the turn would take, beside those the rule takes; Compared are those that fit within the limit for them (7 for the faces the rule takes, 10 for the others, unless the row says), whose pairs are counted:

| Faces | Count | Compared | People | Same-person pairs | Same ≥ 0.50 | Same ≥ 0.60 | Different-person pairs | Different ≥ 0.40 | Different ≥ 0.50 | Crossing |
|---|---|---|---|---|---|---|---|---|---|---|
| Taken by the frontal rule | 13119 | 13098 | 5705 | 46762 | 95.4 % | 80.9 % | 114316242 | 0.0280 % | 0.0005 % | 471 (3.6 %) |
| Turned away by it, turned at most 0.40 | 12 | 12 | 11 | 62 | 100.0 % | 85.5 % | 104722 | 0.0449 % | 0.0000 % | 0 (0.0 %) |
| Turned away by it, turned at most 0.45 | 28 | 28 | 26 | 87 | 100.0 % | 81.6 % | 244409 | 0.0356 % | 0.0004 % | 1 (3.6 %) |
| Turned away by it, turned at most 0.50 | 51 | 51 | 48 | 145 | 97.2 % | 80.0 % | 445187 | 0.0290 % | 0.0002 % | 1 (2.0 %) |
| Turned away by it, turned at most 0.55 | 60 | 60 | 55 | 175 | 97.7 % | 79.4 % | 523745 | 0.0284 % | 0.0002 % | 1 (1.7 %) |
| Turned away by it, turned at most 0.60 | 67 | 67 | 61 | 190 | 97.4 % | 79.5 % | 584854 | 0.0311 % | 0.0002 % | 1 (1.5 %) |
| Turned away by it, turned at most 0.65 | 72 | 72 | 64 | 209 | 96.7 % | 78.5 % | 628495 | 0.0304 % | 0.0002 % | 1 (1.4 %) |
| Turned away by it, turned at most 0.70 | 76 | 76 | 67 | 225 | 96.9 % | 78.2 % | 663407 | 0.0295 % | 0.0002 % | 1 (1.3 %) |
| Turned away by it, turned at most 0.80 | 85 | 85 | 75 | 269 | 95.9 % | 74.3 % | 741951 | 0.0272 % | 0.0003 % | 2 (2.4 %) |
| Turned away by it, turned at most 0.55, fitting within 7 | 60 | 48 | 43 | 137 | 97.8 % | 81.8 % | 418999 | 0.0310 % | 0.0002 % | 1 (2.1 %) |
| Turned away by it, turned at most 0.55, fitting over 7 to 10 | 60 | 12 | 12 | 38 | 97.4 % | 71.1 % | 104746 | 0.0181 % | 0.0000 % | 0 (0.0 %) |
| Turned away by it, turned over 0.55 to 0.70, fitting within 7 | 16 | 0 | 0 | 0 | – | – | 0 | – | – | – |
| Turned away by it, turned over 0.55 to 0.70, fitting over 7 to 10 | 16 | 16 | 15 | 50 | 94.0 % | 74.0 % | 139662 | 0.0337 % | 0.0000 % | 0 (0.0 %) |
| Turned away by it, turned over 0.70 to 0.80, fitting within 7 | 9 | 0 | 0 | 0 | – | – | 0 | – | – | – |
| Turned away by it, turned over 0.70 to 0.80, fitting over 7 to 10 | 9 | 9 | 9 | 44 | 90.9 % | 54.5 % | 78544 | 0.0076 % | 0.0013 % | 1 (11.1 %) |
| Turned away by it, all | 86 | 86 | 76 | 271 | 95.9 % | 73.8 % | 750681 | 0.0272 % | 0.0003 % | 2 (2.3 %) |
| Compared by the People detector | 13205 | 13174 | 5732 | 46987 | 95.5 % | 80.9 % | 114979649 | 0.0280 % | 0.0005 % | 472 (3.6 %) |

Over the 8813 faces fitting within 7 with a frontal picture of the same person, the rank correlation (Spearman's) of each measure with the face's mean score against those pictures: the turn -0.012, the ratio of the eyes' distances to the nose 0.014.

#### Turned faces at 16 px

Each face of the bucket that fits within 7 against every frontal picture of the same person (of their 10 drawn) and of everyone else (of theirs). Same ≥ and Different ≥ are the shares of those pairs at or above the score; Crossing are the faces of the bucket that score at or above 0.50 with at least one frontal picture of someone else, of 8732 pictures of 5044 people.

| Turn | Faces | Fit within 7 | People | Same-person pairs | Same ≥ 0.40 | Same ≥ 0.50 | Different-person pairs | Different ≥ 0.40 | Different ≥ 0.50 | Crossing |
|---|---|---|---|---|---|---|---|---|---|---|
| 0.00 to 0.10 | 6949 | 6949 | 3646 | 24442 | 98.4 % | 92.9 % | 60648674 | 0.0254 % | 0.0005 % | 272 (3.9 %) |
| 0.10 to 0.20 | 4039 | 4039 | 2332 | 14835 | 98.8 % | 94.4 % | 35250533 | 0.0296 % | 0.0005 % | 144 (3.6 %) |
| 0.20 to 0.30 | 1537 | 1537 | 1066 | 5513 | 98.9 % | 94.9 % | 13415571 | 0.0309 % | 0.0005 % | 59 (3.8 %) |
| 0.30 to 0.40 | 461 | 461 | 366 | 1602 | 98.7 % | 94.2 % | 4023850 | 0.0322 % | 0.0006 % | 14 (3.0 %) |
| 0.40 to 0.45 | 83 | 80 | 74 | 224 | 100.0 % | 96.0 % | 698336 | 0.0255 % | 0.0007 % | 3 (3.8 %) |
| 0.45 to 0.50 | 65 | 60 | 55 | 185 | 99.5 % | 94.6 % | 523735 | 0.0271 % | 0.0004 % | 1 (1.7 %) |
| 0.50 to 0.55 | 30 | 19 | 18 | 83 | 100.0 % | 94.0 % | 165825 | 0.0386 % | 0.0000 % | 0 (0.0 %) |
| 0.55 to 0.60 | 22 | 4 | 4 | 22 | 68.2 % | 68.2 % | 34906 | 0.0372 % | 0.0258 % | 1 (25.0 %) |
| 0.60 to 0.70 | 24 | 0 | 0 | 0 | – | – | 0 | – | – | – |
| 0.70 to 0.80 | 15 | 0 | 0 | 0 | – | – | 0 | – | – | – |
| 0.80 to 1.00 | 4 | 0 | 0 | 0 | – | – | 0 | – | – | – |
| 1.00 and over | 1 | 0 | 0 | 0 | – | – | 0 | – | – | – |

Two faces of the same bucket, both fitting within 7:

| Turn | Same-person pairs | Same ≥ 0.50 | Different-person pairs | Different ≥ 0.40 | Different ≥ 0.50 |
|---|---|---|---|---|---|
| 0.00 to 0.10 | 56221 | 97.3 % | 24084605 | 0.0276 % | 0.0007 % |
| 0.10 to 0.20 | 25692 | 97.9 % | 8129049 | 0.0379 % | 0.0013 % |
| 0.20 to 0.30 | 4394 | 98.2 % | 1176022 | 0.0520 % | 0.0005 % |
| 0.30 to 0.40 | 340 | 99.7 % | 105690 | 0.1372 % | 0.0132 % |
| 0.40 to 0.45 | 7 | 100.0 % | 3153 | 0.0634 % | 0.0000 % |
| 0.45 to 0.50 | 5 | 100.0 % | 1765 | 0.0000 % | 0.0000 % |
| 0.50 to 0.55 | 1 | 100.0 % | 170 | 0.0000 % | 0.0000 % |
| 0.55 to 0.60 | 0 | – | 6 | 0.0000 % | 0.0000 % |
| 0.60 to 0.70 | 0 | – | 0 | – | – |
| 0.70 to 0.80 | 0 | – | 0 | – | – |
| 0.80 to 1.00 | 0 | – | 0 | – | – |
| 1.00 and over | 0 | – | 0 | – | – |

The faces turned 0.40 or more by their fit, each paired whatever its fit; Turned away are those of them the frontal rule turns away:

| Turn | Fit | Faces | Turned away | People | Same-person pairs | Same ≥ 0.50 | Different-person pairs | Different ≥ 0.40 | Different ≥ 0.50 |
|---|---|---|---|---|---|---|---|---|---|
| 0.40 to 0.50 | 7 or less | 140 | 35 | 126 | 409 | 95.4 % | 1222071 | 0.0262 % | 0.0006 % |
| 0.40 to 0.50 | over 7 to 10 | 8 | 8 | 8 | 34 | 97.1 % | 69822 | 0.0143 % | 0.0000 % |
| 0.40 to 0.50 | over 10 to 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.40 to 0.50 | over 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.50 to 0.55 | 7 or less | 19 | 3 | 18 | 83 | 94.0 % | 165825 | 0.0386 % | 0.0000 % |
| 0.50 to 0.55 | over 7 to 10 | 11 | 7 | 11 | 34 | 100.0 % | 96018 | 0.0156 % | 0.0010 % |
| 0.50 to 0.55 | over 10 to 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.50 to 0.55 | over 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.55 to 0.60 | 7 or less | 4 | 0 | 4 | 22 | 68.2 % | 34906 | 0.0372 % | 0.0258 % |
| 0.55 to 0.60 | over 7 to 10 | 18 | 9 | 17 | 61 | 95.1 % | 157115 | 0.0356 % | 0.0006 % |
| 0.55 to 0.60 | over 10 to 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.55 to 0.60 | over 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.60 to 0.70 | 7 or less | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.60 to 0.70 | over 7 to 10 | 23 | 17 | 19 | 117 | 91.5 % | 200719 | 0.0319 % | 0.0000 % |
| 0.60 to 0.70 | over 10 to 15 | 1 | 1 | 1 | 7 | 0.0 % | 8725 | 0.0115 % | 0.0000 % |
| 0.60 to 0.70 | over 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.70 to 0.80 | 7 or less | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.70 to 0.80 | over 7 to 10 | 14 | 12 | 14 | 56 | 91.1 % | 122192 | 0.0155 % | 0.0008 % |
| 0.70 to 0.80 | over 10 to 15 | 1 | 1 | 1 | 8 | 100.0 % | 8724 | 0.0115 % | 0.0000 % |
| 0.70 to 0.80 | over 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.80 and over | 7 or less | 0 | 0 | 0 | 0 | – | 0 | – | – |
| 0.80 and over | over 7 to 10 | 4 | 3 | 4 | 14 | 100.0 % | 34914 | 0.0200 % | 0.0000 % |
| 0.80 and over | over 10 to 15 | 1 | 1 | 1 | 0 | – | 8732 | 0.0000 % | 0.0000 % |
| 0.80 and over | over 15 | 0 | 0 | 0 | 0 | – | 0 | – | – |

The faces the frontal rule turns away, with their eyes a fifth of the box apart, that a limit on the turn would take, beside those the rule takes; Compared are those that fit within the limit for them (7 for the faces the rule takes, 10 for the others, unless the row says), whose pairs are counted:

| Faces | Count | Compared | People | Same-person pairs | Same ≥ 0.50 | Same ≥ 0.60 | Different-person pairs | Different ≥ 0.40 | Different ≥ 0.50 | Crossing |
|---|---|---|---|---|---|---|---|---|---|---|
| Taken by the frontal rule | 13121 | 13099 | 5705 | 46769 | 93.6 % | 75.9 % | 114324967 | 0.0276 % | 0.0005 % | 493 (3.8 %) |
| Turned away by it, turned at most 0.40 | 12 | 12 | 11 | 62 | 96.8 % | 80.6 % | 104722 | 0.0420 % | 0.0010 % | 1 (8.3 %) |
| Turned away by it, turned at most 0.45 | 31 | 31 | 29 | 87 | 97.7 % | 74.7 % | 270605 | 0.0285 % | 0.0004 % | 1 (3.2 %) |
| Turned away by it, turned at most 0.50 | 55 | 55 | 52 | 155 | 95.5 % | 74.2 % | 480105 | 0.0237 % | 0.0002 % | 1 (1.8 %) |
| Turned away by it, turned at most 0.55 | 65 | 65 | 60 | 190 | 96.3 % | 72.6 % | 567390 | 0.0233 % | 0.0002 % | 1 (1.5 %) |
| Turned away by it, turned at most 0.60 | 74 | 74 | 67 | 222 | 95.5 % | 68.9 % | 645946 | 0.0260 % | 0.0003 % | 2 (2.7 %) |
| Turned away by it, turned at most 0.65 | 81 | 81 | 71 | 251 | 95.2 % | 69.7 % | 707041 | 0.0263 % | 0.0003 % | 2 (2.5 %) |
| Turned away by it, turned at most 0.70 | 92 | 91 | 78 | 315 | 94.3 % | 68.9 % | 794297 | 0.0263 % | 0.0003 % | 2 (2.2 %) |
| Turned away by it, turned at most 0.80 | 105 | 103 | 89 | 360 | 93.6 % | 66.9 % | 899036 | 0.0240 % | 0.0003 % | 3 (2.9 %) |
| Turned away by it, turned at most 0.55, fitting within 7 | 65 | 50 | 45 | 137 | 95.6 % | 75.9 % | 436463 | 0.0268 % | 0.0002 % | 1 (2.0 %) |
| Turned away by it, turned at most 0.55, fitting over 7 to 10 | 65 | 15 | 15 | 53 | 98.1 % | 64.2 % | 130927 | 0.0115 % | 0.0000 % | 0 (0.0 %) |
| Turned away by it, turned over 0.55 to 0.70, fitting within 7 | 27 | 0 | 0 | 0 | – | – | 0 | – | – | – |
| Turned away by it, turned over 0.55 to 0.70, fitting over 7 to 10 | 27 | 26 | 22 | 125 | 91.2 % | 63.2 % | 226907 | 0.0339 % | 0.0004 % | 1 (3.8 %) |
| Turned away by it, turned over 0.70 to 0.80, fitting within 7 | 13 | 0 | 0 | 0 | – | – | 0 | – | – | – |
| Turned away by it, turned over 0.70 to 0.80, fitting over 7 to 10 | 13 | 12 | 12 | 45 | 88.9 % | 53.3 % | 104739 | 0.0067 % | 0.0010 % | 1 (8.3 %) |
| Turned away by it, all | 109 | 106 | 92 | 374 | 93.9 % | 66.3 % | 925218 | 0.0238 % | 0.0003 % | 3 (2.8 %) |
| Compared by the People detector | 13230 | 13190 | 5736 | 47084 | 93.6 % | 75.9 % | 115119264 | 0.0276 % | 0.0005 % | 495 (3.8 %) |

Over the 8814 faces fitting within 7 with a frontal picture of the same person, the rank correlation (Spearman's) of each measure with the face's mean score against those pictures: the turn -0.009, the ratio of the eyes' distances to the nose 0.011.

## What the results say

- **Different people score the same at every size.** The line for 1 in 1,000 different-person pairs is 0.365 to
  0.372 from 40 px down to 10 px, and for 1 in 10,000 0.418 to 0.435. A smaller face does not make strangers look
  more alike; it makes the same person look less alike: the median same-person score falls from 0.69 at 40 px to
  0.66 at 16 px and 0.60 at 10 px. These lines are lower than the design expected (match near 0.6, learn near 0.7)
  and close to the 0.363 that OpenCV gives for SFace's cosine.
- **Comparing a near face with a far one costs little:** a named sample at 40 px against a face at 16 px matches as
  well as two faces at 16 px.
- **The detector sets the floor before recognition does.** In a frame as wide as the detector's input it finds
  almost every face down to 12 px; in a 1080p frame, which it sees at a third of its size, it finds 83 % at 24 px,
  55 % at 20 px, 19 % at 16 px and 1 % at 12 px.
- **The margin does not stop strangers.** Nearly every wrong recognition in the galleries is a stranger's face that
  scores above the line for one enrolled person, and that person's score is then far above the second's. The match
  line is set per pair (1 in 1,000), but a face is compared with 20 people of 3 samples each, so a stranger has many
  more chances to cross it: at 0.40 without a margin, 2 of every 100 strangers' faces are given a name. A margin of
  0.20 brings it to 1.4 in 1,000 of all probes and costs 5 of every 100 genuine faces. A higher line does more for
  less: with a margin of 0.10, 0.47 keeps wrong recognitions to 1 in 1,000 and recognises 96.8 % of genuine faces.
- **The fit does not predict a failed match on LFW:** the faces whose best same-person score misses the match score
  fit no worse than those that match. LFW has few profiles, so the fit limit here only removes the worst 0.6 % of
  frontal faces. Faces found again in the small pictures fit about as well as on the originals.
- **A turned face fits worse, but that does not make it match worse.** Between 0.50 and 0.55 a third of the faces fit
  worse than 7, from 0.55 to 0.60 four in five, and from 0.60 on every one; but none of the faces turned 0.40 or more
  fits worse than 11.15 (the median of those over 7 is 7.83, their 90th percentile 9.78). The faces turned 0.40 or more
  that fit 7 to 10 match their own person about as often as frontal faces do, at every turn up to 0.80 (92.6 % to 100 %
  of their pairs at 24 px, against 95.4 %), and strangers no more often (0.012 % to 0.040 % of their pairs reach 0.40,
  against 0.028 %; at 0.50, 0 to 1 pair a bucket). The fit is a frontal face's measure: for a turned one it mostly says
  how far it is turned, which is why turned faces have a limit of their own (**How far a face may be turned**).
- **The frontal rule turns away few faces, and half of them are not turned.** It turns away 112 of the 13,233 faces
  (0.8 %); 55 of those are turned less than 0.5 along their eyes (a tilted head puts the nose beside an eye across the
  picture). 50 of the 112 fit within 7, every one of them turned at most 0.55; 106 fit within 10.
- **Neither measure predicts the score over LFW.** The rank correlation of the face's mean same-person score with the
  turn is −0.01, and with the ratio of the eyes' distances to the nose 0.01: almost every face is near frontal, and
  what lowers a score there is not the turn. The turn is the measure used because it does not change with a tilt of the
  head, and because for an upright face 0.5 is where the frontal rule's line is.
- **Two strangers turned alike are a little more alike.** Two faces of different people both turned 0.30 to 0.40
  reach the suggest score 4 to 5 times as often as two frontal faces (0.12 % of pairs against 0.03 %), and the match
  score 0.017 % of the time against 0.0006 %, on 105,690 pairs. A person named from a turned card, compared with a
  stranger turned the same way, is that much likelier to be suggested or mistaken. Further out the buckets have too
  few pairs to say.

## Defaults

The values the People detector ships with. They are stricter than the plan's rules give (next section), for the
reasons under them:

```
UsableEyes = 16
LearnEyes = 24
FitLimit = 7
TurnLimit = 0.70
TurnedFitLimit = 10
MatchScore = 0.50
MatchMargin = 0.10
SuggestScore = 0.40
LearnScore = 0.60
LearnMargin = 0.15
Unlike = 0.40
```

- `MatchScore` 0.50. The plan's rule looks at one pair of faces, but a face at the door is compared with every sample
  of everyone in the list, so a stranger has many chances to cross the line: in the galleries, 0.47 is where wrong
  recognitions fall to 1 in 1,000 (96.8 % of genuine faces still recognised). 0.50 leaves room above that for a list
  with more samples a person than the galleries' three (up to 40), and for a door camera's worse pictures.
- `SuggestScore` 0.40 and `LearnScore` 0.60: a tenth under and a tenth over the match score. The suggest line fills
  in a name the user may save without looking, so it sits at the plan's own match line (1 in 1,000 a pair), not at 1 in
  100. The learn line is above the galleries' 1 in 10,000 (0.54): a face learned wrongly stays and is compared with
  every later face. When the user moves *Match score*, the two lines move with it, a tenth either side.
- `MatchMargin` 0.10 and `LearnMargin` 0.15, the design's values. The galleries show a margin does little against
  strangers, which the higher lines now handle; it is kept for what LFW cannot measure, two people in one list who
  look alike, as relatives do.
- `UsableEyes` 16 and `LearnEyes` 24, the design's values. On LFW recognition holds down to 10 px, but those are clean
  photos, and in a 1080p frame the detector finds few faces under 16 px anyway.
- `FitLimit` 7, as measured.
- `Unlike` 0.40 (`FaceMatcher.Unlike`), which is not one of the plan's rules and does not move with *Match score*. A
  Save that names a card's faces for a person asks first when none of them comes this close to any of the person's
  samples, named or learned: faces that unlike are far more likely a stranger's, the wrong person chosen, than a new
  look of theirs. It is the line for 1 in 1,000 different-person pairs (0.365 to 0.372 at every size), rounded up to
  0.05 as the plan rounds its match line. Under it fall 1.3, 1.2, 1.3, 1.5 and 1.8 % of same-person pairs at 40, 32,
  24, 20 and 16 px between the eyes (3.1 % at 12 px, 5.6 % at 10 px); over it, 0.023 to 0.026 % of different-person
  pairs at those sizes (0.038 % at 10 px), so more than 99.96 % of them are under it.
  - A lower line would ask the same person less often and let far more wrong names through. At 0.30, the line for 1 in
    100, 0.4 to 0.5 % of same-person pairs fall under it, but 0.90 to 1.01 % of different-person pairs are over it.
    The gap grows with the pairs compared: the guard takes the best of every pair of the card's faces (up to 5) and the
    person's samples (up to 40), as a gallery takes the best of its 60 samples. There, 2.05 % of strangers' probes reach
    0.40 for someone (1,487 of 72,377, with no margin), near the 2.3 % that 60 pairs at 10 px's 0.038 % give when taken
    as independent. Taken so, 200 pairs reach 0.40 for about 5 % of wrong names, and 0.30 for about 85 %: at 0.30 the
    guard would miss most wrong names given to a person with many samples, which is the case it is for.
  - The best of several pairs helps the same person too: with 3 named pictures each, 99.3 % of the galleries' genuine
    probes at 10 px reach 0.40 as the right person, so at most 0.7 % would be asked about. A known person's face only
    comes to a card when it is not recognised, though: about 3 % of genuine probes at the gallery line nearest the
    defaults (0.47 with a margin of 0.10), of which up to a fifth (0.7 of 3.2 points) are under 0.40. So, on LFW's
    clean photos, up to about one in five of a known person's cards is asked about when it is named for them; the rest
    are suggested as them at the default *Match score*, where this line is the suggest line. Sunglasses, a turned head,
    night infrared and a door camera's blur lower the same person's scores, by an amount this data cannot measure.
- `TurnLimit` 0.70 and `TurnedFitLimit` 10, from **How far a face may be turned** below: a face the frontal rule turns
  away is compared when it is turned at most 0.70 and fits within 10, where a face the rule takes still needs to fit
  within 7 and has no limit on its turn. They are not among the plan's rules, which came before them, and so are not in
  **What the plan's rules give**.

## How far a face may be turned

The rule: the widest limits at which the faces the frontal rule turns away but the limits take (a) do not reach the
match score with strangers more often than frontal faces do, and (b) still reach it with the same person about as
often as frontal faces do; and no wider than LFW has faces to show it. The numbers are from **Turned faces** above,
for the faces the frontal rule turns away whose eyes are a fifth of their box apart.

- **A turn of 0.70 and a fit of 10 meet both.** The faces they take beside the frontal ones are 76 at 24 px, of 67
  people: 96.9 % of their 225 pairs with frontal pictures of the same person reach 0.50 (frontal faces: 95.4 %), and
  0.0002 % of their 663,407 pairs with strangers do (frontal: 0.0005 %); at 0.40, 0.0295 % (frontal: 0.0280 %); one of
  the 76 crosses 0.50 with a stranger (1.3 %, frontal 3.6 %). At 16 px, 91 faces of 78 people: 94.3 %, 0.0003 % and
  0.0263 % (frontal 93.6 %, 0.0005 % and 0.0276 %), and two of them cross 0.50 (2.2 %, frontal 3.8 %).
- **By turn and fit** (24 px, then 16 px):

  | Turned | Fit | Faces (people) | Same ≥ 0.50 | Same ≥ 0.60 | Different ≥ 0.40 | Different ≥ 0.50 |
  |---|---|---|---|---|---|---|
  | at most 0.55 | 7 or less | 48 (43), 50 (45) | 97.8 %, 95.6 % | 81.8 %, 75.9 % | 0.0310 %, 0.0268 % | 0.0002 %, 0.0002 % |
  | at most 0.55 | over 7 to 10 | 12 (12), 15 (15) | 97.4 %, 98.1 % | 71.1 %, 64.2 % | 0.0181 %, 0.0115 % | 0, 0 |
  | 0.55 to 0.70 | 7 or less | none | | | | |
  | 0.55 to 0.70 | over 7 to 10 | 16 (15), 26 (22) | 94.0 %, 91.2 % | 74.0 %, 63.2 % | 0.0337 %, 0.0339 % | 0, 0.0004 % |
  | 0.70 to 0.80, not taken | over 7 to 10 | 9 (9), 12 (12) | 90.9 %, 88.9 % | 54.5 %, 53.3 % | 0.0076 %, 0.0067 % | 0.0013 %, 0.0010 % |
  | frontal faces | 7 or less | 13,098, 13,099 | 95.4 %, 93.6 % | 80.9 %, 75.9 % | 0.0280 %, 0.0276 % | 0.0005 %, 0.0005 % |

  The pairs behind each row: 137, 38, 50 and 44 same-person pairs at 24 px (137, 53, 125 and 45 at 16 px), and 78,000
  to 440,000 different-person pairs a row.
- **Why 0.70.** From 0.55 to 0.70 the faces match their own person a little less often than frontal ones (1.4 and 2.4
  points less), within what 50 and 125 pairs can tell, and strangers no more often at the match score. From 0.70 to
  0.80 they match less often again, one in nine and one in twelve of them crosses 0.50 with a stranger, and only about
  half of their pairs with their own person reach the learn score: few as they are, they argue against going further.
  Beyond 0.80 LFW has 2 faces at 24 px and 5 at 16 px. The 41 faces turned 0.60 or more that fit 7 to 10 are turned
  0.69 at the median, so 0.70 keeps about half of what little data there is beyond 0.60.
- **Why 10.** No face in LFW fits worse than 11.15. The three turned 0.40 or more that fit worse than 10 (all turned
  0.60 or more, and too small to be measured at 24 px) are too few to measure; one of them matches none of its 7 pairs
  with its own person. A limit over 10 would take almost nothing more of LFW, so nothing here supports one.
- **What it takes.** Of the 112 faces the frontal rule turns away, the People detector compared 50 with a limit of 0.55
  and the fit limit of 7; with 0.70 and 10 it compares 91. Of the other 21, 19 are turned more than 0.70, one has its
  eyes closer than a fifth of its box, and one fits worse than 10. Every face turned 0.55 to 0.70 that the frontal rule
  turns away fits worse than 7, so the turn limit beyond 0.55 takes faces only because the fit is 10.
- **What it leaves as it was.** A face the frontal rule takes keeps the fit limit of 7 and no limit on its turn. With
  its head rolled, such a face can be turned 0.5 or more along its eyes: 42 of LFW's are, 20 of which fit within 7 and
  are compared, and 22 of which fit worse and are not, though with the same points upright most of them would be
  compared as turned faces.
- **The suggest band.** Of the pairs of a face turned 0.40 to 0.55 with frontal pictures of the same person, 2.2 % to
  3.4 % score from 0.40 to under 0.50 at 24 px; of its pairs with strangers, 0.024 % to 0.046 %. The People detector
  takes such a turned face, a maybe, for that person seen from further aside than their samples, and neither groups nor
  counts it (the README's **Which faces are used**); a stranger's turned face that scores there with someone goes
  uncounted too.
- **Learning.** A turned face is learned by the same rules as a frontal one, and its fit is limited only as it is for
  being compared: a learned sample of a turned face may fit up to 10. Fewer of them reach the learn score, 0.60: 71.1 %
  and 74.0 % of the pairs with their own person for those fitting 7 to 10, at 24 px (64.2 % and 63.2 % at 16 px),
  against 80.9 % (75.9 %) for frontal faces; and their pairs with strangers reach 0.50 no more often than frontal
  faces' do. Nothing here gives a reason to learn them less, and the learn rules stay as they were.
- **How thin it is.** Each row past 0.55 rests on 9 to 26 faces; a single face there moves a rate by several points.
  The pairs are LFW's posed photos, where a face turned 0.5 or more is rare and is seldom also small, blurred or seen
  from above as at a door. The turn is a measure of the five points, not of the pose: nothing here gives it in degrees.

## What the plan's rules give

```
UsableEyes = 10
LearnEyes = 18
FitLimit = 7
MatchScore = 0.40
MatchMargin = 0.20
SuggestScore = 0.30
LearnScore = 0.45
LearnMargin = 0.20
```

How each follows from the tables, by the rules of the plan:

- `UsableEyes` (px): the smallest size at which at least 8 in 10 same-person pairs reach that size's line for 1 in
  1,000 different-person pairs. Every size passes; the smallest, 10 px, has 96.3 %.
- `MatchScore`: that size's line, 0.3720, rounded up to 0.05: 0.40. At 0.40, 0.038 % of different-person pairs match
  at 10 px.
- `MatchMargin`: the smallest margin at which at most 1 in 1,000 gallery probes is recognised wrong at
  `MatchScore`. **No margin measured reaches it**; 0.20 comes nearest, with 1.36 in 1,000, and is given here. See
  Verdict.
- `SuggestScore`: the line for 1 in 100 at the usable size, 0.2974, rounded to 0.05: 0.30.
- `LearnScore`: the line for 1 in 10,000 at the usable size, 0.4348, rounded up to 0.05: 0.45.
- `LearnMargin`: the smallest margin at which at most 1 in 10,000 probes is recognised wrong at `LearnScore`. **No
  margin measured reaches it**; 0.20 comes nearest, with 7.6 in 10,000, and is given here. See Verdict.
- `LearnEyes` (px): the smallest size at which at least 8 in 10 same-person pairs reach `LearnScore` (10 px, with
  88.8 %), but no less than `UsableEyes` + 8: 18.
- `FitLimit` (crop px): the 99th percentile of the fit over the faces that matched, 6.39, rounded up to a whole
  pixel: 7.

## Verdict

PASS: 10 px between the eyes already reaches 8 in 10 (96.3 %). Three things about the values the plan's rules give,
which is why **Defaults** above departs from them:

1. **`MatchMargin` and `LearnMargin` do not meet their rule.** With 20 people in the list, no margin up to 0.20 keeps
   wrong recognitions to 1 in 1,000 at 0.40, or to 1 in 10,000 at 0.45; 0.20 is the nearest value measured, at 1.36
   in 1,000 and 7.6 in 10,000. Meeting the targets needs a higher line rather than a larger margin: 0.47 with a
   margin of 0.10 for 1 in 1,000 (96.8 % of genuine faces recognised), and about 0.54 for 1 in 10,000 (89 %).
2. **`UsableEyes` is the smallest size measured, on LFW's clean photos.** The rule would have gone lower still. A door
   camera's faces are worse than these at the same size, so 10 px is a floor that real footage is unlikely to reach.
3. **With a 1080p camera the detector, not recognition, decides.** It finds only 19 % of faces at 16 px between the
   eyes and 1 % at 12 px in such a frame, so few faces under about 24 px are ever offered to recognition there.

What recognition can and cannot be trusted with at a door camera: it can put a likely name on a clear face, frontal or
turned up to the limits for turned faces, of someone in the people list, as something to look at rather than proof. It cannot be trusted to keep strangers out:
at the defaults up to about one of every thousand faces of strangers gets an enrolled person's name even on LFW's clean
photos with a small list, and more at a real door or with a long list, so nothing that unlocks, disarms or opens should depend on it. Small, turned, blurred
or night faces will often go unrecognised, and with a 1080p camera the detector misses most faces under about 24 px
between the eyes before recognition is tried.

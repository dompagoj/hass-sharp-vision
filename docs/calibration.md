# Calibration of face recognition

How well SFace (`face_recognition_sface_2021dec.onnx`, run by `hs_vision`'s recogniser) tells the same person from
different people, by the size of the face in pixels between the eyes, measured on the public benchmark LFW. The
numbers set the People detector's recognition rules: when a face is usable, the match, suggest and learn scores,
their margins, and the fit limit. **Defaults**, after the results, has them.

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

### Reproducing it

The detections and embeddings are kept in a cache beside the LFW folder (`<lfw dir>.calibrate-cache`, about 47 MB),
so the statistics run again from it without embedding again. On 24 November 2026, on macOS arm64:

- The first run computed everything and wrote the cache. The runs from the cache after it printed the same numbers
  as the first run in every line it printed (the table of gallery lines, and the last table's layout, came later),
  and the last two printed the same output as each other byte for byte.
- `--check 500` computed 500 pictures drawn with a fixed seed again, detection and embeddings, and compared them with
  the cache: all 500 match bit for bit.

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
  frontal faces; it is a guard against turned faces, which this data cannot measure. Faces found again in the small
  pictures fit about as well as on the originals.

## Defaults

The values the People detector ships with. They are stricter than the plan's rules give (next section), for the
reasons under them:

```
UsableEyes = 16
LearnEyes = 24
FitLimit = 7
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

What recognition can and cannot be trusted with at a door camera: it can put a likely name on a clear, frontal face
of someone in the people list, as something to look at rather than proof. It cannot be trusted to keep strangers out:
at the defaults up to about one of every thousand faces of strangers gets an enrolled person's name even on LFW's clean
photos with a small list, and more at a real door or with a long list, so nothing that unlocks, disarms or opens should depend on it. Small, turned, blurred
or night faces will often go unrecognised, and with a 1080p camera the detector misses most faces under about 24 px
between the eyes before recognition is tried.

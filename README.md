# hass-sharp-vision

Face and person detection, and face recognition, for [HassSharp](https://github.com/dompagoj/hass-sharp).

These files need HassSharp 0.0.52-beta or later: the People detector keeps its recognition pictures through
HassSharp's media files (`Media`), which come with that release.

## What it is

Three node libraries for HassSharp:

- **Vision** (`nodes/Vision.cs`): detection and recognition on the native library, and **Detect faces**, a step for an
  entity definition's graph that draws the faces in a picture.
- **People** (`nodes/People.cs`): the list of people every People detector shares, and the unknown faces each one
  groups.
- **People detector** (`nodes/PeopleDetector.cs`): a device node for entity definitions that watches a Home Assistant
  camera, reports the faces and people it sees, and recognises the faces of the people you name.

They run on `hs_vision`, a Rust library in `native/` that runs three models on [tract](https://github.com/sonos/tract):
YuNet for faces and YOLOX-nano for people, which are embedded in the library, and SFace for recognition, which is a
file of its own (see [The face recognition model](#the-face-recognition-model)). HassSharp downloads the library once
from this repository's releases, checks it by its SHA-256 and keeps it with the definition's files.

With them comes an entity definition, **Front door people** (`nodes/front_door_people.json`): a People detector on
`camera.front_door`. [`hass_sharp.toml`](hass_sharp.toml) lists the four for HassSharp's **Add external**.

## Install

1. In HassSharp's panel, open **Node libraries** (or the entity definitions list) and choose **Add external**. Paste
   `https://github.com/dompagoj/hass-sharp-vision` and choose **Inspect**.
2. It lists what this repository offers: the node libraries `Vision.cs`, `People.cs` and `PeopleDetector.cs`, and the
   entity definition **Front door people**. Leave all four checked and choose **Import**. The libraries are imported
   first, together: Detect faces needs only `Vision.cs`, `People.cs` uses `Vision.cs`, and `PeopleDetector.cs` uses
   both.
3. The definition comes in turned off. Open **Front door people**, pick your camera in the **Front door** node's
   **Camera**, save it, and turn it on.

To update, run **Add external** again: a file you already have starts unchecked, and checking it offers to overwrite
it. Overwriting **Front door people** keeps the camera you picked and the other values you typed into the **Front door**
node, such as **Check every** or **Face score**, whether or not the new file sets them; a setting whose type changed
takes the file's.

### Upgrading from 1.0.x

In 1.0.x, **Front door people**'s **Front door** was a C# device node whose code derived `PeopleDetector`. In 1.1.0 it
is the People detector's own node, with the same node id and the same key, `front_door`. Run **Add external**, check
the three libraries and **Front door people**, choose to overwrite `Vision.cs`, `PeopleDetector.cs` and **Front door
people** (`People.cs` is new), and import. The device, its entities with their history, its stored data and the camera
you picked are kept: they go by the definition and the node's key, and the overwrite keeps a value typed into a setting
that the old node and the new one both have.

A definition of your own whose C# device node derives `PeopleDetector`, such as
`public class FrontDoor : PeopleDetector { }`, keeps working unchanged: the import gives its node the new settings, at
their defaults, and keeps its code and the values typed into it. It may be replaced by the People detector's node: see
HassSharp's guide, *From a C# node to the class's node*, in `docs/node-libraries.md`.

### Without Add external

The files can be pasted in by hand:

1. In HassSharp's panel, open **Node libraries** and choose **Add file**. Name it `Vision.cs`, paste in
   [`nodes/Vision.cs`](nodes/Vision.cs) and save it. Detect faces needs only this file.
2. Add `People.cs` with [`nodes/People.cs`](nodes/People.cs), then `PeopleDetector.cs` with
   [`nodes/PeopleDetector.cs`](nodes/PeopleDetector.cs), in this order: each uses the files before it.
3. In an entity definition, add the **People detector** node from the **Vision** category of the step list, choose
   its camera, and save the definition.

To update these, paste the three files from `main` in again and save them. Either way, a new library is downloaded into
a folder of its own checksum, and the one loaded before stays loaded until Home Assistant restarts.

## People detector

The People detector is a node of its own: in an entity definition's step list it is **People detector**, under
**Vision**. Drop it, choose its camera and save; there is no code to write. A C# device node whose class derives from
`PeopleDetector`, as in `public class FrontDoor : PeopleDetector { }`, works the same way, and may add entities of its
own in its constructor.

It has fourteen settings:

| Setting | Default | What it is |
| --- | --- | --- |
| Camera | none (required) | The Home Assistant camera to watch. |
| Check every | 0.5 | Seconds between checks of the camera's newest frame, from 0.1 to 60. |
| Stay on for | 10 | Seconds that Face seen and Person seen stay on after the last frame with one, from 0 to 3600; Recognised holds a name as long. |
| Face score | 0.7 | The lowest score a face counts with, from 0.1 to 1. Lower finds more, and more that are not faces. |
| Person score | 0.45 | The lowest score a person counts with, from 0.1 to 1. Lower finds more, and more that are not people. |
| Detect people | on | Whether people are looked for. Off, the person model is not run (it is still loaded, with the face model), and Person seen, People and Last person seen are unavailable. |
| Recognise faces | on | Whether the faces seen are recognised. Off, the face recognition model is not downloaded, nothing is matched or stored, the device has no page, and Recognised is unavailable. |
| Match score | 0.50 | The lowest score a face is recognised at, from 0.3 to 0.9. Higher recognises fewer faces, and fewer wrongly. The lines for suggesting and learning move with it. |
| Learn new looks | on | Whether a face recognised beyond doubt is kept as a further sample of its person. |
| Keep unknown faces for | 7 | Whole days a face nobody has named is kept after it was last seen, from 1 to 365. |
| Keep recognition pictures | off | Whether the face of each arrival on Recognised is kept for a day in Home Assistant's media folder, and named on Recognised (see [Recognition pictures](#recognition-pictures)). Every Home Assistant user can open the media folder, so it is off unless you turn it on. Off, no picture is kept, and those kept before are deleted at the next start. |
| Boxes on the camera | on | Whether the camera picture shows a box around each face, and around each person when people are detected, with its score above it. Off, neither boxes nor scores are drawn. |
| Face points on the camera | on | Whether the camera picture shows the five points of each face: its eyes, its nose and its mouth corners. |
| Names on the camera | on | Whether the camera picture shows a recognised face's name above its box, or where its box would be with **Boxes on the camera** off. Off, every face with a box shows its score. |

The device (manufacturer HassSharp, model People detector) has eight entities, in this order:

| Entity | Key | What it shows |
| --- | --- | --- |
| Camera | `camera` | Each frame checked, with the faces and people found drawn on it as the three settings on the camera say; with all three off, the frame as it is. It has no name of its own, so Home Assistant shows the device's. |
| Face seen | `face_seen` | Binary sensor (occupancy): on while a face is seen. |
| Person seen | `person_seen` | Binary sensor (occupancy): on while a person is seen. |
| Faces | `faces` | Sensor (measurement): how many faces the newest frame checked has. |
| People | `people` | Sensor (measurement): how many people the newest frame checked has. |
| Last face seen | `last_face_seen` | Timestamp: when a face was last seen. |
| Last person seen | `last_person_seen` | Timestamp: when a person was last seen. |
| Recognised | `recognised` | Sensor: who is in frame, by full name, or `Unknown`, or `Nobody` (see [Recognised](#recognised)). |

**Stay on for** is a hold. Face seen turns on at the first check that finds a face, and turns off only once
**Stay on for** seconds have passed since the last check that found one. A check that finds none within that time
leaves it on, so a frame without a face between two checks that find one does not turn it off. Person seen works the
same way. While one stays on, its last seen is written at most once a minute; when it turns off, last seen is the
time of the last check that found one. The last seen sensors keep their state across restarts; Recognised starts
unavailable ([below](#recognised)), and the others start unknown.

The device opens the camera's live feed, at most 1280 pixels wide, when the definition starts, and checks it on its
interval whether or not anyone watches the camera. Each check takes the newest frame, finds the faces at an input size
of 640 and the people at 416, recognises the faces it can (below), shows the frame with what it found on the device's
camera, and sets the entities. Measured in the Home Assistant container on one arm64 host, faces at 640 take about
29 ms and people at 416 about 35 ms. Each face recognised adds about 45 ms there, measured under load: a check with
four faces recognised took about 250 ms, with people detected too and a second detector running beside it. So with a
short **Check every** and several faces in view the detector checks less often than set.

The native library is downloaded once, the first time the definition starts, and loaded from the kept file after
that. There are builds for Linux on arm64 and x64, with musl (the Home Assistant container) and with glibc:
`linux-musl-arm64`, `linux-musl-x64`, `linux-arm64` and `linux-x64`. On another platform the definition does not
start, and says why.

A check fails when the camera gives no frame within 10 seconds, for instance because it is not there. Its first
failure for a reason is logged as an error, and each one after that at debug level. While checks fail, Faces and
People are unavailable, and Face seen and Person seen go off once their hold has passed. A camera that has never
given a frame leaves them unknown.

## Recognising faces

With **Recognise faces** on, the People detector learns who people are from you. It shows the faces it does not know
on its page, one card for each person it keeps seeing; you name each person once, and from then on every People
detector of the installation recognises them and draws their name on its camera picture.

A person has a first name and, if you give one, a last name, each at most 64 characters. Their full name, the first
name and the last name, is what the page shows, and no two people may have the same full name, whatever its case: a
new person or a rename that would make a second "Ana Horvat" is refused with "There is already a person named Ana
Horvat: choose them from the list." The camera picture draws the first name alone, and when someone else in the list
has the same first name, the first letter of the last name too: "Ivan K.". A people list stored by an earlier build
of 1.1.0, which had one name a person, keeps each name whole as the person's first name; give them a last name with
**Save** on their card if you like.

It tells automations who is there with its **Recognised** sensor ([below](#recognised)). It does not follow a face from
frame to frame: the camera picture names only the faces of the frame it shows, while Recognised holds a name for **Stay
on for**. It does not recognise anyone by their body, clothes or gait. People cannot be imported, exported or named from
uploaded photos. It is not a lock (see [Trust and privacy](#trust-and-privacy)).

### Which faces are used

A face is recognised only when it is clear enough:

- it is at least 16 pixels between the eyes, in the frame as the device checks it;
- it is not turned too far from the camera: its eyes are at least a fifth of its box's width apart, and its nose tip is
  either between them across the picture or, along the line between the eyes, no further from their midpoint than
  0.70 of the distance between them (0.5 is the nose tip in line with an eye; it measures the points, not an angle).
  So a three-quarter view is used, and so is a tilted head, measured along its own eyes; a profile is not;
- its five points (eyes, nose tip, mouth corners) line up with a face's: one that fits badly is not used. A turned face
  (its nose tip beside its eyes) may fit somewhat worse than one looking at the camera, as its points lie further from a
  frontal face's (a fit of 10 instead of 7, in the aligned crop's pixels): on the calibration's photos every face turned
  more than 0.60 fits worse than 7, and nearly every one fits within 10.

At most four faces are recognised in a check, the largest first. Every other face still counts for Face seen and Faces,
and is drawn with its score (with **Boxes on the camera** on).

A face whose nose tip is beside its eyes (a turned face) is matched as any other, with one difference: when it is only
suggested ([below](#matching)), it is taken for that person seen from further aside than their samples, and goes
nowhere. It is not recognised, and does not go to the unknown faces or count as one, unless it belongs to an unknown
group known not to be that person ([Unknown faces](#unknown-faces)), whose face it is then. A turned face that matches
no one is an unknown face like any other, so a stranger who does not look into the camera still counts. A known person
turned further than their samples reach can match no one too: their face then becomes an unknown card of its own, and
counts in `unknown_faces`, until you name it for them, which gives them that look as a sample.

### Matching

A face is compared with every sample of every person in the list. A person's score is how alike the face is to their
closest sample: 1 for the same picture, while 99 of 100 pairs of different people score under about 0.30. With **Match
score** at its default, 0.50:

- **Recognised:** the best person's score is at least 0.50 and beats the next person's by at least 0.10. The person's
  last seen (when, and at which camera) is written, at most once a minute, and their name is drawn above the face.
- **Suggested:** the best score is at least 0.40, but the face is not recognised. It goes to the unknown faces, with
  that person chosen for you to confirm; a turned face goes nowhere ([above](#which-faces-are-used)).
- **Unknown:** anything else. It goes to the unknown faces.

**Match score** moves the other two lines with it: the line for suggesting a name is a tenth under it, and the line for
learning (below) a tenth over it. At 0.60, a name is suggested from 0.50 and learned from 0.70.

A name is given to one face of a frame, the best match: another face of that frame that matches the same person goes
to the unknown faces, and is drawn with its score, not the name.

### Unknown faces

Each People detector keeps the faces it does not know in its own stored data. A face joins a group of faces that is
at least **Match score** alike to one of its samples, or starts one; two groups that come that close are joined. People
seen in the same frame are kept apart: their faces are never put on one card, however alike they are. A face seen in a
frame with a named person is never taken for that person afterwards while its card waits, and neither is a card that
shared a frame with a card you named, so two people who look alike should each be named, on their own card. A group
keeps up to 5 samples, the larger faces and the ones unlike those it has, and counts at most one sighting a check.

- A group is shown on the page from its third sighting. One that is not shown within an hour is dropped, so a passer-by
  seen once never becomes a card.
- A shown group not seen for **Keep unknown faces for** days (7 by default) is deleted. At most 50 groups are kept; the
  one seen longest ago goes first.
- When the people list changes, the groups are matched again: one that now matches a person is removed, unless it
  shared a frame with them.

### Recognised

**Recognised** says who is in frame:

- its state is the full names of the people in frame, in alphabetical order, joined by ", ": `Ivan Horvat`, or
  `Ana Horvat, Ivan Horvat`; `Unknown` when no one is recognised but a face that is not anyone's is in frame; and
  `Nobody` otherwise. Home Assistant keeps a state of at most 255 characters, so a longer one is cut at a whole name,
  with ", …" after it.
- A person is in frame from the check that recognises their face until **Stay on for** seconds (10 by default) after
  the last check that did, as Face seen is on: a name stays while the person looks away, and goes that long after they
  were last recognised. A check that fails does not end it sooner.
- A face counts as not anyone's when it was compared with the people list and given to no one: no one matched, it was
  only suggested (unless it was turned), or it was a second face of someone recognised in the same frame. A face that
  was not compared (too small, turned too far, fitting badly, or beyond the four largest) does not count, and neither
  does a turned face only suggested, so a face in a crowd or a profile does not make Recognised `Unknown`.

Its attributes:

| Attribute | What it is |
| --- | --- |
| `people` | The full names in frame, as a list, in the state's order, never cut. |
| `first_names` | Their first names, in the same order. |
| `unknown_faces` | The most faces that were not anyone's in one check within the last **Stay on for** seconds, so that it holds as the state does. |
| `last_recognised` | The full name of the person whose visit began last, kept after they leave. A visit begins when someone who was not in frame is recognised. |
| `last_recognised_at` | When that visit began. |
| `pictures` | With **Keep recognition pictures** on, the picture of each person in `people`, in its order: the path of their arrival's picture, or null for one without (its save failed, or it was pruned). An empty list with the setting off, or with nobody in frame. |
| `last_recognised_picture` | The path of the picture of `last_recognised`'s arrival, kept after they leave and across a restart while its file is there; null when there is none. |
| `unknown_picture` | The path of the picture of the unknown arrival, while `unknown_faces` is above 0; null otherwise. |

Recognised changes, and is written to Home Assistant's history, when someone arrives or leaves, or a count, a name or a
picture changes: not at every check. A rename on the page, on this detector or another, shows at once, and a person
deleted or merged away goes from the state, from `last_recognised` and from the pictures. It is unavailable with
**Recognise faces** off, and while the face recognition model has not opened (it is still downloading, or failed to
open and is tried again): a face no one could compare is never `Unknown`, so an automation for strangers does not fire
for the family then. After a restart it is unavailable until the model has opened and a check has looked with it, and
then says what that check found: `Nobody`, unless someone is in frame. A camera that gives no frame after the start
leaves it unavailable, as no check looks until one comes. `last_recognised` and its time come back, from the detector's
own stored data, with `last_recognised_picture` while its file is there. A change from unavailable is not a change from
`Nobody`, so an automation that triggers on a change from `Nobody` does not fire for a restart.

Two automations, for a detector whose Recognised is `sensor.front_door_recognised` (Home Assistant names it after the
device, so **Front door people**'s is that). The first sends a notification when someone comes to the door; the second
turns on the hall light whenever Recognised changes while Ana Horvat is at the door:

```yaml
- alias: Someone at the door
  triggers:
    - trigger: state
      entity_id: sensor.front_door_recognised
      from: Nobody
  conditions:
    - condition: template
      value_template: "{{ trigger.to_state.state not in ['unavailable', 'unknown'] }}"
  actions:
    - action: notify.notify
      data:
        message: "At the door: {{ trigger.to_state.state }}"

- alias: Hall light for Ana
  triggers:
    - trigger: state
      entity_id: sensor.front_door_recognised
  conditions:
    - condition: template
      value_template: "{{ 'Ana Horvat' in (state_attr('sensor.front_door_recognised', 'people') or []) }}"
  actions:
    - action: light.turn_on
      target:
        entity_id: light.hall
```

A state trigger with neither `from` nor `to` fires at each change of the state or an attribute, so the second also fires
when someone else arrives or leaves while Ana is there; turning on a light that is on does nothing. It fires too when
Recognised becomes available after a restart, and its condition turns the light on then only if Ana is at the door. Do
not let Recognised unlock, disarm or open anything (see [Trust and privacy](#trust-and-privacy)).

### Recognition pictures

With **Keep recognition pictures** on, the detector keeps the face of each arrival as a picture, and Recognised says
where it is, so that a phone notification can show who came. It needs HassSharp 0.0.52-beta or later.

- **An arrival** is a person entering Recognised: the check that begins their visit and sets `last_recognised_at`. Its
  picture is the crop of the face that check gave them, as the page shows a face (the face's box grown by a quarter on
  each side, 160 pixels on its long side, a JPEG). A person has one picture a visit: the checks after keep none, and
  **Stay on for** holds their picture with them in `pictures`.
- **An unknown arrival** is `unknown_faces` rising from 0. Its picture is the crop of the largest face that check
  compared and gave to no one, named in `unknown_picture` while unknown faces are in frame. Once they have gone, the
  next unknown face is an arrival of its own, with a new picture.
- The file is on disk before the state that names it is sent to Home Assistant, and the path never changes: a picture
  is never written over, so the path of an arrival always shows that arrival's face.
- The pictures are in Home Assistant's media folder (its `media_dirs` entry `local`, else the first by name), under
  `hass_sharp/<definition id>/<node key>/`, each named by the time of its check in UTC and 16 random hex digits, such
  as `20261124T081502-3f9a0c1d2e4b5a69.jpg`: never by anything of the person. Home Assistant serves each at the path
  the attributes give, such as
  `/media/local/hass_sharp/front_door_people/front_door/20261124T081502-3f9a0c1d2e4b5a69.jpg`, to anyone logged in.
- A detector keeps at most 20 pictures, and none older than a day: at each check and at each start, the older ones and
  those beyond the 20 newest are deleted, and an attribute that named one becomes null before its file goes. A person
  deleted or merged away, on this detector or another, loses their pictures on every detector; the person they were
  merged into keeps their own. With the setting off, or **Recognise faces** off, every picture is deleted at the next
  start, and deleting the definition deletes them too.
- A picture that cannot be saved (Home Assistant has no media folder, or it cannot be written) leaves recognition as it
  was: the arrival is announced with a null picture, and the failure is logged once as a warning, without a name,
  until a picture is saved again.

**Who can see them.** Every user who can log in to Home Assistant can open the media folder in the **Media** browser
and see every picture there, and so can anything that has a user's login, such as a phone with the companion app. That
is why the setting is off unless you turn it on. A backup that includes the media folder holds the pictures kept then.

Two automations that send a phone notification with the face, for a detector whose Recognised is
`sensor.front_door_recognised` and a phone whose notify action is `notify.mobile_app_my_phone`. The first is for a
person recognised, the second for an unknown face; each has a button that opens a dashboard of your own:

```yaml
- alias: Who is at the door
  triggers:
    - trigger: state
      entity_id: sensor.front_door_recognised
  conditions:
    - condition: template
      value_template: >-
        {{ trigger.from_state.state not in ['unavailable', 'unknown']
           and trigger.to_state.attributes.last_recognised_picture is not none
           and trigger.to_state.attributes.last_recognised_at != trigger.from_state.attributes.last_recognised_at }}
  actions:
    - action: notify.mobile_app_my_phone
      data:
        message: "{{ trigger.to_state.attributes.last_recognised }} is at the door"
        data:
          image: "{{ trigger.to_state.attributes.last_recognised_picture }}"
          actions:
            - action: URI
              title: Open the door camera
              uri: /lovelace/door

- alias: A stranger at the door
  triggers:
    - trigger: state
      entity_id: sensor.front_door_recognised
  conditions:
    - condition: template
      value_template: >-
        {{ trigger.from_state.state not in ['unavailable', 'unknown']
           and trigger.to_state.attributes.unknown_picture is not none
           and trigger.to_state.attributes.unknown_picture != trigger.from_state.attributes.unknown_picture }}
  actions:
    - action: notify.mobile_app_my_phone
      data:
        message: Someone the door camera does not know is at the door
        data:
          image: "{{ trigger.to_state.attributes.unknown_picture }}"
          actions:
            - action: URI
              title: Open the door camera
              uri: /lovelace/door
```

Each fires once an arrival: `last_recognised_at` changes only when a visit begins, and `unknown_picture` only when
unknown faces arrive; the first condition keeps a restart from firing them. Read the picture from the trigger
(`trigger.to_state`), not with `state_attr`: the trigger's state is the one of that arrival and never changes, while
the sensor's may already name the next person by the time the action runs, as when two people arrive one after the
other. The picture at that path is that arrival's face for as long as it is kept; the phone loads it when it shows the
notification, with its own login, so a notification opened more than a day later, or after 20 more arrivals, may show
no face.

### The page

The page is in the definition's editor, under **Device** in the bottom drawer. Only Home Assistant administrators see
it. It has two lists, and a third when asked:

- **Unknown faces**, which wait for you (their number is on the node's card and the definition's): one card for each
  shown group, in the order they were first shown, with its largest face, how often and when it was seen, and
  "Might be …" for a suggested person. A card keeps its place among the others for as long as it is there, and a new
  card is always added at the end; one that goes leaves the rest in their order. To name the face, press **Person**: a
  dialog lists the people you have named, each with their face, how many samples they have and when they were last
  seen, and a box to search them by name. Choose one with a click, or with the arrow keys and Enter; or choose **+ New
  person** under the list, fill in **First name** (and **Last name** if you like), and press **Use**. Then press
  **Save**: the group's samples go to the person you chose, or make the new person; a person with 20 named samples
  keeps the most different ([below](#how-many-samples-a-person-keeps)). A suggested person is chosen already, so
  **Save** alone accepts the suggestion. **Dismiss** deletes the group.

  When the card's faces look very unlike the person you chose, as a stranger's do (none of them comes 0.40 close to
  any of that person's samples, named or learned), **Save** saves nothing the first time, and the card says: "This
  face does not look like Ana Horvat's samples. Press Save again to add it to Ana Horvat anyway." Press **Save** again
  within 2 minutes, with the same person chosen, to save it as usual. Choosing someone else, waiting longer, or the
  card taking another face meanwhile asks again. A new person is never asked about, and at the default **Match
  score** neither is the person a card suggests. Their own face seldom looks that unlike them: a new look, such as
  sunglasses or a turned head, may be asked about, and one more **Save** adds it.
- **People**, by full name, each with their newest named face, how many samples they have (and how many learned), and
  when and at which camera they were last seen. **Save** renames them to the **First name** and **Last name** on their
  card. To merge two cards of one person, press **Merge into** on the card to remove, choose the person to keep in its
  dialog, and press **Merge**: after asking, it moves the samples to the person chosen, as many as they keep, and
  removes the card's person. **Merge into** and **Merge** are there when there is someone else to merge into.
  **Samples** shows the third list. **Delete** removes the person and their samples, after asking.
- **Samples of …**, one card for each sample of the person chosen, named first, then learned, the newest first.
  **Remove** deletes a sample; the last sample a person was named with cannot be removed (delete the person instead).
  **Close** on the first card closes the list, and it closes when the person is deleted.

A change made on one detector's page shows on every other detector's page within a few seconds, in its lists and in
the people its dialogs offer. A person deleted or merged away on another detector while a dialog offers them goes from
the dialog, and a **Save** or **Merge** that still names them does nothing and shows the page as it is now.

### Learning

With **Learn new looks** on, a recognised face is kept as a **learned** sample of its person when all of these hold:

- its score is at least the learn line (0.60 at the default **Match score**) and beats the next person's by at least
  0.15;
- it is at least **Match score** alike to one of the samples you named the person with, so a learned sample is never
  the only reason another is learned;
- it is at least 24 pixels between the eyes;
- it adds something: it is less than 0.9 alike to every sample the person has;
- the person gained or lost no learned sample in the last 10 minutes.

A turned face ([above](#which-faces-are-used)) is learned by the same rules, so a person can gain a look from aside.

A person has at most 20 learned samples: when they have 20, a new one replaces the oldest learned one. A learned
sample can be wrong: the rules make it rare, not impossible. Learned samples are marked on the page and can be removed
one by one. A look that was removed can be learned again while it keeps being seen, 10 minutes after the removal at the
earliest; to stop that, turn **Learn new looks** off, which stops all learning.

### How many samples a person keeps

A person has at most 20 samples you named them with and 20 learned ones, and the list at most 200 people. Naming a face
for a person who has 20 named samples is never refused: they keep the 20 most different from each other.

- A new look, such as their face in sunglasses, takes the place of one of the two samples most alike to each other: of
  those two, the one more alike to the rest, and on a tie the older. The one that goes is deleted with its picture.
- A look they have already, one at least as alike to one of their samples as those two are to each other, is not
  stored. The card goes all the same, and the naming counts as a sighting.
- Each face of a card is judged in turn, against the samples as the one before left them. A merge into the person
  judges the merged person's named samples the same way, the newest first.
- Learned samples take no part, and stay as they are.

A face named for the wrong person is unlike their others, so it is kept. **Save** asks first when it looks very unlike
them ([The page](#the-page)); one saved all the same is removed in **Samples of …**.

## Trust and privacy

**Recognition is a convenience. Do not let it unlock, disarm or open anything.** A stranger can be given the name of
someone in the list, and someone in it can go unrecognised.

[`docs/calibration.md`](docs/calibration.md) measured SFace on LFW, a public set of 13,233 photos of 5,749 people,
shrunk to door-camera sizes. Its numbers are an upper bound: those are posed, well-lit press photos, and a door camera's
faces are worse. By the size of the face:

| Pixels between the eyes | A face compared with another of the same person, median score | Score that 1 in 10,000 pairs of different people reach | Faces the detector finds in a 1080p frame |
| --- | --- | --- | --- |
| 40 | 0.69 | 0.4177 | 99.6 % |
| 32 | 0.69 | 0.4189 | 96.7 % |
| 24 | 0.68 | 0.4213 | 82.9 % |
| 20 | 0.68 | 0.4237 | 55.0 % |
| 16 | 0.66 | 0.4214 | 19.3 % |

A smaller face does not make strangers look more alike; it makes the same person look less alike. But a face at the
door is compared with every sample of everyone in the list, so a stranger has many chances to score high with someone.
In lists of 20 people with 3 samples each, with faces 10 pixels between the eyes, 0.47 with a margin of 0.10 was where
wrong names fell to 1 in 1,000 faces, nearly all of them strangers', while 96.8 % of the faces of people in the list
were recognised. **Match score**'s default, 0.50, leaves room above that. In the calibration's own words: at the
defaults up to about one of every thousand faces of strangers gets an enrolled person's name even on LFW's clean
photos with a small list, and more at a real door or with a long list. Small, turned, blurred or night faces will
often go unrecognised, and with a 1080p camera the detector misses most faces under about 24 pixels between the eyes
before recognition is tried.

People who look alike, relatives above all, score closer than the calibration's strangers: in this repository's own
test photo, two different men score 0.52 to 0.56 at about 18 pixels between the eyes, above the default **Match
score**.

Turned faces were measured on the same photos: the 76 faces of 67 people that the turn limit of 0.70 and the fit of 10
let in beside the frontal ones matched their own person as often as frontal faces did (96.9 % of pairs against 95.4 %,
at 24 pixels between the eyes), and strangers' as seldom. But LFW has few of them, and fewer the further they are
turned: 16 faces of 15 people turned 0.55 to 0.70, 9 turned 0.70 to 0.80 (which already matched less often), and almost
none beyond. Nothing in it says how a face seen from above or in the dark does turned. Two strangers turned alike are a
little more alike than two frontal ones, so a person named from a turned card is likelier to be suggested for a
stranger turned the same way.

Faces, their crops, their embeddings and the names you give them are biometric and personal data:

- They are kept only in Home Assistant's storage, under `.storage/hass_sharp/data/`, and, with **Keep recognition
  pictures** on, the faces of the last arrivals in its media folder ([Recognition pictures](#recognition-pictures)),
  which every Home Assistant user can open. They are never sent anywhere. No log line holds a name, a face or an
  embedding.
- Stored data is part of Home Assistant's backups, so every backup holds the people list as it was then, and a backup
  kept off the machine carries it with it.
- A name drawn on the camera picture is seen by every Home Assistant user who can see that camera, and by whatever the
  camera is shown on or shared with. **Names on the camera** turns the names off.
- Recognised's state and attributes hold the names of the people at the camera: every Home Assistant user sees them,
  and the recorder keeps them in its history, and the logbook shows them, for as long as it keeps history. To keep them
  out of the history, exclude the sensor in the recorder's settings.
- Tell the people who live in or visit the home that their faces are recognised and kept.

To delete them:

- a person, with their samples: **Delete** on their card;
- one sample: **Remove** in **Samples of …**;
- an unknown face: **Dismiss** on its card;
- the whole people list: HassSharp's **Stored data** dialog, in the **Entities** page's header, deletes the shared
  store `hass-sharp-vision.people`. It refuses while a running definition has the store open, so turn off every
  definition with a People detector first;
- a detector's unknown faces: they are its node's own data, deleted with its definition, or when the definition is
  saved without a node of that key;
- a detector's recognition pictures: turn **Keep recognition pictures** off and save the definition, which deletes them
  as it starts; a person's go when they are deleted. An administrator can also delete one in the **Media** browser.

## The face recognition model

Recognition runs SFace, `face_recognition_sface_2021dec.onnx`, from the OpenCV model zoo's `face_recognition_sface`:
SFace is contributed by Yaoyao Zhong, and its ONNX conversion is by Chengrui Wang. It is under the Apache License 2.0,
whose text is [`native/model/LICENSE-sface`](native/model/LICENSE-sface), the zoo's licence text as it ships it with the
model. The zoo does not state the data the model was trained on.

The model is 38.7 MB (38,696,353 bytes) and is used unmodified. It is not embedded in the library and not committed
here: each release carries it beside the libraries, with its licence. A People detector with **Recognise faces** on
downloads it once, in the background after the definition has started, checks it by its SHA-256
(`0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79`) and keeps it with the definition's files. Until it
has opened, faces are counted and drawn but not recognised, and Recognised is unavailable; if it cannot be downloaded
or opened, the log says why, once for each reason, and it is tried again on a check at most every 10 minutes. With
**Recognise faces** off, and for Detect faces, it is never downloaded.

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
two embedded models' licences. Run it after a dependency changes: CI fails when the file is not current.

`native/fetch-model.sh` downloads the face recognition model into
`native/model/face_recognition_sface_2021dec.onnx`, from the OpenCV zoo's Hugging Face mirror, unless a file with its
pinned SHA-256 is there already. A download with another SHA-256 fails and is removed. The file is ignored by git.

## Testing

The crate's tests, from `native/`, with the toolchain `native/rust-toolchain.toml` names (`rustup toolchain install`),
after `native/fetch-model.sh`:

```sh
cargo test --release --locked
```

The C# tests build `nodes/Vision.cs`, `nodes/People.cs` and `nodes/PeopleDetector.cs` against a hass-sharp checkout
that has library classes as nodes, with every file of its runtime suite:

```sh
dotnet run --project tests/Vision.Tests --disable-build-servers -p:HassSharpRoot=/path/to/hass-sharp -- --vision --people --people-detector --vision-manifest
```

`HASS_SHARP_ROOT` may name the checkout instead of `-p:HassSharpRoot`. The four suites:

- `--vision`: the tests of `Vision.cs`: the download, detection, drawing, and the recogniser.
- `--people`: those of `People.cs`: the people list, the matcher, the unknown-face groups and the learning rule, over
  device data in a temporary directory, with made-up embeddings.
- `--people-detector`: those of `PeopleDetector.cs`, with made-up embeddings, and its own node with the real model.
- `--vision-manifest`: imports the three libraries as **Add external** does, checks the People detector's node, and
  previews `nodes/front_door_people.json`.

The tests need the .NET 11 SDK, Python 3 (the fake ffmpeg of the runtime suite is a Python script) and libjpeg-turbo.

The tests that run the detector load a local build of the library: `HS_VISION_LIBRARY` when it is set (the file must
exist), else `native/dist/libhs_vision-<rid>.so` from `build.sh`, else `native/target/release/libhs_vision.dylib` or
`.so` from `cargo build --release --lib` in `native/`. The recogniser's tests also need the SFace model:
`HS_VISION_MODEL` when it is set (the file must exist), else `native/model/face_recognition_sface_2021dec.onnx` from
`native/fetch-model.sh`. Without them they are skipped, unless `HS_VISION_REQUIRE_LIBRARY=1`, which fails them
instead, as CI does.

The pins and the manifest, from the repository root:

```sh
HASS_SHARP_ROOT=/path/to/hass-sharp python3 -m unittest discover -s tests
```

They check that the table in `Vision.cs` is `native/SHA256SUMS`, that its `Source` is the release of
`native/Cargo.toml`'s version, that its model's name and SHA-256 are those `native/fetch-model.sh` fetches, and that
the first line of each node file names that version. With `HS_VISION_CHECK_RELEASE=1`, as in CI, they also compare that
release's `SHA256SUMS` with `native/SHA256SUMS`, once it is released. With `HASS_SHARP_ROOT` set to a hass-sharp
checkout, they also read `hass_sharp.toml` and the files it lists by the rules of that checkout's
`custom_components/hass_sharp/external.py`; without it, that test is skipped.

## Releasing

A change to a node file alone needs no release: users import the files, or copy them, from `main`. A new setting of
`PeopleDetector` needs a default: a library save is refused while a definition has its node without a value for a
setting that has none. A change to the library or the model is released as a new version.

1. **HassSharp first.** The C# job of `ci.yaml` builds against the hass-sharp commit `HASS_SHARP_REF` names, by its full
   sha: the commit of the HassSharp release these files need, which the top of this README names ("HassSharp
   0.0.52-beta or later"), by its tag's commit, or a later commit of hass-sharp's `main` when only the tests need
   something newer. When the files come to need something of a later HassSharp, release that first, then set
   `HASS_SHARP_REF` to its tag's commit and name its version at the top.
2. Set the new version in `native/Cargo.toml`, and in `Cargo.lock` with `cargo update -p hs_vision --offline` in
   `native/`. If a dependency changed, run `native/build.sh --licenses` too.
3. Run `native/build.sh` for all four RIDs and copy `native/dist/SHA256SUMS` into `native/SHA256SUMS`. In `Vision.cs`,
   set `Source` to `https://github.com/dompagoj/hass-sharp-vision/releases/download/v<version>/` and the pinned table
   (between `// pinned:begin` and `// pinned:end`) to those checksums; `ModelName` and `ModelSha256` stay those of
   `native/fetch-model.sh`. In every node file, set the version in the first line's comment.
   `python3 -m unittest discover -s tests` checks all of these. Commit, push, and wait for CI to pass.
4. The owner tags that commit `v<version>` and pushes the tag.
5. `release.yaml` runs every test of `ci.yaml`, checks that the tag is the crate's version and is not released yet,
   builds each RID on a runner of its own architecture, and creates the release with the four libraries,
   `SHA256SUMS`, `NOTICE` and `THIRD-PARTY-LICENSES`, and the model, `face_recognition_sface_2021dec.onnx`, fetched and
   checked by `native/fetch-model.sh`, with `LICENSE-sface`. It compares the release's `SHA256SUMS` with
   `native/SHA256SUMS` and says whether they differ.
6. If they differ, copy the release's `SHA256SUMS` into `native/SHA256SUMS` and its checksums into `Vision.cs`'s
   table, and commit that: until then, a library downloaded from the release fails `Vision.cs`'s checksum check.

So the order is HassSharp's release, then `HASS_SHARP_REF`, then the tag. A release is never uploaded to again: a
rebuild is a new version.

## Licence

MIT; see [`LICENSE`](LICENSE). [`native/NOTICE`](native/NOTICE) names the models, the linked crates and the test
photos, which are not this repository's own, and their licences.
[`native/THIRD-PARTY-LICENSES`](native/THIRD-PARTY-LICENSES) has the licence texts of the linked crates and the
embedded models; [`native/model/LICENSE-sface`](native/model/LICENSE-sface) has the face recognition model's.

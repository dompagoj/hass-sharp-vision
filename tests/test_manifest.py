"""hass_sharp.toml, read by the rules HassSharp's Add external imports it with: hass-sharp's
custom_components/hass_sharp/external.py, loaded by its path from the checkout HASS_SHARP_ROOT names (it uses the
standard library only). Without HASS_SHARP_ROOT the test is skipped."""

import importlib.util
import os
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "hass_sharp.toml"
# The People detector's own node, of the library's id in hass_sharp.toml.
PEOPLE_DETECTOR = "library:people_detector:global%3A%3ANodeLib.PeopleDetector"
CAMERA = {"kind": "literal", "type": "entity_id:camera", "value": "camera.front_door"}


def external():
    """hass-sharp's external.py, or None when HASS_SHARP_ROOT is unset."""
    checkout = os.environ.get("HASS_SHARP_ROOT")
    if not checkout:
        return None
    spec = importlib.util.spec_from_file_location("hass_sharp_external", Path(checkout) / "custom_components" / "hass_sharp" / "external.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


x = external()


@unittest.skipIf(x is None, "set HASS_SHARP_ROOT to a hass-sharp checkout")
class ManifestTest(unittest.TestCase):
    def test_repository_manifest(self):
        items = x.parse_manifest(MANIFEST.read_bytes())
        self.assertEqual(items, [
            x.Item("library", "Vision.cs", "nodes/Vision.cs",
                   "Face and person detection on a native library (YuNet and YOLOX-nano on tract), face recognition with SFace, "
                   "and the Detect faces node", "vision"),
            x.Item("library", "People.cs", "nodes/People.cs",
                   "The people list every People detector shares, and the unknown faces each one groups", "people"),
            x.Item("library", "PeopleDetector.cs", "nodes/PeopleDetector.cs",
                   "People detector: a device node that watches a camera, reports faces and people seen, and recognises faces",
                   "people_detector"),
            x.Item("entity", "Front door people", "nodes/front_door_people.json", "A People detector on camera.front_door", None),
        ])

        files = {}
        for item in items:
            with self.subTest(item.path):
                self.assertIsNone(x.check_path(item.path, item.kind))
                path = ROOT / item.path
                self.assertTrue(path.is_file(), f"{item.path} exists")
                files[item.path] = path.read_bytes()
                self.assertLessEqual(len(files[item.path]), x.CAP, f"{item.path} is at most {x.CAP} bytes")

        vision, people, detector, front_door = items
        self.assertIn("public static class FaceDetection", x.library_source(vision, files[vision.path]))
        self.assertIn("public sealed class PeopleList", x.library_source(people, files[people.path]))
        detector_source = x.library_source(detector, files[detector.path])
        self.assertIn('[Node("People detector", Category = "Vision")]\npublic class PeopleDetector : SharpDevice', detector_source)
        self.assertIn("    public PeopleDetector()", detector_source)

        body = x.entity_body(front_door, files[front_door.path])
        self.assertEqual(body["name"], "Front door people")
        self.assertEqual(body["settings"], [])
        graph = body["logic"]["graph"]
        self.assertEqual(graph["name"], "Front door people")
        self.assertEqual(graph["edges"], [])
        [node] = graph["nodes"]
        # The People detector's own node, of the library's id in the manifest; no code and no signature.
        self.assertEqual(node["id"], "front_door")
        self.assertEqual(node["kind"], PEOPLE_DETECTOR)
        self.assertEqual(node["config"], {"key": "front_door", "name": "Front door", "arguments": {"Camera": CAMERA}})
        # The import rewrites the kind to the id the library is stored under.
        [moved] = x.rewrite_library_kinds(graph, {"vision": "x", "people_detector": "library-1234"})["nodes"]
        self.assertEqual(moved["kind"], "library:library-1234:global%3A%3ANodeLib.PeopleDetector")

    def test_overwriting_front_door_people_of_1_0_keeps_what_was_typed(self):
        """A Front door people imported from 1.0.x has the C# device node front_door deriving PeopleDetector; an
        overwrite with this file's node of the class keeps the camera and the score typed into it."""
        items = x.parse_manifest(MANIFEST.read_bytes())
        front_door = next(item for item in items if item.kind == "entity")
        graph = x.entity_body(front_door, (ROOT / front_door.path).read_bytes())["logic"]["graph"]
        settings = [
            {"key": "Camera", "label": "Camera", "description": "The camera to watch.", "type": "entity", "secret": False,
             "domain": "camera", "required": True},
            {"key": "FaceScore", "label": "Face score", "description": "", "type": "number", "secret": False, "minimum": 0.1,
             "maximum": 1},
        ]
        typed = {"Camera": {**CAMERA, "value": "camera.porch"}, "FaceScore": {"kind": "literal", "type": "number", "value": 0.8}}
        one_zero = {"nodes": [{"id": "front_door", "kind": "csharp_device", "position": {"x": 60, "y": 100}, "config": {
            "key": "front_door", "name": "Front door", "source": "public class FrontDoorPeople : PeopleDetector { }\n",
            "signature": {"title": "Front door people", "description": "", "domain": None, "settings": settings},
            "arguments": typed}}], "edges": []}
        # The catalog after the import, with the class's node of the library stored under people_detector.
        catalog = {"nodes": {PEOPLE_DETECTOR: {"title": "People detector", "declares": "device",
                                               "classSignature": {"title": "People detector", "settings": settings}}}}
        [kept] = x.keep_device_inputs(graph, one_zero, catalog)["nodes"]
        self.assertEqual(kept["kind"], PEOPLE_DETECTOR)
        self.assertEqual(kept["config"]["key"], "front_door")
        self.assertEqual(kept["config"]["arguments"], typed)


if __name__ == "__main__":
    unittest.main()

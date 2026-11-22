# hass-sharp-vision

Node libraries for [HassSharp](https://github.com/dompagoj/hass-sharp): face and person detection, on a Rust
library (`hs_vision`) built on [tract](https://github.com/sonos/tract).

## Layout

```
LICENSE  README.md  hass_sharp.toml  .gitignore
native/                 the hs_vision crate (Cargo.toml, src, model, tests, NOTICE, build.sh, SHA256SUMS, licence texts)
nodes/                  Vision.cs, PeopleDetector.cs, front_door_people.json
tests/                  the C# test project and the Python pin check
.github/workflows/      ci.yaml, release.yaml
```

Licence: MIT (LICENSE). native/NOTICE lists what the native library embeds and links.

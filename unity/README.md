# Unity phased-array output console

A Unity front end for the 8-TX / 1-RX ultrasonic platform in this repository.
It draws the transmit beam the hardware actually forms, replays the recorded
angle/range sessions under [`results/distance/`](../results/distance) as a
sector image, and talks to the ESP32 dashboard over HTTP.

Everything is code. There are no scenes, prefabs, materials or shaders to wire
up: one component builds its own textures and draws through IMGUI, so it behaves
identically in the built-in pipeline, URP and HDRP.

## What this is, and what it is not

This is a **visualisation and forward-modelling tool**. It is honest about the
platform it is attached to:

- The beam it simulates is **transmit-only**. There is one receiver, so there is
  no receive beamforming to fold in, and the tool never pretends otherwise.
- The simulated field is a Huygens sum of eight elements with cylindrical
  spreading and element directivity. It does **not** model absorption,
  reflection, multipath, the analogue front end or the envelope detector.
- Recorded frames are **mean envelope ADC counts**, not calibrated
  reflectivities. The scene labels in the legacy exports are known to be
  unreliable; see [`results/README.md`](../results/README.md).
- A live device link shows telemetry. Neither firmware sketch streams a full
  angle-by-range image, so the sector panel stays sparse on a live link.

## Quick start

**Option A — open this folder as a project.** Unity 2021.3 LTS or newer.
`File > Open Project`, point it at `unity/`, let it generate the rest of
`Library/` and `ProjectSettings/`. Then:

1. `Ultrasound > Create Phased Array Console` in the menu bar.
2. Press play.

**Option B — drop it into an existing project.** Copy
`unity/Assets/UltrasoundPhasedArray` and `unity/Assets/StreamingAssets/ultrasound`
into that project's `Assets/`, then follow the same two steps. The only package
dependency beyond built-in modules is `com.unity.test-framework`, and that is
only needed to run the tests.

Adding the component by hand works too: create an empty GameObject and add
**Phased Array Console**. No camera, canvas or light is required.

## The four panels

| Panel | Shows |
|---|---|
| **Acoustic field** | The 2-D pressure field in front of the aperture. *Wavefront* animates the finite tone burst leaving the array; *Beam map* shows the steady-state magnitude. Range rings, the steered ray, grating-lobe rays and the far-field boundary are drawn over it. |
| **Transmit beam pattern** | Array factor in dB against observation angle, with the scanned sector shaded. The faint trace is the nominal eight-element pattern, so a sub-aperture or a dead channel shows up as a gap. |
| **Sector image** | The angle/range frame scan-converted into a B-mode sector, with log compression, gain, dynamic range and colour map controls. |
| **Transmit channels** | The delay schedule per element: the commanded delay as a bar, the delay actually emitted on the ESP32 tick grid as a tick, plus per-channel gain and any live echo readings. |

## Three sources

**Simulate** runs the forward model. Place virtual reflectors by bearing and
range and see the frame the platform would record for them, including the
uncorrected range bias if you want it.

**Recording** replays a baked session. Two are included, baked from
`results/distance/`:

- `reflector_50cm.uscan` — the 50 cm session, 31 angles x 101 bins, six frames.
- `reflector_84_5cm.uscan` — the 84.5 cm session, 31 angles x 171 bins, six frames.

Each file holds `TARGET` and `BG` acquisitions for `FULL8`, `LEFT4` and `RIGHT4`.
Background subtraction and removal of the measured range bias are toggles.

**Live** polls the ESP32 dashboard. It recognises both firmware sketches from
their JSON payloads:

| Firmware | Endpoint used | What arrives |
|---|---|---|
| `html_logging.ino` | `/api/data`, `/api/scan` | Eight per-transmitter echo results and a summary |
| `ultrasonic_shape_rudder.ino` | `/api/data`, `/api/scan`, `/api/sweep`, `/api/set?angle=` | One result per commanded angle, accumulated into an angular profile |

Set the dashboard address to whatever the serial monitor printed.

## Where the numbers come from

The physics defaults are lifted from `ultrasonic_shape_rudder.ino` so the
simulated beam matches what the firmware fires:

| Quantity | Value | Firmware symbol |
|---|---|---|
| Element positions | -59.5 to +59.5 mm, 17 mm pitch | `TX_X_MM` |
| Sound speed | 0.34675 mm/µs | `SOUND_SPEED_MM_PER_US` |
| Carrier | 40 kHz | `PERIOD_US` = 25 µs |
| Delay grid | 240 MHz | `ticksPerUs` |
| Per-channel trim | 0 µs | `CHANNEL_TRIM_US` |

`BeamPlan.Compute` is a direct port of `prepareBeam()`, including the shift that
makes the earliest channel fire at t = 0 and the rounding onto the CPU tick grid.

The range axis matches the exports exactly: 775 µs maps to 13.4366 cm and the
bins step by 50 µs, which is 0.867 cm two-way.

## What the tool makes visible

These fall straight out of the geometry, and the console shows each of them:

- **The pitch is about two wavelengths.** 17 mm against a 8.67 mm wavelength is
  1.96 λ, so grating lobes are unavoidable — at broadside they sit at ±30.7°.
- **At the edge of the scan they move inside it.** Steering to +15° puts a
  full-amplitude second beam at **-14.5°**, inside the ±15° sector that the
  recorded sessions sweep. A reflector at +20°, outside the sweep entirely,
  produces its strongest response near -10°. Bearing, not range, is what fails.
- **50 cm is barely far field.** The Rayleigh distance is 40.8 cm, so the
  far-field pattern only just applies at the audited 50.0 cm target.
- **The burst is long compared with the ranges of interest.** 24 cycles is
  600 µs, which is 10.4 cm of two-way range — the axial extent of every echo.
- **Delay quantisation is not the problem.** A 240 MHz tick grid holds the worst
  channel error to 0.002 µs, which is 0.03° of carrier phase.

This addresses item 7 of the processing roadmap in the top-level README:
quantify the grating lobes rather than smoothing them away.

## Baking your own sessions

Either use the editor, **Ultrasound > Bake angle-range CSV to .uscan...**, which
reads the CSV and writes into `StreamingAssets/ultrasound` directly, or the
Python tool:

```bash
python3 unity/Tools/bake_scan.py results/distance/70cm.csv \
    --out unity/Assets/StreamingAssets/ultrasound/reflector_70cm.uscan \
    --label "70 cm session" --max-range-cm 160
```

Then run **Ultrasound > Refresh dataset manifest** so the console lists it.
The container format is documented in [`Tools/uscan_format.md`](Tools/uscan_format.md).

Raw CSVs can also be loaded at runtime through `CsvScanImporter`, which parses
on a background thread; the largest export is over half a million rows.

## Tests

35 edit-mode tests under `Assets/UltrasoundPhasedArray/Tests`, run from
`Window > General > Test Runner`:

- `BeamPhysicsTests` checks the delay schedule, the array factor, the grating
  lobe formula and the beamwidth against values derived independently from the
  array geometry.
- `ScanDataTests` covers the `.uscan` round trip, the CSV importer, background
  subtraction, bilinear sampling and both firmware JSON payloads.
- `SimulationTests` covers the forward model and the acoustic field, including
  the out-of-sector aliasing case.

## File map

```
unity/
├── Assets/
│   ├── StreamingAssets/ultrasound/    baked .uscan datasets and the manifest
│   └── UltrasoundPhasedArray/
│       ├── Runtime/
│       │   ├── ArrayConfig.cs         geometry and acoustics, firmware defaults
│       │   ├── BeamPlan.cs            port of prepareBeam(), incl. tick quantisation
│       │   ├── ArrayFactor.cs         far-field pattern, grating lobes, beamwidth
│       │   ├── AcousticField.cs       Huygens field, instantaneous and steady state
│       │   ├── ScanFrame.cs           angle x range frames and libraries
│       │   ├── ScanConverter.cs       polar to cartesian sector conversion
│       │   ├── Colormap.cs            colour tables
│       │   ├── TexturePainter.cs      CPU rasteriser
│       │   ├── PhasedArrayConsole*.cs the component and its control column
│       │   ├── Panels/                the four instrument panels
│       │   └── Sources/               .uscan, CSV, forward model, HTTP link, JSON
│       ├── Editor/                    menu items and the CSV baker
│       └── Tests/                     edit-mode tests
├── Packages/manifest.json
└── Tools/                             bake_scan.py and the format spec
```

`.meta` files are deliberately not committed; Unity generates them on import.

## Performance

The field is evaluated on the CPU across all cores. At the default pixel step of
2 it costs a few milliseconds per frame; raise the step on a slow machine. The
other three panels only redraw when something changes.

## Limitations

- Transmit beamforming only, into a single wide-beam receiver.
- No absorption, reflection, multipath or receiver modelling.
- Baked amplitudes are raw envelope means; they are not calibrated.
- The live link cannot fill a sector image, because neither firmware produces one.
- Assigning an `ArrayConfig` asset makes the console work on a runtime copy by
  default, so play-mode edits do not modify the asset on disk. Turn off
  `cloneConfigAtRuntime` if you want them to persist.

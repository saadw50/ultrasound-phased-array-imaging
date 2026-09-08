# The `.uscan` container

A baked angle-by-range dataset. One file holds several frames, normally the
`TARGET` and `BG` acquisitions for each of the `FULL8`, `LEFT4` and `RIGHT4`
sub-apertures of a single session.

Written by `unity/Tools/bake_scan.py` and by **Ultrasound > Bake angle-range CSV
to .uscan...** in the editor. Read by `UscanFile.Read` in
`Assets/UltrasoundPhasedArray/Runtime/Sources/UscanFile.cs`.

All integers and floats are little-endian; `int32` is signed, `float32` is IEEE 754.

```
magic        char[4]   "USCN"
version      int32     currently 1
frameCount   int32
metaLength   int32
meta         byte[metaLength]      UTF-8, newline separated key=value

repeated frameCount times:
  metaLength int32
  meta       byte[metaLength]      UTF-8, newline separated key=value
  angleCount int32
  binCount   int32
  angles     float32[angleCount]   steering angle, degrees, ascending
  ranges     float32[binCount]     range bin centre, centimetres, ascending
  amplitude  float32[angleCount * binCount]   angle-major
```

Metadata is `key=value` text rather than JSON so the reader needs no parser.
Keys in use:

| Scope | Key | Meaning |
|---|---|---|
| file | `source` | file name of the CSV the data came from |
| file | `label` | human readable session name |
| file | `note` | provenance and caveats |
| frame | `scene` | `TARGET` or `BG`, copied from the export |
| frame | `tx_mode` | `FULL8`, `LEFT4` or `RIGHT4` |
| frame | `label` | display name for the frame |
| frame | `unit` | what the amplitudes are, e.g. `envelope_adc_counts_mean` |
| frame | `repeats_max` | largest repeat count averaged into any cell |

Unknown keys are ignored, so the format can grow without breaking older readers.

## Amplitudes

`amplitude` is the mean of the `envelope_adc` column over every repeat of a
given `(scene, tx_mode, angle_deg, bin_index)`. It is a raw 10-bit envelope
reading, not a calibrated reflectivity, and the scene labels in the legacy
exports are known to be unreliable — see `results/README.md`.

Ranges are copied from the export's own `range_cm` column, so they carry the
uncorrected timing bias described in the top-level README. The console can
subtract that bias at display time.

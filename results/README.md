# Public experimental results

This directory contains selected distance, acoustic-image, oscilloscope-timing,
and material-classification outputs from the ESP32 ultrasonic project. It does
not contain the conference paper, journal manuscript, or the large raw
`frames/*.raw` capture folders.

## Distance measurements

`distance/` contains exported angle/range CSV files from the Processing/USB
measurement workflow:

- `30cm_bg.csv`: background/control export associated with the 30-cm setup.
- `40cm.csv`, `50cm.csv`, `60cm.csv`, `70cm.csv`, `80cm.csv`, `100cm.csv`,
  `150cm.csv`: labelled distance experiments.
- `84.5cmraw_data.csv`: dense angle/range export associated with the 84.5-cm
  experiment.
- `latestraw_usb_angle_range.csv`: exploratory 31-angle USB scan.

The CSVs preserve the original export fields, including scene, angle, detected
status, first-crossing time, distance estimate, bin index, time-of-flight,
and range. The labels are experimental metadata, not certified accuracy
values. Some exports mix background and object rows; inspect `scene` and
`detected` before computing metrics. The later controlled raw-waveform audit
found a separate +12.68-cm timing bias at a confirmed 50.0-cm target, so these
legacy exports should not be treated as an already calibrated range benchmark.

## 2D acoustic imaging exports

`imaging/` contains the original generated image exports:

- `2dzoom_78-85objraw_data.png`: zoomed object-range view.
- `84.5cm2d_acoustic.png`: 2D acoustic image export.
- `objacoustic_2d_figure.png`: object-focused 2D export.

These are peak/envelope-cell visualizations from the Processing workflow. They
are useful for documenting the project’s visualization pipeline, but they are
not validated full-waveform inverse reconstructions or quantitative object
detection results. Grating lobes, bistatic geometry, multipath, and timing
bias remain active limitations.

## Timing and oscilloscope measurements

`timing/` contains exported oscilloscope CSV files used to inspect TX/RX and
envelope timing, including `ALL0001.CSV`, `ENv- and Ch0.csv`, `Object.csv`,
`Raw data.csv`, `Resting.csv`, `Test2.csv`, and `doughterboard.csv`. These files
retain instrument export headers and channel traces; the timing evidence is
not a per-channel calibration certificate.

## Material-classification results

`classification/` contains the root and session-specific RF/SVM CSV exports
plus `SESSION_QUALITY_RANKING.csv`. The SVM is useful on some in-distribution
sessions, but older wall and metal/cardboard sessions include wrong or
low-confidence outputs. These files must not be interpreted as independent
material-generalization accuracy. Proper validation requires leave-one-session
and leave-one-object-out splits.

## Provenance

The files were copied from the local project directories:

- `D:/CustomTxRx/ProcessingIDE`
- `D:/CustomTxRx/Oscilloscope`
- `D:/CustomTxRx/ESP32_Raw_Reflector_Classification/raw_reflector_results`

The repository contains selected derived/exported results only. File names and
metadata are preserved so that each result can be traced back to its source
workflow. Contact the maintainer before redistribution of data that may contain
laboratory or personally identifying metadata.

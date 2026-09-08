#!/usr/bin/env python3
"""Bake angle/range CSV exports into the compact .uscan container Unity reads.

The exports under ``results/distance/`` store one row per (capture, range bin).
A capture is identified by ``capture_id`` and carries a steering ``angle_deg``,
a ``tx_mode`` sub-aperture and a ``scene`` label.  This tool averages the
``envelope_adc`` column over every repeat of a given (scene, tx_mode, angle)
and writes the resulting angle x range grid.

Usage:
    python3 unity/Tools/bake_scan.py results/distance/84.5cmraw_data.csv \
        --out unity/Assets/StreamingAssets/ultrasound/84.5cm.uscan \
        --label "84.5 cm reflector" --max-range-cm 160

The container format is documented in unity/Tools/uscan_format.md.
"""

import argparse
import csv
import os
import struct
import sys
from collections import defaultdict

MAGIC = b"USCN"
VERSION = 1


def encode_meta(pairs):
    """Metadata is newline separated key=value UTF-8 so the reader needs no JSON."""
    lines = []
    for key, value in pairs:
        text = str(value).replace("\n", " ").replace("=", "-")
        lines.append("{}={}".format(key, text))
    return "\n".join(lines).encode("utf-8")


def read_csv(path, max_range_cm):
    """Return {(scene, tx_mode): {angle: {bin: [values]}}}, {bin: range_cm}."""
    grids = defaultdict(lambda: defaultdict(lambda: defaultdict(list)))
    bin_range = {}

    with open(path, newline="", encoding="utf-8-sig") as handle:
        reader = csv.DictReader(handle)
        required = {"scene", "tx_mode", "angle_deg", "bin_index",
                    "range_cm", "envelope_adc"}
        missing = required - set(reader.fieldnames or [])
        if missing:
            raise SystemExit("{}: missing columns {}".format(path, sorted(missing)))

        for row in reader:
            try:
                rng = float(row["range_cm"])
                if max_range_cm and rng > max_range_cm:
                    continue
                angle = float(row["angle_deg"])
                index = int(row["bin_index"])
                value = float(row["envelope_adc"])
            except (TypeError, ValueError):
                continue

            bin_range[index] = rng
            grids[(row["scene"], row["tx_mode"])][angle][index].append(value)

    return grids, bin_range


def bake(path, out_path, label, max_range_cm, scenes, tx_modes):
    grids, bin_range = read_csv(path, max_range_cm)
    if not grids:
        raise SystemExit("{}: no usable rows".format(path))

    bins = sorted(bin_range)
    ranges = [bin_range[b] for b in bins]
    bin_slot = {b: i for i, b in enumerate(bins)}

    keys = sorted(grids)
    if scenes:
        keys = [k for k in keys if k[0] in scenes]
    if tx_modes:
        keys = [k for k in keys if k[1] in tx_modes]
    if not keys:
        raise SystemExit("{}: scene/tx-mode filter matched nothing".format(path))

    frames = []
    for scene, tx_mode in keys:
        per_angle = grids[(scene, tx_mode)]
        angles = sorted(per_angle)
        amps = []
        repeats = 0
        for angle in angles:
            row = [0.0] * len(bins)
            for index, values in per_angle[angle].items():
                slot = bin_slot.get(index)
                if slot is not None and values:
                    row[slot] = sum(values) / len(values)
                    repeats = max(repeats, len(values))
            amps.extend(row)

        frames.append({
            "meta": encode_meta([
                ("scene", scene),
                ("tx_mode", tx_mode),
                ("label", "{} - {} {}".format(label, scene, tx_mode)),
                ("repeats_max", repeats),
                ("unit", "envelope_adc_counts_mean"),
            ]),
            "angles": angles,
            "ranges": ranges,
            "amps": amps,
        })

    global_meta = encode_meta([
        ("source", os.path.basename(path)),
        ("label", label),
        ("angle_count", len(frames[0]["angles"])),
        ("bin_count", len(bins)),
        ("note", "mean envelope ADC over repeats; not a calibrated reflectivity"),
    ])

    os.makedirs(os.path.dirname(out_path) or ".", exist_ok=True)
    with open(out_path, "wb") as out:
        out.write(MAGIC)
        out.write(struct.pack("<ii", VERSION, len(frames)))
        out.write(struct.pack("<i", len(global_meta)))
        out.write(global_meta)
        for frame in frames:
            out.write(struct.pack("<i", len(frame["meta"])))
            out.write(frame["meta"])
            out.write(struct.pack("<ii", len(frame["angles"]), len(frame["ranges"])))
            out.write(struct.pack("<%df" % len(frame["angles"]), *frame["angles"]))
            out.write(struct.pack("<%df" % len(frame["ranges"]), *frame["ranges"]))
            out.write(struct.pack("<%df" % len(frame["amps"]), *frame["amps"]))

    size = os.path.getsize(out_path)
    print("{} -> {} ({} frames, {} angles x {} bins, {:.1f} kB)".format(
        path, out_path, len(frames), len(frames[0]["angles"]), len(bins), size / 1024.0))
    for frame in frames:
        peak = max(frame["amps"]) if frame["amps"] else 0.0
        name = frame["meta"].decode("utf-8").splitlines()[2]
        print("   {}  peak={:.1f}".format(name, peak))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("csv", help="angle/range export from results/distance/")
    parser.add_argument("--out", required=True, help="destination .uscan file")
    parser.add_argument("--label", default="", help="human readable dataset label")
    parser.add_argument("--max-range-cm", type=float, default=0.0,
                        help="drop bins beyond this range (0 keeps everything)")
    parser.add_argument("--scene", action="append", default=[],
                        help="keep only these scene labels (repeatable)")
    parser.add_argument("--tx-mode", action="append", default=[],
                        help="keep only these tx_mode labels (repeatable)")
    args = parser.parse_args(argv)

    label = args.label or os.path.splitext(os.path.basename(args.csv))[0]
    bake(args.csv, args.out, label, args.max_range_cm,
         set(args.scene), set(args.tx_mode))
    return 0


if __name__ == "__main__":
    sys.exit(main())

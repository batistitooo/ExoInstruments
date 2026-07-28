#!/usr/bin/env python3
"""
Checks that an exported ExoInstruments frame is what its own header says it is.

WHY THIS EXISTS. The imaging pipeline now writes calibratable ADU with the
detector constants that describe them: EGAIN (electrons per count), RDNOISE,
BIASLVL, DARKCURR, EXPTIME. That means the frame carries its own prediction --
a bias frame MUST have a mean of BIASLVL and a spread of RDNOISE/EGAIN, and a
dark MUST add DARKCURR*EXPTIME on top. Nothing outside the frame is needed to
check it, so this script reads the header, works out what the pixels should do,
and compares. A disagreement is a real bug in the pipeline, not a matter of
opinion.

    python3 tools/check_calibration_frames.py bias.fits dark.fits light.fits

    python3 tools/check_calibration_frames.py --subtract light.fits dark.fits

The second form is the end-to-end test: subtracting a dark of matching exposure,
gain, binning and cooler setpoint from a light frame must REMOVE the hot pixels.
That is the whole reason an observer takes a dark, and it was impossible in this
pipeline while hot pixels were stamped on after digitisation.

No third-party packages: FITS is 2880-byte blocks of 80-character cards followed
by big-endian integers, which the standard library reads perfectly well. This
runs on the Python 3 that ships with macOS and most Linux distributions, the
same standard tools/pack_gaia_catalog.py holds itself to.
"""

import array
import collections
import math
import sys

BLOCK = 2880
CARD = 80


# --- FITS reading -------------------------------------------------------------

def parse_card(card):
    """One 80-character card as (key, value), or (None, None) for commentary/padding."""
    key = card[:8].strip()
    if not key or key in ("END", "COMMENT", "HISTORY"):
        return None, None
    if card[8:10] != "= ":
        return None, None

    rest = card[10:].strip()
    if rest.startswith("'"):
        # Quoted string: the comment separator only counts after the closing quote.
        end = rest.find("'", 1)
        return key, rest[1:end].strip() if end > 0 else rest[1:].strip()

    value = rest.split("/")[0].strip()
    if value in ("T", "F"):
        return key, value == "T"
    try:
        return key, int(value)
    except ValueError:
        pass
    try:
        return key, float(value.replace("E", "e"))
    except ValueError:
        return key, value


def read_fits(path):
    """Returns (header dict, list of physical pixel values)."""
    with open(path, "rb") as f:
        header = {}
        while True:
            block = f.read(BLOCK)
            if len(block) < BLOCK:
                raise ValueError(f"{path}: header ended early -- not a FITS file?")
            text = block.decode("ascii", "replace")
            finished = False
            for i in range(BLOCK // CARD):
                card = text[i * CARD:(i + 1) * CARD]
                if card[:8].strip() == "END":
                    finished = True
                    break
                key, value = parse_card(card)
                if key is not None:
                    header[key] = value
            if finished:
                break

        bitpix = header.get("BITPIX")
        if bitpix != 16:
            raise ValueError(f"{path}: BITPIX={bitpix}, only 16-bit is handled here")

        width, height = header.get("NAXIS1"), header.get("NAXIS2")
        count = width * height
        raw = f.read(count * 2)
        if len(raw) < count * 2:
            raise ValueError(f"{path}: data block is short ({len(raw)} of {count * 2} bytes)")

    values = array.array("h")
    values.frombytes(raw)
    if sys.byteorder == "little":
        values.byteswap()          # FITS is big-endian by definition

    # BZERO=32768 with signed 16-bit storage is how FITS represents unsigned counts.
    zero = header.get("BZERO", 0)
    scale = header.get("BSCALE", 1)
    return header, values, zero, scale


# --- statistics, exactly rather than by sampling ------------------------------

def summarise(values, zero, scale):
    """
    Mean, median and standard deviation over every pixel.

    Counted rather than sorted: the pixels are integers over a bounded range, so a
    histogram gives the exact median in one pass instead of sorting tens of millions
    of values.
    """
    hist = collections.Counter(values)
    n = sum(hist.values())

    def physical(raw):
        return zero + scale * raw

    total = sum(physical(v) * c for v, c in hist.items())
    mean = total / n

    variance = sum(c * (physical(v) - mean) ** 2 for v, c in hist.items()) / n

    keys = sorted(hist)
    seen, median = 0, None
    for k in keys:
        seen += hist[k]
        if seen >= n / 2:
            median = physical(k)
            break

    # A ROBUST spread as well as the plain one, and the difference between them is the
    # point rather than a detail. A dark frame's hot pixels are a handful of values
    # thousands of counts above everything else, and they dominate a standard deviation
    # completely: on a 512x512 test frame, 87 hot pixels take the measured stddev from
    # 1.4 to 44.7 counts. A percentile spread ignores them, so it measures the noise of
    # the ARRAY rather than the brightness of its defects.
    #
    # From INTERPOLATED percentiles rather than the median absolute deviation. On integer
    # counts the MAD can only come out as a whole number, so sigma*1.4826 is quantised to
    # multiples of 1.48 counts -- useless for checking a noise of order one count, which is
    # exactly the regime here. Interpolating within the histogram bin recovers sub-count
    # resolution, and the 15.87 and 84.13 percentiles are the +/-1 sigma points of a
    # Gaussian by definition, so half their separation is sigma with no fudge factor.
    def percentile(p):
        target = p * n
        seen_here = 0
        for key in keys:
            c = hist[key]
            if seen_here + c >= target:
                return physical(key) - 0.5 + (target - seen_here) / c
            seen_here += c
        return physical(keys[-1])

    robust_sigma = 0.5 * (percentile(0.8413) - percentile(0.1587))

    return {
        "n": n,
        "mean": mean,
        "median": median,
        "stddev": math.sqrt(variance),
        "robust_sigma": robust_sigma,
        "min": physical(keys[0]),
        "max": physical(keys[-1]),
        "hist": hist,
        "zero": zero,
        "scale": scale,
    }


def count_above(stats, threshold):
    return sum(c for v, c in stats["hist"].items() if stats["zero"] + stats["scale"] * v > threshold)


# --- the checks ---------------------------------------------------------------

def report(ok, text):
    print(("  ok   " if ok else "  FAIL ") + text)
    return 0 if ok else 1


def check_frame(path):
    header, values, zero, scale = read_fits(path)
    stats = summarise(values, zero, scale)

    kind = str(header.get("IMAGETYP", "?"))
    gain = header.get("EGAIN")            # electrons per ADU
    read_noise = header.get("RDNOISE")    # electrons
    bias = header.get("BIASLVL")
    dark = header.get("DARKCURR")         # electrons/s per PHYSICAL pixel
    exptime = header.get("EXPTIME")
    binning = header.get("XBINNING", 1)

    print(f"\n=== {path}")
    print(f"    {kind}, {header.get('NAXIS1')}x{header.get('NAXIS2')}, "
          f"EXPTIME={exptime}s, XBINNING={binning}")
    print(f"    EGAIN={gain} e-/adu   RDNOISE={read_noise} e-   "
          f"BIASLVL={bias} adu   DARKCURR={dark} e-/px/s   CCD-TEMP={header.get('CCD-TEMP')} C")
    if "MAGZERO" in header:
        print(f"    MAGZERO={header['MAGZERO']}  (m = -2.5*log10(adu/s) + MAGZERO)")
    if "RANDSEED" in header:
        print(f"    RANDSEED={header['RANDSEED']}  (this frame is reproducible from it)")
    print(f"    measured: median={stats['median']:.1f}  robust sigma={stats['robust_sigma']:.3f}  "
          f"(plain mean={stats['mean']:.3f}, plain stddev={stats['stddev']:.3f})  "
          f"min={stats['min']:.0f}  max={stats['max']:.0f} adu")

    failures = 0
    if gain is None or read_noise is None or bias is None:
        print("    (no EGAIN/RDNOISE/BIASLVL: nothing to check against -- a processed product?)")
        return 0

    is_bias = exptime in (0, 0.0) or "bias" in kind.lower()
    is_dark = "dark" in kind.lower()

    if not (is_bias or is_dark):
        return report_light_frame(header, stats, gain, read_noise, bias)

    # What the header says the pixels must do. Compared against the ROBUST statistics,
    # because the defects a dark frame exists to record would otherwise swamp both.
    dark_electrons = (dark or 0.0) * (exptime or 0.0) * binning * binning
    predicted_level = math.floor(bias + dark_electrons / gain)
    predicted_sigma = math.sqrt(dark_electrons + read_noise ** 2) / gain

    print(f"    predicted: median={predicted_level:.0f}  sigma={predicted_sigma:.3f} adu")

    # One count of tolerance: the converter truncates rather than rounds, so the level can
    # only ever be pinned to within a count.
    failures += report(abs(stats["median"] - predicted_level) <= 1.0,
                       f"level matches the header ({stats['median']:.0f} vs {predicted_level:.0f} adu)")

    if predicted_sigma < 1.0:
        print(f"  note   predicted spread is {predicted_sigma:.2f} adu, BELOW one count: at this")
        print( "         gain the converter cannot resolve the read noise, so what you measure is")
        print( "         quantisation, not the amplifier. Raise the gain until RDNOISE/EGAIN")
        print( "         exceeds 1 adu if you want to measure the read noise itself.")
    else:
        inflated = stats["stddev"] > 1.5 * stats["robust_sigma"]
        failures += report(abs(stats["robust_sigma"] - predicted_sigma) < 0.2 * predicted_sigma,
                           f"noise matches the header ({stats['robust_sigma']:.3f} vs {predicted_sigma:.3f} adu)"
                           + (f" -- the plain stddev reads {stats['stddev']:.1f} because the defects dominate it"
                              if inflated else ""))

    # Clipping cannot be seen from a minimum value: with a 5-sigma pedestal, a few pixels
    # legitimately land in the bottom bin. What clipping produces is a SPIKE there -- every
    # negative excursion piled into one count -- so the test is whether the population at
    # zero exceeds what the noise alone predicts.
    at_zero = stats["hist"].get(-int(stats["zero"] / stats["scale"]), 0) if stats["scale"] else 0
    at_zero = sum(c for v, c in stats["hist"].items() if stats["zero"] + stats["scale"] * v == 0)
    if predicted_sigma > 0:
        z = (0.5 - (bias + dark_electrons / gain)) / predicted_sigma
        expected_zero = stats["n"] * 0.5 * (1.0 + math.erf(z / math.sqrt(2.0)))
        failures += report(at_zero <= max(5.0, 5.0 * expected_zero),
                           f"no pile-up at zero: {at_zero} pixels in the bottom count against "
                           f"{expected_zero:.1f} the noise alone predicts -- the pedestal is doing its job")

    if is_dark:
        hot = count_above(stats, predicted_level + 10.0 * max(predicted_sigma, 1.0))
        print(f"    {hot} pixels sit more than 10 sigma above the dark level "
              f"({100.0 * hot / stats['n']:.4f}% of the frame) -- hot pixels and cosmic rays")
        failures += report(hot > 0, "the dark frame actually contains defects to subtract")

    return failures


def report_light_frame(header, stats, gain, read_noise, bias):
    """
    Is there anything in this frame, and if so can the display possibly show it?

    "I see nothing" has two very different causes and they need separating. Either the
    exposure genuinely collected no signal, or it collected signal that the display's
    transfer function renders as black. The pixels settle it: this measures the target
    against the frame's own background noise, and separately against the converter's full
    scale, because the display is normalised to the LATTER.
    """
    adc_max = (1 << header.get("ADCBITS", 16)) - 1
    background = stats["median"]
    noise = max(stats["robust_sigma"], 1e-9)

    # Signal is anything standing clear of the background. 5 sigma is the usual threshold
    # for calling a detection real.
    threshold = background + 5.0 * noise
    signal_pixels = count_above(stats, threshold)
    peak_over_background = stats["max"] - background

    print(f"    background: {background:.1f} adu, noise {noise:.2f} adu")
    print(f"    peak:       {stats['max']:.0f} adu, i.e. {peak_over_background:.1f} adu "
          f"= {peak_over_background / noise:.1f} sigma above background")
    print(f"    {signal_pixels} pixels stand more than 5 sigma clear of the background "
          f"({100.0 * signal_pixels / stats['n']:.4f}% of the frame)")
    print(f"    brightest pixel reaches {100.0 * stats['max'] / adc_max:.3f}% of the "
          f"converter's full scale ({adc_max} adu)")

    failures = 0
    detected = peak_over_background > 5.0 * noise
    failures += report(detected,
                       "there IS signal in this frame"
                       if detected else
                       "NOTHING in this frame stands clear of the noise -- the exposure really did "
                       "collect nothing, so this is an exposure/target problem, not a display one")

    if detected:
        fraction = stats["max"] / adc_max
        # The display normalises to the converter's full scale with no auto-scaling, and the
        # asinh stretch turns over at 2% of it. Below that a real signal is rendered almost
        # linearly, which for a faint target means almost black.
        if fraction < 0.02:
            print("  note   ...but the peak is below 2% of full scale, which is where the asinh")
            print("         stretch turns over. The display normalises to the CONVERTER's range,")
            print("         not to this frame's own content, so a real signal this faint renders")
            print("         as near-black however good the data is. Raise the exposure, the gain,")
            print("         or the binning -- or read the FITS in a viewer that auto-scales.")
        else:
            print("       and the peak is bright enough for the display stretch to show it.")

    return failures


def check_subtraction(light_path, dark_path):
    """The end-to-end test: does subtracting the dark remove the hot pixels?"""
    lh, lv, lz, ls = read_fits(light_path)
    dh, dv, dz, dsc = read_fits(dark_path)

    print(f"\n=== dark subtraction: {light_path} minus {dark_path}")
    failures = 0

    if len(lv) != len(dv):
        return report(False, "frames are different sizes -- they cannot be subtracted")

    for key in ("EXPTIME", "XBINNING", "GAIN", "CCD-TEMP"):
        same = lh.get(key) == dh.get(key)
        failures += report(same, f"{key} matches ({lh.get(key)} vs {dh.get(key)})"
                                 + ("" if same else "  <-- a dark is only valid for a matching light"))

    bias = lh.get("BIASLVL", 0)
    lstats = summarise(lv, lz, ls)
    dstats = summarise(dv, dz, dsc)

    # A defect threshold well above the frame's own noise, from the DARK's statistics.
    threshold = dstats["median"] + 10.0 * max(dstats["stddev"], 1.0)
    hot_in_light = sum(1 for a in lv if lz + ls * a > threshold)

    # Subtract, restoring one pedestal (subtracting two frames removes it twice).
    residual = array.array("i", (lz + ls * a - (dz + dsc * b) + int(bias) for a, b in zip(lv, dv)))
    hot_after = sum(1 for r in residual if r > threshold)

    print(f"    defect threshold from the dark: {threshold:.1f} adu")
    print(f"    pixels above it in the light:      {hot_in_light}")
    print(f"    pixels above it after subtraction: {hot_after}")

    # The light also contains the target, which subtraction must NOT remove, so the
    # count does not go to zero -- it collapses to whatever real signal is that bright.
    failures += report(hot_after < hot_in_light,
                       f"subtraction removed {hot_in_light - hot_after} of them "
                       f"({100.0 * (hot_in_light - hot_after) / max(1, hot_in_light):.1f}%)")
    print("    Anything left should be real: the target itself, and cosmic rays, which land")
    print("    somewhere different in every exposure and so cannot be subtracted by a dark.")
    return failures


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return 1

    if args[0] == "--subtract":
        if len(args) != 3:
            print("usage: --subtract <light.fits> <dark.fits>")
            return 1
        failures = check_subtraction(args[1], args[2])
    else:
        failures = sum(check_frame(p) for p in args)

    print("\nALL CHECKS PASSED" if failures == 0 else f"\n{failures} CHECK(S) FAILED")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())

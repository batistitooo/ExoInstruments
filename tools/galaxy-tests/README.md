# Galaxies: the profile, the size solve, and the renderer

A galaxy is drawn from four catalogued numbers: total B magnitude, D25, axis ratio, position
angle, plus one profile. Every step between the catalogue and the frame fails quietly:

* **b_n slightly wrong** scales every galaxy's flux, through the `e^(b_n)` factor in the total.
* **the total-flux factor wrong** scales them all uniformly, which looks like a calibration choice.
* **the R_e solve on the wrong root** gives the right flux at the wrong *size*.
* **a pixel integration that misses the nucleus** loses light exactly where the profile is steepest;
  a Sersic n = 4 profile has an infinite central slope, so the value at a pixel's centre is not its
  average over the pixel by an unbounded factor.

None of those throws, and all of them still produce a picture of a galaxy. So each is measured.

## Run

```
dotnet run -p:Core=../../ExoInstruments/Core
python3 -m venv env && ./env/bin/pip install numpy scipy astropy
./env/bin/python compare_galaxy.py
```

## References

SciPy's `gammainc` / `gammaincinv`, because b_n is *defined* as the inverse of the regularised
incomplete gamma at 1/2 rather than as the series everyone quotes; and astropy's `Sersic2D`, written
independently of this project, for the profile itself.

The comparison also reports what the usual Ciotti & Bertin (1999) asymptotic series would have cost
against the exact inversion, since that series is the normal way this constant is obtained.

## What is checked

| | against |
|---|---|
| P(a, x) and log Gamma over a = 0.2..30 | SciPy |
| b_n over 0.3 <= n <= 8 | `gammaincinv(2n, 0.5)` |
| total-flux factor | the closed form of Graham & Driver (2005) eq. 4 |
| surface brightness profile | astropy `Sersic2D` |
| enclosed fraction | `gammainc(2n, b_n (R/R_e)^(1/n))` |
| R_e from (m_T, D25) | round trip: mu(D25/2) must come back at exactly 25 |
| the solve's branch | R_e must lie inside the isophote, not outside it |
| deposited electrons | total x analytic enclosed fraction, over 81 shapes |
| centroid and axis ratio of the deposit | the values it was given, at a sub-pixel offset |

The grid deliberately includes combinations with **no** solution for R_e. That is not a numerical
failure: it is a galaxy whose catalogued total magnitude is too faint to reach 25 mag/arcsec^2
anywhere at its catalogued size, and the caller falls back to keeping the size rather than the
isophote and says so.

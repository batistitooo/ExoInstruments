# constellation-tests

The constellation a target sits in, cross-validated against **astropy**.

## Why

Naming a constellation looks like a table lookup and is really a frame change. Delporte's
boundaries, adopted by the IAU in 1928, published 1930, unchanged since, are lines of constant
right ascension and declination **in the mean equinox of B1875 and in no other frame**. In J2000
they are slanted curves. So the lookup is:

```
J2000 (FK5/ICRS)
  ->  FK4 B1950      Murray 1989 A&A 218, 325 eq. 28 + the eq. 29 rotating-system term
  ->  FK4 B1875      Newcomb precession (Explanatory Supplement 1992, ch. 3)
  ->  Roman's scan   VI/42, first arc whose declination floor is below the position and whose
                     right-ascension arc brackets it
```

Get the frame change wrong and the answer is right in the middle of every constellation and wrong
along every edge, wrong exactly where it is interesting, and never obviously wrong at all.

## The three references

| # | reference | what it establishes |
|---|---|---|
| 1 | astropy `FK4NoETerms(equinox=B1875)` | the frame change itself, the **same** chain, so this is an arithmetic check |
| 2 | Roman's own worked examples in the VI/42 ReadMe | the Newcomb precession and the table scan, against the table author's own answers |
| 3 | astropy `get_constellation` | the end-to-end answer, by a **different** chain, see below |

Reference 3 is deliberately not the same route: `get_constellation` precesses with the modern IAU
2006 model to the Julian date of B1875 rather than going through FK4, which its own docstring calls
"plenty sufficient for constellations". The two realisations of "B1875" genuinely differ, so the
check is not "agree everywhere" but "disagree **only** where that difference can explain it".

## Results

| check | result |
|---|---|
| position after the frame change, 2 664 grid points | **3.0×10⁻⁹ arcsec** vs astropy FK4NoETerms |
| the frame change is not a no-op | median displacement 1.264° over the 125 years |
| Roman's published worked examples | **8 / 8** reproduce |
| constellation over a 258 480-point grid | 258 373 agree with astropy (**99.959 %**) |
| the two B1875 realisations differ by | up to 21.0 arcsec |
| furthest disagreement from a boundary | **20.1 arcsec**, inside that 21.0 arcsec budget |
| constellations reachable | **88 / 88** |

The last row has no external reference and is the check that catches a lost or mis-sorted record:
Roman's scan depends on record ORDER, and a table that has been re-sorted still answers plausibly
for most of the sky while making some constellation unreturnable. Equuleus and Crux, the two
smallest, are the ones that would go first.

The 107 disagreements are not errors on either side. Every one of them is a position closer to a
boundary than the two frame realisations are to each other, which is to say a position where "which
constellation" is genuinely ambiguous at the precision the published table carries; its right
ascensions are quantised to 0.0001 h, itself 1.5 arcsec at the equator.

## What this does NOT establish

- **Not the E-terms question.** FK4 star positions carry the E-terms of elliptic aberration, up to
  0.343 arcsec; this chain deliberately omits them because Delporte's boundaries are grid lines, not
  observed positions. That is an argument, not a measurement.
- **Nothing about the names.** The 88 names, meanings and genitives come from the IAU's own table
  via `tools/generate_constellation_table.py`, which cross-checks the abbreviations against VizieR's
  independent list. The generator refuses to write a file if the two disagree.

## Running

```
dotnet run -p:Core=../../ExoInstruments/Core
python -m venv env && ./env/bin/pip install numpy astropy
./env/bin/python compare_constellations.py
```

Exit code 0 when every check passes. Verified against astropy 6.0.1.

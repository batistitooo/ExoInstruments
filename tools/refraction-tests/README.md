# Atmospheric dispersion, and the chromatic PSF

Air has a refractive index a few parts in ten thousand above one, and that index depends on
wavelength, the same dispersion that makes a prism work. So the atmosphere lifts a star toward the
zenith by an angle that depends on colour, drawing it out into a short spectrum. Every professional
survey pipeline models this; it is a first-order astrometric and photometric systematic, and it is why
high-resolution instruments carry atmospheric dispersion correctors.

## What it is worth here

400 to 700 nm, 45 degrees from the zenith:

| instrument | plate scale | site | smear | in pixels |
|---|---|---|---|---|
| RedCat 51 | 3.82"/px | 650 m | 1.349" | 0.4 |
| RC20 | 0.0688"/px | 650 m | 1.349" | **19.6** |
| CDK1000 | 0.0398"/px | 1712 m | 1.215" | **30.5** |
| FORS2 | 0.0630"/px | 2635 m | 1.107" | **17.6** |
| SPHERE/ZIMPOL | 0.0036"/px | 2635 m | 1.107" | **307** |

Three hundred pixels on an instrument that delivers a 25 mas core. SPHERE cannot exist without a
corrector and has one (Beuzit et al. 2019, A&A 631, A155), so `HasAtmosphericDispersionCorrector` is
set for it and the residual is scaled to 5% rather than to zero; a real prism pair cancels the
dispersion of a model atmosphere at a design zenith distance, not the night's actual air.

## One kernel for both effects, and why that is exact

Three things vary with wavelength inside a single filter: the Airy pattern scales as lambda/D, the
seeing disc as lambda^(-1/5) through r0, and the dispersion offset as the refractivity difference.
A frame is not monochromatic, so what it records is the sum of the monochromatic images weighted by
how many photons arrive at each wavelength.

Convolution is linear, so that sum can be taken on the **kernels** instead of on the images:

    sum_i w_i (image * K_i)  =  image * (sum_i w_i K_i)

One convolution with the weighted-mean kernel is therefore not an approximation of a chromatic PSF,
it **is** one, and it costs nothing beyond building the kernel. Each sub-band is laid down at its own
dispersion offset with bilinear placement, so the smear is not quantised into whole pixels.

The dispersion offset depends only on wavelength and zenith distance, both common to a field
arcminutes across, which is what makes a shared kernel legitimate. What is not shared is the photon
weighting: a redder source's smear is slightly shorter. That is a second-order difference and is left
out of the shared kernel, and stated.

## Run

```
dotnet run -p:Core=../../ExoInstruments/Core
python3 -m venv env && ./env/bin/pip install numpy PyAstronomy
./env/bin/python compare_refraction.py
```

## Results

The refractive index is Filippenko (1982, PASP 94, 715) eq. 1-3, the standard astronomical reference.
The check is against PyAstronomy's air-to-vacuum conversion, which **is** the refractive index of air
and which ships three independent published formulations:

| against | agreement |
|---|---|
| Edlen (1953) | 2.9e-5 relative |
| Peck & Reeder (1972) | 5.7e-5 |
| Ciddor (1996) | 6.2e-5 |
| the three against each other | **6.5e-5** |

So the residual is the literature's own spread, not an error in the transcription. Also checked: the
temperature and pressure scaling is the identity at 15 C and 1013.25 mbar, altitude lowers the index,
and humid air is optically thinner than dry air at the same pressure.

The geometry:

| | |
|---|---|
| refraction at the zenith | exactly zero |
| proportional to tan z | to 8e-16 |
| blue refracted more than red | everywhere |
| absolute refraction at z = 45 deg, sea level | 57.3" against the classical 57.5" |

And the kernel:

| | |
|---|---|
| one sub-band, no offset, against the monochromatic kernel | **bit-identical** |
| normalisation | 2e-9 |
| centroid against the photon-weighted mean offset | 0.01 px |
| across-dispersion width vs zenith distance | constant to 0.2% |
| along-dispersion width | grows monotonically |

That first row is the one that matters most: it proves the chromatic path reduces exactly to the path
already cross-validated against GalSim, so the new machinery cannot have changed the old physics.

# slew-tests

What it costs to point an orbital telescope somewhere else: the rest-to-rest manoeuvre, the
guide-star acquisition after it, the electric charge both spend, and the orbit-averaged power ledger
that has to pay for them.

## Why

The observatory panel used to repoint a spacecraft instantly and for nothing. That is not a small
simplification: the slew rate is the single constraint that shapes a real observing programme, which
is why STScI schedules large manoeuvres inside Earth occultation rather than lose the sky. Once it
takes time and charge, three quantities decide whether the telescope can be used at all, and all
three are easy to get wrong in ways that still look plausible on screen:

```
manoeuvre    alpha = tau/J, capped at the published rate ceiling   Core/SlewDynamics.cs
eclipse      what fraction of the orbit the panels see the Sun     Core/OrbitalPowerBudget.cs
ledger       charge in, charge out, clamped at both ends           Core/OrbitalPowerBudget.cs
```

## Run

```
dotnet run -c Release -p:Core=../../ExoInstruments/Core
```

## What is checked, and against what

Nothing here asserts that the code does what the code says. Every check is against a published
figure, an identity between two independently published ones, or a closed form derived in the test
itself and compared with the one the mod computes.

| Section | Checked against |
| --- | --- |
| 1b | The scale transplant: a real-scale home body must give exactly 1x (every published figure used as published), and a 90 deg repoint on stock Kerbin must cost the same fraction of an orbit as HST's does of Earth's. |
| 1 | HST Primer Cycle 34, "Pointing, Orientation, and Roll Constraints": 6 deg/min, and its own stated consequence that a full circle takes about an hour. Two published numbers for one fact; they have to agree. Plus "Orbital Visibility, Acquisition Times, and Overheads" for the 6.5 minute guide-star acquisition. |
| 2 | The two branches of the profile against each other at the crossover `theta = w^2/alpha`, where both must give `2w/alpha`. Continuity, monotonicity, and the degenerate cases (no torque, zero angle). |
| 3 | The cost model, and the identity `I = m Isp g0` for the thruster path. |
| 4 | `FractionOfAngleCovered` against a 200 000-step numerical integration of the rate profile it claims to be the integral of. |
| 5 | Vallado's circular-orbit shadow fraction, written out independently here, against the mod's implementation (which delegates to `OrbitalVisibility` instead). Then against HST's published ~36 minute occultation in a 96 minute orbit, and against `ContinuousViewingHalfWidthDeg` for where full sunlight begins. |
| 6 | `Advance` and `EnduranceSeconds` against each other: advancing by the endurance has to land exactly on the reserve. |

## Three findings that came out of it

**The published rate cannot be used literally.** 6 deg/min is calibrated against a 96 minute orbit.
Kerbin is a tenth of Earth's size, so a low orbit is about 29 minutes and the same repoint costs
**51 % of an orbit** instead of 16 %: in play, a target clear of the limb when clicked was behind
the planet by the time the telescope arrived. What the constraint exists to represent is the
fraction of an orbit spent turning, so that ratio is what is preserved, through the grazing-orbit
period √(R³/μ) of the home body against Earth's. Section 1b pins both ends of it.

**The shipped part is rate-limited for every repoint a player will ever make.** Its wheels are
12 kN m an axis against about 79 000 kg m^2, which is 8.7 deg/s^2: four orders of magnitude more
angular acceleration than HST's real wheels manage, because KSP's reaction wheels are balanced for
flying rockets. The crossover between the torque-limited and rate-limited branches therefore falls
at 43.8 arcsec, still inside one field of view. The published rate ceiling is what governs the time,
exactly as it does on the real spacecraft, and the torque figure in the part config cannot make
repointing free. Section 3 asserts this so it cannot drift.

**Billing the ramps rather than the manoeuvre made the constraint vanish.** The first cost model
charged the wheels only for the time they were changing momentum, on the correct-sounding grounds
that a wheel coasting at constant speed draws no torque current. With ramps lasting tens of
milliseconds, a ninety-degree repoint came to 0.017 EC out of a 400 EC battery. It is also wrong
twice over: KSP's own `ModuleReactionWheel` bills for the whole time the autopilot is commanding it,
so the ground-commanded slew would have been cheaper than the identical slew flown by hand, and a
real reaction wheel assembly draws tens of watts continuously whether or not it is accelerating.
See the comment on `ReactionWheelChargeUnits`.

## Balance, not physics

A 90 degree repoint on the shipped part comes to about 207 EC, against the 400 EC the part itself
carries: half the battery for one repoint. That is deliberate (an orbital observatory wants to be
built as a real satellite bus with panels) but it is a balance number, not a sourced one, and it is
the kind of thing to validate in play. It was 675 EC before the scale transplant, more than the
battery held, which was a consequence of the manoeuvre being three times too long rather than of any
decision about balance. The affordability test is not a flat balance check: it
asks whether the battery outlasts the manoeuvre at the net rate, so the sunlight the spacecraft
flies through during the manoeuvre counts.

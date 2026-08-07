using System;
using System.Globalization;
using ExoInstruments.Core;

// Headless cross-validation of the repointing and power models, to the same standard as
// tools/spacecraft-tests: nothing here checks that the code does what the code says. Every
// assertion is against a figure STScI publishes, against an identity between two independently
// published quantities, or against a closed form derived here from first principles and compared
// with the one the mod computes.
//
// Run from this directory with:
//   dotnet run -c Release -p:Core=../../ExoInstruments/Core

internal static class Program
{
    private static int failures;
    private static int checks;

    private static void Main()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        Section("1. HST's published slew rate, and its own cross-check");
        PublishedSlewRate();

        Section("1b. Transplanting those figures into a scaled universe");
        UniverseScale();

        Section("2. The rest-to-rest profile");
        ProfileShape();

        Section("3. What a slew costs");
        SlewCost();

        Section("4. Where the boresight is part way through");
        ProfileIntegral();

        Section("5. Eclipse geometry against HST's published occultation");
        Eclipse();

        Section("6. The power ledger");
        Ledger();

        Console.WriteLine();
        Console.WriteLine($"{checks - failures}/{checks} checks passed.");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    // ------------------------------------------------------------------ 1

    // HST Primer for Cycle 34, "Pointing, Orientation, and Roll Constraints".
    private const double HstSlewRateDegPerMinute = 6.0;
    private const double HstSlewRateDegPerSecond = HstSlewRateDegPerMinute / 60.0;

    // HST Primer for Cycle 34, "Orbital Visibility, Acquisition Times, and Overheads".
    private const double HstAcquisitionSeconds = 6.5 * 60.0;

    // The part config's wheels (12 kN m each axis) on a 4.5 t vehicle. Inertia is the module's own
    // 2/5 M R^2 on a 13.24 m tube, the same estimate ModuleExoSpaceTelescope.EstimateInertiaKgM2
    // makes, so the numbers below are the ones the shipped part really flies with.
    private const double PartTorqueNm = 12000.0;
    private const double PartMassKg = 4500.0;
    private const double PartRadiusMeters = 13.24 / 2.0;
    private static readonly double PartInertia = 0.4 * PartMassKg * PartRadiusMeters * PartRadiusMeters;

    private static void PublishedSlewRate()
    {
        // The Primer states the rate AND its consequence: "The slew rate of HST is limited to
        // approximately 6 degrees per minute of time. Consequently, about one hour is needed to go
        // full circle in pitch, yaw, or roll." Those are two published numbers describing one
        // fact, and if they disagree one of them has been transcribed wrong.
        double fullCircleMinutes = 360.0 / HstSlewRateDegPerMinute;
        Check("full circle at 6 deg/min is the Primer's 'about one hour'",
              fullCircleMinutes, 60.0, 0.5, "minutes");

        // A 90 degree repoint at the published rate. This is the figure quoted everywhere as
        // "HST takes about a quarter of an hour to slew 90 degrees", and it has to fall out of the
        // rate rather than be carried as a second constant.
        double pureRotation = 90.0 / HstSlewRateDegPerSecond;
        Check("90 deg at the published rate", pureRotation / 60.0, 15.0, 0.1, "minutes");

        // And the shipped part must actually BE rate-limited at that angle, or the published
        // ceiling is decorative: the wheels have to be strong enough to reach it.
        SlewProfile p = SlewDynamics.Compute(90.0, PartTorqueNm, PartInertia,
                                             HstSlewRateDegPerSecond, HstAcquisitionSeconds);
        Assert("a 90 deg repoint on the shipped part is rate-limited, not torque-limited", p.RateLimited);
        Check("its rotation time is the published-rate one plus the spin-up",
              p.ManoeuvreSeconds, pureRotation + HstSlewRateDegPerSecond / p.AccelerationDegPerSecond2,
              1e-9, "s");
        Check("acquisition is charged on top", p.TotalSeconds - p.ManoeuvreSeconds,
              HstAcquisitionSeconds, 1e-9, "s");
    }

    // ------------------------------------------------------------------ 1b

    // Stock KSP's home body, and Earth's, from the game's own figures and the published ones.
    private const double KerbinRadius = 600000.0;
    private const double KerbinMu = 3.5316000e12;
    private const double EarthRadius = 6.371e6;
    private const double EarthMu = 3.986004418e14;

    private static void UniverseScale()
    {
        // THE TEST THAT MAKES THIS A SCALE TRANSPLANT RATHER THAN A DIFFICULTY SLIDER. On a
        // real-scale install the home body IS Earth, and every published figure has to come back
        // out untouched. If this is ever not exactly 1, the "scaling" is doing something else.
        Check("a real-scale install leaves the published figures alone",
              SlewDynamics.UniverseTimeScale(EarthRadius, EarthMu), 1.0, 1e-12, "x");

        double scale = SlewDynamics.UniverseTimeScale(KerbinRadius, KerbinMu);
        Check("stock Kerbin runs about 3.3 times faster than Earth", scale, 3.26, 0.05, "x");

        // The invariant being preserved: the fraction of one orbit a 90 degree repoint costs. HST
        // spends 15 min turning and 6.5 acquiring out of a 96 min orbit. A telescope in an
        // equivalent low orbit of Kerbin has to spend the same FRACTION, or the constraint means
        // something different in the two universes.
        double hstFraction = (15.0 + 6.5) / 96.0;

        // A low Kerbin orbit, taken at the same altitude-over-radius ratio as HST's 540 km over
        // Earth's 6371, so the two orbits are the same orbit in scaled terms.
        double altitudeRatio = 540.0 / 6371.0;
        double kerbinOrbitRadius = KerbinRadius * (1.0 + altitudeRatio);
        double kerbinPeriodMin = 2.0 * Math.PI * Math.Sqrt(
            kerbinOrbitRadius * kerbinOrbitRadius * kerbinOrbitRadius / KerbinMu) / 60.0;

        double scaledRateDegPerMin = HstSlewRateDegPerMinute * scale;
        double scaledAcquisitionMin = HstAcquisitionSeconds / 60.0 / scale;
        double kerbinFraction = (90.0 / scaledRateDegPerMin + scaledAcquisitionMin) / kerbinPeriodMin;

        Check("a 90 deg repoint costs the same fraction of an orbit as HST's does",
              kerbinFraction, hstFraction, 0.02, "of an orbit");

        // And the number the player actually waits for, which is what the literal transplant got
        // catastrophically wrong: at 6 deg/min it was 15 minutes out of a ~31 minute orbit, so a
        // target clear of the limb at the click was behind the planet on arrival.
        Check("the literal figure would have cost half a Kerbin orbit",
              (90.0 / HstSlewRateDegPerMinute) / kerbinPeriodMin, 0.48, 0.05, "of an orbit");
        Check("the transplanted one costs about a seventh of it",
              (90.0 / scaledRateDegPerMin) / kerbinPeriodMin, 0.148, 0.02, "of an orbit");

        Assert("a degenerate body falls back to no scaling at all",
               SlewDynamics.UniverseTimeScale(0.0, 0.0) == 1.0);
    }

    // ------------------------------------------------------------------ 2

    private static void ProfileShape()
    {
        const double Alpha = 0.05;                 // deg/s^2, chosen so the crossover is convenient
        double torque = 1000.0;
        double inertia = torque / (Alpha * Math.PI / 180.0);
        double omegaMax = 0.5;                     // deg/s

        Check("the acceleration is torque over inertia",
              SlewDynamics.AngularAccelerationDegPerSecond2(torque, inertia), Alpha, 1e-9, "deg/s^2");

        // The two branches are different formulae and they have to meet: at theta = w^2/alpha the
        // triangular profile's peak rate is exactly the ceiling, and both give 2w/alpha.
        double crossover = omegaMax * omegaMax / Alpha;
        SlewProfile at = SlewDynamics.Compute(crossover, torque, inertia, omegaMax, 0.0);
        SlewProfile below = SlewDynamics.Compute(crossover * 0.999999, torque, inertia, omegaMax, 0.0);
        SlewProfile above = SlewDynamics.Compute(crossover * 1.000001, torque, inertia, omegaMax, 0.0);

        Check("the profile is continuous across the crossover, from below",
              below.ManoeuvreSeconds, at.ManoeuvreSeconds, 1e-3, "s");
        Check("the profile is continuous across the crossover, from above",
              above.ManoeuvreSeconds, at.ManoeuvreSeconds, 1e-3, "s");
        Check("and both branches give 2 w / alpha there",
              at.ManoeuvreSeconds, 2.0 * omegaMax / Alpha, 1e-6, "s");
        Assert("below the crossover the vehicle never reaches its ceiling", !below.RateLimited);
        Assert("above it, it does", above.RateLimited);

        // Monotone in the angle, which is the one property a player will notice being wrong.
        double previous = -1.0;
        bool monotone = true;
        for (double angle = 0.5; angle <= 180.0; angle += 0.5)
        {
            double t = SlewDynamics.Compute(angle, torque, inertia, omegaMax, 0.0).ManoeuvreSeconds;
            if (t < previous) monotone = false;
            previous = t;
        }
        Assert("a bigger repoint never takes less time", monotone);

        // No torque is not a slow slew.
        SlewProfile dead = SlewDynamics.Compute(30.0, 0.0, inertia, omegaMax, 10.0);
        Assert("a vehicle with no control torque reports an infinite manoeuvre",
               double.IsInfinity(dead.ManoeuvreSeconds));

        // A repoint of zero angle still pays the acquisition.
        SlewProfile nudge = SlewDynamics.Compute(0.0, torque, inertia, omegaMax, HstAcquisitionSeconds);
        Check("a zero-angle repoint still costs a guide star acquisition",
              nudge.TotalSeconds, HstAcquisitionSeconds, 1e-9, "s");
    }

    // ------------------------------------------------------------------ 3

    private static void SlewCost()
    {
        const double WheelChargePerSecond = 0.75;   // the part config's own RESOURCE rate

        // AT THE RATE THE GAME REALLY PLANS WITH, which is the published one through the universe's
        // time scale (section 1b). Costing the unscaled figure here would have this section
        // asserting the price of a manoeuvre nothing ever flies.
        double scale = SlewDynamics.UniverseTimeScale(KerbinRadius, KerbinMu);
        double rate = HstSlewRateDegPerSecond * scale;
        double acquisition = HstAcquisitionSeconds / scale;

        SlewProfile ninety = SlewDynamics.Compute(90.0, PartTorqueNm, PartInertia, rate, acquisition);
        SlewProfile oneEighty = SlewDynamics.Compute(180.0, PartTorqueNm, PartInertia, rate, acquisition);

        Assert("both are rate-limited", ninety.RateLimited && oneEighty.RateLimited);
        Assert("the 180 deg repoint really does take about twice as long",
               oneEighty.ManoeuvreSeconds > 1.8 * ninety.ManoeuvreSeconds);

        // The charge follows the manoeuvre, which is the rule KSP itself bills a loaded vessel's
        // wheels by: a repoint ordered from the observatory costs what the same repoint costs
        // flown by hand. See ReactionWheelChargeUnits for the version of this that was wrong.
        double ninetyEc = SlewDynamics.ReactionWheelChargeUnits(in ninety, WheelChargePerSecond);
        double oneEightyEc = SlewDynamics.ReactionWheelChargeUnits(in oneEighty, WheelChargePerSecond);
        Check("a 90 deg repoint costs its 15 minutes at the wheels' own rate",
              ninetyEc, WheelChargePerSecond * ninety.ManoeuvreSeconds, 1e-9, "EC");
        Check("and a 180 deg one costs about twice that",
              oneEightyEc / ninetyEc, 2.0, 0.05, "x");

        // THE BALANCE CONSEQUENCE, stated as a check so it cannot drift unnoticed. At the game's
        // own slew rate a 90 degree repoint is about half the part's own 400 EC battery: a real
        // constraint that an unprepared telescope survives, rather than one it cannot pay at all.
        // (Before the scale transplant of section 1b it was 675 EC, more than the battery holds,
        // which was a consequence of the manoeuvre being three times too long rather than of any
        // decision about balance.)
        Assert($"a 90 deg repoint costs {ninetyEc:F0} EC, a serious bite out of the part's own 400 EC",
               ninetyEc > 100.0 && ninetyEc < 400.0);

        // The torquing time is the two ramps and nothing else. Not what the charge is billed on
        // any more, but it is what decides which branch of the profile the vehicle is on.
        Check("torquing time is 2 w / alpha",
              SlewDynamics.TorquingSeconds(in ninety),
              2.0 * ninety.PeakRateDegPerSecond / ninety.AccelerationDegPerSecond2, 1e-9, "s");

        // WHERE THE SHIPPED PART ACTUALLY SITS, and it is worth stating because it is not what you
        // would guess. Its wheels are 12 kN m an axis against an inertia of about 79 000 kg m^2,
        // which is 8.7 deg/s^2: some three orders of magnitude more angular acceleration than HST's real
        // wheels manage, because KSP's reaction wheels are balanced for flying rockets and not for
        // holding a telescope still. The crossover between the two branches therefore falls at
        // w^2/alpha, a couple of arcseconds, so EVERY repoint a player will ever command is
        // rate-limited and the PUBLISHED ceiling is what governs the time, exactly as it does on
        // the real spacecraft. The torque figure in the part config cannot make repointing free.
        double crossoverDeg = rate * rate
                            / SlewDynamics.AngularAccelerationDegPerSecond2(PartTorqueNm, PartInertia);
        Assert("the shipped part's torque/rate crossover is below its own pointing tolerance "
             + $"({crossoverDeg * 3600.0:F1} arcsec)", crossoverDeg * 3600.0 < 81.0);
        Assert("so a repoint the size of one field of view is already rate-limited",
               SlewDynamics.Compute(81.0 / 3600.0, PartTorqueNm, PartInertia, rate, 0.0).RateLimited);

        // A torque-limited slew torques throughout, so there the cost IS the duration. Below the
        // crossover to reach that branch at all on this vehicle.
        SlewProfile small = SlewDynamics.Compute(crossoverDeg * 0.5, PartTorqueNm, PartInertia, rate, 0.0);
        Assert("below the crossover the manoeuvre is torque-limited", !small.RateLimited);
        Check("and then the wheels work for the whole manoeuvre",
              SlewDynamics.TorquingSeconds(in small), small.ManoeuvreSeconds, 1e-9, "s");

        // Thruster impulse: momentum in and back out again, over the moment arm.
        double impulse = SlewDynamics.ThrusterImpulseNewtonSeconds(PartInertia, ninety.PeakRateDegPerSecond,
                                                                  PartRadiusMeters);
        double expected = 2.0 * PartInertia * (ninety.PeakRateDegPerSecond * Math.PI / 180.0) / PartRadiusMeters;
        Check("thruster impulse is 2 J omega / r", impulse, expected, 1e-9, "N s");

        // And the propellant that impulse costs, from I = m Isp g0.
        double massKg = SlewDynamics.PropellantMassKg(impulse, 240.0);
        Check("propellant mass inverts I = m Isp g0", massKg * 240.0 * 9.80665, impulse, 1e-9, "N s");
        Assert("a thruster-pointed telescope spends real propellant on one repoint", massKg > 0.0);
    }

    // ------------------------------------------------------------------ 4

    private static void ProfileIntegral()
    {
        double torque = 1000.0;
        double inertia = torque / (0.05 * Math.PI / 180.0);
        double omegaMax = 0.5;

        foreach (double angle in new[] { 3.0, 90.0, 179.0 })
        {
            SlewProfile p = SlewDynamics.Compute(angle, torque, inertia, omegaMax, 0.0);
            string tag = $"{angle:F0} deg ({(p.RateLimited ? "rate" : "torque")}-limited)";

            Check($"{tag}: nothing covered at t = 0",
                  SlewDynamics.FractionOfAngleCovered(in p, 0.0), 0.0, 1e-12, "");
            Check($"{tag}: all of it covered at the end",
                  SlewDynamics.FractionOfAngleCovered(in p, p.ManoeuvreSeconds), 1.0, 1e-12, "");

            // The profile is symmetric in time about its midpoint, whichever branch it took, so
            // exactly half the angle is behind the vehicle when half the time has passed.
            Check($"{tag}: half the angle at half the time",
                  SlewDynamics.FractionOfAngleCovered(in p, 0.5 * p.ManoeuvreSeconds), 0.5, 1e-9, "");

            // Independently: integrating the rate profile numerically has to reproduce the closed
            // form, which is what actually establishes that the piecewise expression is right.
            const int Steps = 200000;
            double dt = p.ManoeuvreSeconds / Steps;
            double covered = 0.0;
            double ramp = p.PeakRateDegPerSecond / p.AccelerationDegPerSecond2;
            for (int i = 0; i < Steps; i++)
            {
                double t = (i + 0.5) * dt;
                double rate;
                if (t <= ramp) rate = p.AccelerationDegPerSecond2 * t;
                else if (t >= p.ManoeuvreSeconds - ramp)
                    rate = p.AccelerationDegPerSecond2 * (p.ManoeuvreSeconds - t);
                else rate = p.PeakRateDegPerSecond;
                covered += rate * dt;
            }
            Check($"{tag}: the integrated rate profile covers the commanded angle",
                  covered, p.AngleDeg, p.AngleDeg * 1e-6, "deg");

            // The rate is the derivative of the covered angle, so integrating it has to reproduce
            // the profile. This is what makes a frame taken mid-slew streak by the right amount.
            Check($"{tag}: the rate is zero at both ends",
                  SlewDynamics.RateDegPerSecondAt(in p, 0.0)
                + SlewDynamics.RateDegPerSecondAt(in p, p.ManoeuvreSeconds), 0.0, 1e-12, "deg/s");
            Check($"{tag}: it peaks at the profile's own peak",
                  SlewDynamics.RateDegPerSecondAt(in p, 0.5 * p.ManoeuvreSeconds),
                  p.PeakRateDegPerSecond, 1e-9, "deg/s");

            bool monotone = true;
            double last = -1.0;
            for (int i = 0; i <= 500; i++)
            {
                double f = SlewDynamics.FractionOfAngleCovered(in p, p.ManoeuvreSeconds * i / 500.0);
                if (f < last - 1e-12) monotone = false;
                last = f;
            }
            Assert($"{tag}: the boresight never goes backwards", monotone);
        }
    }

    // ------------------------------------------------------------------ 5

    private static void Eclipse()
    {
        // HST Primer, Orbital Constraints. The Primer's own altitude and period.
        const double EarthRadiusKm = 6371.0;
        const double HstAltitudeKm = 540.0;
        const double HstPeriodMinutes = 96.0;
        double r = (EarthRadiusKm + HstAltitudeKm) * 1000.0;
        double bodyR = EarthRadiusKm * 1000.0;

        // Vallado's closed form, written out here independently of the mod's implementation, which
        // delegates to OrbitalVisibility instead. If the delegation is wrong this is what catches it.
        double VallodoFraction(double beta)
        {
            double cosBeta = Math.Cos(Math.Abs(beta) * Math.PI / 180.0);
            double arg = Math.Sqrt(r * r - bodyR * bodyR) / (r * cosBeta);
            if (arg >= 1.0) return 0.0;
            return Math.Acos(arg) / Math.PI;
        }

        for (double beta = 0.0; beta < 90.0; beta += 3.0)
        {
            if (Math.Abs(OrbitalPowerBudget.EclipsedOrbitFraction(r, bodyR, beta) - VallodoFraction(beta)) > 1e-12)
            {
                Fail($"eclipse fraction departs from Vallado's closed form at beta = {beta:F0} deg");
                break;
            }
        }
        Pass("the eclipse fraction is Vallado's closed form at every beta from 0 to 90 deg");

        // Against the published occultation duration: STScI gives HST up to about 36 minutes of
        // Earth occultation in a 96 minute orbit for a target in the orbital plane, which is the
        // same geometry the spacecraft's own shadow obeys.
        double eclipsedMinutes = OrbitalPowerBudget.EclipsedOrbitFraction(r, bodyR, 0.0) * HstPeriodMinutes;
        Check("HST's in-plane eclipse against the Primer's ~36 min occultation",
              eclipsedMinutes, 36.0, 1.5, "minutes");

        // The beta at which the shadow misses the orbit entirely has to be the same angle at which
        // OrbitalVisibility says a target is in the continuous viewing zone. Two functions written
        // for different questions, and the boundary is one number.
        double rho = OrbitalVisibility.AngularRadiusDeg(bodyR, r);
        double cvz = OrbitalVisibility.ContinuousViewingHalfWidthDeg(rho);
        double betaStar = 90.0 - cvz;
        Check("full sunlight begins exactly where continuous viewing does",
              OrbitalPowerBudget.EclipsedOrbitFraction(r, bodyR, betaStar + 1e-9), 0.0, 1e-12, "");
        Assert("and just inside it there is still an eclipse",
               OrbitalPowerBudget.EclipsedOrbitFraction(r, bodyR, betaStar - 0.5) > 0.0);

        // The degenerate end: an orbit grazing the surface at beta = 0 is in shadow half the time.
        Check("a grazing orbit is eclipsed for half of itself",
              OrbitalPowerBudget.EclipsedOrbitFraction(bodyR * 1.0000001, bodyR, 0.0), 0.5, 1e-3, "");

        // Higher is sunnier, always.
        bool falls = true;
        double previous = 1.0;
        for (double alt = 200.0; alt <= 40000.0; alt += 200.0)
        {
            double f = OrbitalPowerBudget.EclipsedOrbitFraction((EarthRadiusKm + alt) * 1000.0, bodyR, 0.0);
            if (f > previous + 1e-12) falls = false;
            previous = f;
        }
        Assert("the eclipsed fraction falls monotonically with altitude", falls);

        // Beta from the geometry: an orbit normal pointing at the Sun is a face-on orbit, beta 90.
        Check("an orbit normal along the Sun line is beta 90",
              OrbitalPowerBudget.BetaAngleDeg(new SkyVector(0, 0, 1), new SkyVector(0, 0, 1)), 90.0, 1e-9, "deg");
        Check("an orbit normal across it is beta 0",
              OrbitalPowerBudget.BetaAngleDeg(new SkyVector(0, 0, 1), new SkyVector(1, 0, 0)), 0.0, 1e-9, "deg");
    }

    // ------------------------------------------------------------------ 6

    private static void Ledger()
    {
        const double Capacity = 400.0;   // the part config's own battery

        // Net positive, and it stops at the top of the battery rather than banking sunlight.
        double full = OrbitalPowerBudget.Advance(390.0, Capacity, 1.0, 0.6, 0.35, 100000.0);
        Check("a charging telescope stops at its battery capacity", full, Capacity, 1e-9, "EC");

        // Net negative, and it stops at empty rather than owing energy.
        double flat = OrbitalPowerBudget.Advance(10.0, Capacity, 0.0, 0.6, 0.35, 100000.0);
        Check("a discharging one stops at empty", flat, 0.0, 1e-9, "EC");

        // In between, it is exactly the net rate times the time.
        double charge = OrbitalPowerBudget.Advance(200.0, Capacity, 1.0, 0.6, 0.35, 100.0);
        Check("the net rate is generation times sunlit fraction, minus draw",
              charge, 200.0 + (1.0 * 0.6 - 0.35) * 100.0, 1e-9, "EC");

        // EnduranceSeconds and Advance are two views of one integration and must agree: advancing
        // by the endurance has to land exactly on the reserve.
        double endurance = OrbitalPowerBudget.EnduranceSeconds(300.0, 50.0, 0.2, 0.5, 0.35);
        double landed = OrbitalPowerBudget.Advance(300.0, Capacity, 0.2, 0.5, 0.35, endurance);
        Check("advancing by the endurance lands on the reserve", landed, 50.0, 1e-6, "EC");

        Assert("a telescope whose panels cover its load lasts forever",
               double.IsInfinity(OrbitalPowerBudget.EnduranceSeconds(100.0, 0.0, 1.0, 0.5, 0.4)));

        // The reserve is a floor on affordability, not on the arithmetic.
        Assert("a slew that would land on the reserve exactly is affordable",
               OrbitalPowerBudget.CanAfford(100.0, 50.0, 50.0));
        Assert("one that would go a hair below it is not",
               !OrbitalPowerBudget.CanAfford(100.0, 50.1, 50.0));

        // A telescope left alone for a decade must not come back with a negative battery, whatever
        // the load: the clamp is what makes the catch-up on the ledger safe at any warp.
        double decade = OrbitalPowerBudget.Advance(5.0, Capacity, 0.0, 0.0, 100.0, 315360000.0);
        Assert("a decade of catch-up cannot drive the battery negative", decade >= 0.0);
    }

    // ------------------------------------------------------------------ harness

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
    }

    private static void Check(string what, double got, double expected, double tolerance, string unit)
    {
        checks++;
        bool ok = Math.Abs(got - expected) <= tolerance;
        if (!ok) failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}: {got:G8} vs {expected:G8} {unit}".TrimEnd());
    }

    private static void Assert(string what, bool condition)
    {
        checks++;
        if (!condition) failures++;
        Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {what}");
    }

    private static void Pass(string what) => Assert(what, true);

    private static void Fail(string what) => Assert(what, false);
}

using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The power ledger of a telescope nobody is flying.
    ///
    /// KSP does not simulate an unloaded vessel's resources: its batteries hold whatever they held
    /// when the player looked away, its panels produce nothing, its consumers consume nothing. The
    /// ground-operations mode has the telescope unloaded by definition, so charging a slew against
    /// a battery that never discharges is not a constraint, and discharging one that never
    /// recharges is a telescope that works twice and is then scrap. Both halves are modelled here,
    /// because either alone is worse than neither.
    ///
    /// The illumination is orbit-averaged: a vessel the game is not simulating has no meaningful
    /// instant, and the ledger is advanced across gaps that at high warp are many orbits.
    ///
    /// Charge is in KSP's ElectricCharge units throughout; nothing converts them to anything.
    /// Eclipse geometry from Vallado, cross-checked in the harness against HST's own published
    /// occultation.
    /// </summary>
    public static class OrbitalPowerBudget
    {
        private const double DegToRad = Math.PI / 180.0;
        private const double RadToDeg = 180.0 / Math.PI;

        /// <summary>
        /// Fraction of a circular orbit spent in the host body's shadow, from the beta angle
        /// between the orbit plane and the Sun.
        ///
        /// THIS IS THE OCCULTATION RELATION AGAIN. Vallado's shadow fraction is
        /// acos(sqrt(a^2-R^2)/(a cos beta))/pi; OrbitalVisibility.OccultedOrbitFraction, derived
        /// for "how much of the orbit does the planet hide this target for", is
        /// acos(cos rho / cos beta)/pi with rho = asin(R/a), so cos rho = sqrt(a^2-R^2)/a and the
        /// two are identical term for term. One question (a cone of half-angle rho blocking a great
        /// circle tilted by beta) asked about a star in one case and the Sun in the other. So it
        /// delegates; the harness carries Vallado's form written out independently and asserts the
        /// identity, which is the check duplicating the formula would have been there to earn.
        /// </summary>
        public static double EclipsedOrbitFraction(double orbitRadiusMeters, double bodyRadiusMeters,
                                                   double betaAngleDeg)
        {
            if (!(orbitRadiusMeters > 0.0) || !(bodyRadiusMeters > 0.0)) return 0.0;
            if (orbitRadiusMeters <= bodyRadiusMeters) return 0.5;   // inside the body; degenerate

            return OrbitalVisibility.OccultedOrbitFraction(
                OrbitalVisibility.AngularRadiusDeg(bodyRadiusMeters, orbitRadiusMeters), betaAngleDeg);
        }

        /// <summary>
        /// Beta angle from the orbit normal and the direction to the Sun. Ninety degrees is an
        /// orbit seen face-on from the Sun and never eclipsed; zero is edge-on and eclipsed most.
        /// </summary>
        public static double BetaAngleDeg(SkyVector orbitNormal, SkyVector toSun)
        {
            double nm = Math.Sqrt(orbitNormal.X * orbitNormal.X + orbitNormal.Y * orbitNormal.Y
                                + orbitNormal.Z * orbitNormal.Z);
            double sm = Math.Sqrt(toSun.X * toSun.X + toSun.Y * toSun.Y + toSun.Z * toSun.Z);
            if (!(nm > 0.0) || !(sm > 0.0)) return 0.0;

            double cos = (orbitNormal.X * toSun.X + orbitNormal.Y * toSun.Y + orbitNormal.Z * toSun.Z) / (nm * sm);
            if (cos > 1.0) cos = 1.0; else if (cos < -1.0) cos = -1.0;
            return 90.0 - Math.Acos(cos) * RadToDeg;
        }

        /// <summary>
        /// Charge after a stretch of time under a constant load and orbit-averaged supply.
        ///
        /// Clamped at both ends: the top stops a telescope left alone for a decade banking a decade
        /// of sunlight, the bottom stops the ledger owing energy it can never repay. That is what
        /// makes an arbitrarily long catch-up safe.
        /// </summary>
        public static double Advance(double chargeUnits, double capacityUnits,
                                     double generationPerSecondFullSun, double sunlitFraction,
                                     double drawPerSecond, double seconds)
        {
            if (!(seconds > 0.0) || double.IsNaN(seconds) || double.IsInfinity(seconds))
                return Clamp(chargeUnits, capacityUnits);

            double net = Math.Max(0.0, generationPerSecondFullSun) * Clamp01(sunlitFraction)
                       - Math.Max(0.0, drawPerSecond);
            return Clamp(chargeUnits + net * seconds, capacityUnits);
        }

        /// <summary>Whether a one-off demand can be paid without taking the battery below its reserve.</summary>
        public static bool CanAfford(double chargeUnits, double demandUnits, double reserveUnits)
        {
            if (!(demandUnits > 0.0)) return true;
            return chargeUnits - demandUnits >= Math.Max(0.0, reserveUnits);
        }

        /// <summary>
        /// Seconds of a given draw the battery sustains, net of supply, before hitting the reserve.
        /// Infinite when the supply covers the draw, which is the ordinary case for a telescope
        /// idling with working panels.
        /// </summary>
        public static double EnduranceSeconds(double chargeUnits, double reserveUnits,
                                              double generationPerSecondFullSun, double sunlitFraction,
                                              double drawPerSecond)
        {
            double net = Math.Max(0.0, drawPerSecond)
                       - Math.Max(0.0, generationPerSecondFullSun) * Clamp01(sunlitFraction);
            if (!(net > 0.0)) return double.PositiveInfinity;

            double usable = chargeUnits - Math.Max(0.0, reserveUnits);
            return usable > 0.0 ? usable / net : 0.0;
        }

        private static double Clamp(double v, double capacity)
        {
            if (double.IsNaN(v) || v < 0.0) return 0.0;
            return capacity > 0.0 && v > capacity ? capacity : v;
        }

        private static double Clamp01(double v) => v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v);
    }
}

using System;

namespace ExoInstruments.Core
{
    /// <summary>
    /// The instrument's total spectral response, integrated over the passband -- what turns an
    /// apparent magnitude into a real electron count.
    ///
    /// WHAT THIS REPLACES. The photometry used to be a product of scalars:
    ///
    ///     N_e = F0 * 10^(-0.4 m) * FWHM * QE_peak * T_extinction(lambda_c) * area * time
    ///
    /// Every factor after the magnitude is evaluated at one wavelength, or is a single headline
    /// number standing in for a curve. That is the standard back-of-envelope exposure-time
    /// calculation, and it is wrong in three ways that matter here, all in the same direction:
    ///
    ///   * QE_peak is by definition the detector's best wavelength. FORS2's own published curve
    ///     runs 58% at 400nm against 86% at its 600nm peak, so a b_HIGH exposure was credited
    ///     with about 1.5x the electrons it really collects.
    ///   * Extinction at lambda_c alone. The Luminance position is 7700 Angstrom wide on FORS2
    ///     and the extinction coefficient varies by a factor of three across it (k_B 0.38 vs
    ///     k_R 0.13, see AtmosphericImagingNoise), so one sample cannot represent the band.
    ///   * No optical throughput at all. Every photon entering the aperture was collected: no
    ///     mirror reflectivity, no filter peak transmission, no relay optics. A real 0.51m
    ///     instrument does not reach the limiting magnitude that implies.
    ///
    /// WHAT IT DOES INSTEAD. The same integral a real synthetic-photometry package performs
    /// (synphot, GalSim's Bandpass, SkyMaker): the source's photon spectrum is multiplied by the
    /// full system response and integrated over wavelength.
    ///
    ///     N_e = area * time * integral[ n_lambda(lambda) * S(lambda) ] d lambda
    ///     S(lambda) = T_filter * T_optics * QE(lambda) * T_atmosphere(lambda, X) * T_nd
    ///
    /// The source's absolute scale still comes from its measured magnitude, by normalising its
    /// spectral SHAPE at the Johnson V effective wavelength where that magnitude is defined:
    /// n_lambda(lambda_V) = F0 * 10^(-0.4 m). Factoring that out leaves a single number that
    /// carries the whole integral:
    ///
    ///     W = integral[ (phi(lambda)/phi(lambda_V)) * S(lambda) ] d lambda      [Angstrom]
    ///     N_e = F0 * 10^(-0.4 m) * W * area * time
    ///
    /// which has exactly the shape of the old formula with (FWHM * QE * T) replaced by W. So W
    /// is the system's real EFFECTIVE PHOTOMETRIC WIDTH for this particular source: what the
    /// passband is worth once the detector, the optics, the air and the source's own colour are
    /// all accounted for.
    ///
    /// The construction is deliberately reducible: for a flat source spectrum, a grey QE and a
    /// grey atmosphere, the integral collapses to W = FWHM * QE * T and reproduces the previous
    /// model exactly. That is asserted in the headless harness, so the new path is provably a
    /// generalisation of the old one rather than a different model that happens to look similar.
    ///
    /// THE FILTER PROFILE IS NOT INVENTED. Only the published numbers are used: a top-hat of the
    /// filter's own published FWHM at its own published central wavelength, scaled by its
    /// published peak transmission. A top-hat's equivalent width IS its FWHM, so the
    /// normalisation reproduces the published figure by construction, and no shape this pipeline
    /// has no measurement for is imposed. Real interference filters are flat-topped with steep
    /// edges, so this is also the right first-order shape; what is gained here is the
    /// integration of everything ELSE across the band, not a pretend filter profile.
    ///
    /// Pure C#, no Unity dependency. Immutable once built, and safe to read from the background
    /// imaging thread.
    /// </summary>
    public sealed class SystemResponse
    {
        /// <summary>
        /// Quadrature nodes across the passband (Simpson's rule, so this must be even). 64 is
        /// set by the widest band in the roster rather than by the narrowest: FORS2's unfiltered
        /// Luminance position spans 7700 Angstrom, across which both the QE curve and the
        /// extinction coefficient change substantially, while a 5.5nm H-alpha filter is
        /// essentially grey across its own width and would be converged at four.
        /// </summary>
        private const int IntegrationSteps = 64;

        /// <summary>Coolest and hottest blackbody the colour table covers, and how many log-spaced entries it holds.</summary>
        private const double TableMinTeffK = 1500.0;
        private const double TableMaxTeffK = 60000.0;
        /// <summary>
        /// Entries in the colour table. Set by accuracy rather than by taste: interpolation error
        /// falls as the square of the log-temperature spacing, and at 48 entries the table
        /// reproduced the directly-integrated colour term to only 0.14%, which is visible in a
        /// harness check asserting equivalence with the model this replaced. 160 brings it under
        /// 0.02% at a build cost still well inside a millisecond, paid once per capture.
        /// </summary>
        private const int TableEntries = 160;

        private readonly double centralWavelengthMeters;
        private readonly double widthMeters;
        private readonly double greyTransmission;      // filter peak * optics * ND: constant across the band
        private readonly SpectralCurve quantumEfficiencyCurve;   // may be null
        private readonly double scalarQuantumEfficiency;
        private readonly double airmass;
        private readonly double siteAltitudeMeters;

        private readonly double[] tableTeffK;
        private readonly double[] tableWidthAngstrom;
        private readonly double[] tableWidthAngstromNoExtinction;

        /// <summary>
        /// Effective photometric width (Angstrom) for a source with a FLAT photon spectrum --
        /// i.e. one whose colour is unknown and therefore not assumed. Includes the atmosphere.
        /// </summary>
        public double EffectiveWidthAngstromFlat { get; private set; }

        /// <summary>
        /// The same, with the atmosphere left out. For callers that already hold their own
        /// transmission factor and must apply it themselves -- the sky background, where each
        /// term (airglow inside the atmosphere, zodiacal light outside it, moonlight and
        /// twilight already measured through it) is attenuated differently and the distinction
        /// is the physics, not a detail. See SkyBrightnessModel.
        /// </summary>
        public double EffectiveWidthAngstromFlatNoExtinction { get; private set; }

        /// <summary>Central wavelength of the fitted filter, in metres -- carried so callers need not hold it separately.</summary>
        public double CentralWavelengthMeters => centralWavelengthMeters;

        /// <summary>
        /// Builds the response for one filter on one instrument at one airmass.
        ///
        /// greyOpticsTransmission is everything with no published wavelength dependence in this
        /// roster: the filter's own peak transmission, the mirror-coating and relay-optics
        /// throughput (see VisualTelescopeSpec.OpticsTransmission), and any neutral-density
        /// filter. Passing them pre-multiplied rather than as separate arguments keeps this class
        /// from having an opinion about which factors an instrument happens to have.
        ///
        /// quantumEfficiencyCurve may be null, in which case scalarQuantumEfficiency is used
        /// across the band -- the honest treatment for a detector whose manufacturer publishes
        /// only a peak figure, which is the case for this roster's amateur camera.
        /// </summary>
        /// <summary>Measured filter transmission, or null for the published-numbers-only top-hat.</summary>
        private readonly SpectralCurve filterTransmissionCurve;

        /// <summary>
        /// Quadrature nodes when a measured curve is used. Four times the top-hat count: the
        /// support is an 870 nm range rather than one passband, and it contains a steep-shouldered
        /// peak plus a red leak, so the integrand is far from the smooth product a rectangle gives.
        /// </summary>
        private const int CurveIntegrationSteps = 256;

        public SystemResponse(
            double centralWavelengthMeters, double widthAngstrom,
            double greyOpticsTransmission,
            SpectralCurve quantumEfficiencyCurve, double scalarQuantumEfficiency,
            double airmass, double siteAltitudeMeters)
            : this(centralWavelengthMeters, widthAngstrom, greyOpticsTransmission,
                   null, quantumEfficiencyCurve, scalarQuantumEfficiency, airmass, siteAltitudeMeters)
        {
        }

        /// <summary>
        /// As above, but with the filter's REAL measured transmission curve in place of the
        /// top-hat (see FilterCurves).
        ///
        /// When a curve is supplied it carries the filter's shape AND its transmission, so
        /// greyOpticsTransmission must then be the optical train alone, without the filter's peak
        /// transmission folded in; the integration also runs over the curve's own support rather
        /// than over a nominal FWHM. centralWavelengthMeters and widthAngstrom are still carried,
        /// because the PSF and the display quote them, but they no longer drive the integral.
        ///
        /// filterTransmissionCurve = null takes the top-hat path and is exactly the previous
        /// behaviour, which is the right treatment for a filter whose maker publishes only a
        /// central wavelength, a width and a peak.
        /// </summary>
        public SystemResponse(
            double centralWavelengthMeters, double widthAngstrom,
            double greyOpticsTransmission,
            SpectralCurve filterTransmissionCurve,
            SpectralCurve quantumEfficiencyCurve, double scalarQuantumEfficiency,
            double airmass, double siteAltitudeMeters)
        {
            this.filterTransmissionCurve = filterTransmissionCurve;
            this.centralWavelengthMeters = centralWavelengthMeters;
            widthMeters = Math.Max(0.0, widthAngstrom) * 1e-10;
            greyTransmission = Math.Max(0.0, greyOpticsTransmission);
            this.quantumEfficiencyCurve = quantumEfficiencyCurve;
            scalarQuantumEfficiency = Math.Max(0.0, scalarQuantumEfficiency);
            this.scalarQuantumEfficiency = scalarQuantumEfficiency;
            this.airmass = airmass;
            this.siteAltitudeMeters = siteAltitudeMeters;

            EffectiveWidthAngstromFlat = Integrate(0.0, true);
            EffectiveWidthAngstromFlatNoExtinction = Integrate(0.0, false);

            // The colour table is built eagerly, for a thread-safety reason rather than a
            // performance one: the response is constructed on the main thread and then read by
            // the background pipeline for every star in the frame, so it must be fully
            // initialised before it crosses that boundary. Building it costs 48 x 64 integrand
            // evaluations, which is under a millisecond -- the same tabulate-once trick
            // OpticalPsf uses for its radial profiles, and for the same reason: the alternative
            // is repeating a quadrature per star, hundreds of times per wide-field frame.
            tableTeffK = new double[TableEntries];
            tableWidthAngstrom = new double[TableEntries];
            tableWidthAngstromNoExtinction = new double[TableEntries];
            double logMin = Math.Log(TableMinTeffK);
            double logMax = Math.Log(TableMaxTeffK);
            for (int i = 0; i < TableEntries; i++)
            {
                double t = (double)i / (TableEntries - 1);
                double teff = Math.Exp(logMin + t * (logMax - logMin));
                tableTeffK[i] = teff;
                tableWidthAngstrom[i] = Integrate(teff, true);
                tableWidthAngstromNoExtinction[i] = Integrate(teff, false);
            }
        }

        /// <summary>
        /// Effective photometric width (Angstrom) for a blackbody source at the given effective
        /// temperature -- a star of known colour, or a body reflecting sunlight (see
        /// SolarPhotosphereTemperatureK).
        ///
        /// This is where a star's colour enters the photometry. It is not a correction bolted on
        /// afterwards: the same integral that gives the electron count gives the colour term, so
        /// at the V effective wavelength it is exactly 1 by construction and the result reduces
        /// to the catalogue's measured magnitude. The model only ever interpolates away from a
        /// real measurement.
        ///
        /// Pass teffK &lt;= 0 for a source of unknown colour, which returns the flat-spectrum
        /// width rather than assuming a temperature.
        /// </summary>
        public double EffectiveWidthAngstromForTemperature(double teffK)
        {
            return LookUp(teffK, tableWidthAngstrom, EffectiveWidthAngstromFlat);
        }

        /// <summary>
        /// Effective width for a star of known INTRINSIC temperature seen through a known
        /// reddening: the Planck shape of the star it really is, times the extinction curve,
        /// normalised at V so its observed magnitude still sets the flux.
        ///
        /// Not tabulated. The colour table is a product grid over temperature, and adding a
        /// reddening axis to it would multiply its build cost by the number of nodes on that axis
        /// -- 48 ms per capture on the main thread for a grid worth having, against under a
        /// millisecond today. This runs one quadrature per call instead, which is 25 microseconds,
        /// and the caller is expected to cache across a frame (see ReddenedResponseCache). A frame
        /// covers one small solid angle, so its stars share a sight line and a handful of distinct
        /// reddenings serve all of them.
        /// </summary>
        public double EffectiveWidthAngstromForReddenedStar(double intrinsicTeffK, double eBv)
        {
            if (!(eBv > 0.0)) return EffectiveWidthAngstromForTemperature(intrinsicTeffK);
            return Integrate(Math.Max(0.0, intrinsicTeffK), true, eBv);
        }

        /// <summary>EffectiveWidthAngstromForTemperature with the atmosphere left out -- see EffectiveWidthAngstromFlatNoExtinction.</summary>
        public double EffectiveWidthAngstromForTemperatureNoExtinction(double teffK)
        {
            return LookUp(teffK, tableWidthAngstromNoExtinction, EffectiveWidthAngstromFlatNoExtinction);
        }

        private double LookUp(double teffK, double[] table, double flatFallback)
        {
            if (teffK <= 0.0 || double.IsNaN(teffK)) return flatFallback;
            if (teffK <= TableMinTeffK) return table[0];
            if (teffK >= TableMaxTeffK) return table[TableEntries - 1];

            // Interpolate in log T, which is the axis the table is laid out on: the Planck
            // shape's variation across a band is far closer to linear in log T than in T.
            double logMin = Math.Log(TableMinTeffK);
            double logMax = Math.Log(TableMaxTeffK);
            double position = (Math.Log(teffK) - logMin) / (logMax - logMin) * (TableEntries - 1);
            int i = (int)position;
            if (i >= TableEntries - 1) return table[TableEntries - 1];
            double frac = position - i;
            return table[i] + frac * (table[i + 1] - table[i]);
        }

        /// <summary>
        /// Simpson's rule over the filter's top-hat support. Returns Angstrom, since that is the
        /// unit the V-band photon flux density (948 photons/cm^2/s/Angstrom) is quoted per.
        /// </summary>
        private double Integrate(double teffK, bool includeExtinction, double eBv = 0.0)
        {
            if (greyTransmission <= 0.0) return 0.0;

            double start, end;
            if (filterTransmissionCurve != null)
            {
                // Integrate over the measured curve's own support. More nodes than the top-hat
                // path uses, because a real curve has structure -- shoulders, and a red leak far
                // from the passband -- that a rectangle does not, and the support is wider.
                start = filterTransmissionCurve.MinWavelengthMeters;
                end = filterTransmissionCurve.MaxWavelengthMeters;
            }
            else
            {
                if (widthMeters <= 0.0 || centralWavelengthMeters <= 0.0) return 0.0;
                start = centralWavelengthMeters - 0.5 * widthMeters;
                end = centralWavelengthMeters + 0.5 * widthMeters;
            }
            // A filter's stated central wavelength and width can in principle place its blue
            // edge at or below zero for an extremely wide nominal band; clamp rather than
            // integrate through a wavelength that has no physical meaning.
            if (start < 1e-9) start = 1e-9;
            if (end <= start) return 0.0;

            int steps = filterTransmissionCurve != null ? CurveIntegrationSteps : IntegrationSteps;
            double stepMeters = (end - start) / steps;
            double sum = 0.0;
            for (int i = 0; i <= steps; i++)
            {
                double lambda = start + i * stepMeters;
                double weight = (i == 0 || i == steps) ? 1.0 : (i % 2 == 1 ? 4.0 : 2.0);
                sum += weight * Integrand(lambda, teffK, includeExtinction, eBv);
            }

            double integralMeters = sum * stepMeters / 3.0;
            return integralMeters * 1e10; // metres -> Angstrom
        }

        /// <summary>
        /// The integrand: the source's photon spectral shape normalised at Johnson V, times the
        /// system's response at this wavelength.
        /// </summary>
        private double Integrand(double lambda, double teffK, bool includeExtinction, double eBv)
        {
            double shape = 1.0;
            if (teffK > 0.0)
            {
                double atV = StellarPhotometry.PhotonSpectralDensity(StellarPhotometry.JohnsonVWavelengthMeters, teffK);
                if (atV > 0.0)
                    shape = StellarPhotometry.PhotonSpectralDensity(lambda, teffK) / atV;
                else
                    shape = 0.0;
            }

            double qe = quantumEfficiencyCurve != null
                ? quantumEfficiencyCurve.At(lambda)
                : scalarQuantumEfficiency;

            double atmosphere = includeExtinction
                ? AtmosphericImagingNoise.ExtinctionTransmissionAt(airmass, lambda, siteAltitudeMeters)
                : 1.0;

            // With a measured curve the filter carries its own transmission at this wavelength;
            // with a top-hat it is flat across the band and already inside greyTransmission.
            double filter = filterTransmissionCurve != null ? filterTransmissionCurve.At(lambda) : 1.0;

            // Interstellar reddening enters as a SHAPE, normalised at V so the observed
            // magnitude stays the anchor -- see ReddenedStarSpectrum for why that is not double
            // counting. Exactly 1 when the caller has no reddening estimate.
            double reddening = eBv > 0.0
                ? ReddenedStarSpectrum.NormalisedTransmission(lambda, eBv, InterstellarExtinction.MilkyWayRv)
                : 1.0;

            return shape * reddening * filter * greyTransmission * qe * atmosphere;
        }
    }

    /// <summary>Shared spectral constants for sources whose spectrum is known on physical grounds rather than measured per object.</summary>
    public static class SourceSpectra
    {
        /// <summary>
        /// The Sun's effective temperature, 5772 K -- the nominal value fixed by IAU 2015
        /// Resolution B3 (Prsa et al. 2016, AJ 152, 41, "Nominal values for selected solar and
        /// planetary quantities").
        ///
        /// Used as the spectral shape of every source in the frame that shines by reflected
        /// sunlight: the planets and moons the camera photographs, and the moonlight, zodiacal
        /// and twilight terms of the sky background, all of which are sunlight scattered off
        /// something. This is a real improvement over treating them as flat: a solar spectrum is
        /// measurably not flat across a 7700 Angstrom band, and its shape is not in question the
        /// way an individual body's own colour is.
        ///
        /// What it does NOT claim: the reflecting surface is treated as grey, because no
        /// wavelength-resolved albedo exists for a KSP CelestialBody (see the albedo caveat in
        /// PhotonFluxModel). So the modelled spectrum is the Sun's, not the Sun's reddened by a
        /// real planetary reflectance.
        /// </summary>
        public const double SolarPhotosphereTemperatureK = 5772.0;
    }
}

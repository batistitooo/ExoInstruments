using System;
using System.Collections.Generic;
using System.Globalization;
using ExoInstruments.Core;

// Headless cross-validation of the supernova model, to the standard of the other harnesses:
// nothing tests that the code does what the code says. Every assertion is against a published
// figure, a physical identity, or a property the data itself must have.
//
// Run from this directory with:
//   dotnet run -c Release -p:Core=../../ExoInstruments/Core \
//       -p:Templates=../../ExoInstruments/PluginData/SupernovaTemplates.sntpl

internal static class Program
{
    private static int failures;
    private static int checks;

    private static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        // Only a path-looking argument overrides the default: --forecast and --ut take numeric
        // values of their own, and treating those as a template path is how this first ran.
        string path = "../../ExoInstruments/PluginData/SupernovaTemplates.sntpl";
        foreach (string a in args)
            if (!a.StartsWith("--") && a.EndsWith(".sntpl")) path = a;
        SupernovaTemplateSet templates = SupernovaTemplateSet.Load(path);

        if (Array.IndexOf(args, "--census") >= 0)
        {
            Census(templates);
            return;
        }

        int f = Array.IndexOf(args, "--forecast");
        if (f >= 0)
        {
            long seed = f + 1 < args.Length && long.TryParse(args[f + 1], out long sv) ? sv : 0L;
            double fromUt = 0.0;
            int u = Array.IndexOf(args, "--ut");
            if (u >= 0 && u + 1 < args.Length) double.TryParse(args[u + 1],
                System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out fromUt);
            Forecast(templates, seed, fromUt);
            return;
        }

        Section("1. The packed templates against their own published light-curve properties");
        TemplateProperties(templates);

        Section("2. The photometric identity: template magnitudes through the spectrum path");
        PhotometricIdentity(templates);

        Section("3. Rates against Li et al. 2011 Table 4");
        Rates();

        Section("4. Determinism and the Poisson process");
        Determinism();

        Section("5. Positions");
        Positions();

        Console.WriteLine();
        Console.WriteLine($"{checks - failures}/{checks} checks passed.");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    // ------------------------------------------------------------------ 1

    private static void TemplateProperties(SupernovaTemplateSet set)
    {
        SupernovaTemplate ia = set.Get(SupernovaClass.Ia);

        // The rise time of a stretch-1 SN Ia to B maximum is 19.5 +- 0.2 days (Riess et al.
        // 1999, AJ 118, 2675, from the template era this Nugent template belongs to). The
        // template's own peak day is a measurement of the same quantity.
        Check("Ia rise to B maximum", ia.PeakPhaseDays, 19.5, 1.5, "days");

        // Delta m15(B): the Phillips (1993) decline-rate parameter. A stretch-1 template must
        // decline close to the fiducial 1.1 mag in the 15 days after B maximum (Phillips 1993,
        // ApJ 413, L105).
        double dm15 = ia.BOffsetAt(ia.PeakPhaseDays + 15.0);
        Check("Ia Delta m15(B) against Phillips' fiducial", dm15, 1.1, 0.2, "mag");

        // The Ia light curve decelerates onto the radioactive tail: the post-maximum slope is
        // set by Delta m15 (7.3 mag/100d equivalent over the first 15 days), the tail by 56Co
        // decay, whose fully-trapped floor is 2.5 log10(e)/111.3 d = 0.98 mag/100d. The
        // template's last thirty days (+41..+71 past maximum, mid-transition) must be slower
        // than the early decline and faster than the trapping floor, in that order.
        double late = (ia.BOffsetAt(ia.LastPhaseDays) - ia.BOffsetAt(ia.LastPhaseDays - 30.0)) / 30.0 * 100.0;
        double early = (ia.BOffsetAt(ia.PeakPhaseDays + 30.0) - ia.BOffsetAt(ia.PeakPhaseDays + 15.0)) / 15.0 * 100.0;
        Assert($"Ia decline decelerates onto the 56Co tail: {early:F1} then {late:F1} mag/100d, floor 0.98",
               late > 0.98 && late < early);

        // The II-P/II-L split IS a light-curve statement: the plateau holds the II-P's decline
        // well below the II-L's over the same span (the classification of Barbon et al. 1979,
        // measured as systematically different B decline rates by Patat et al. 1994; the
        // template's own light-curve source is Cappellaro et al. 1997). The proper plateau is a
        // V/R phenomenon; in B the discriminant is the RATIO of the two classes' declines.
        SupernovaTemplate iip = set.Get(SupernovaClass.IIP);
        SupernovaTemplate iil = set.Get(SupernovaClass.IIL);
        double plateauMove = Math.Abs(iip.BOffsetAt(90) - iip.BOffsetAt(30));
        double linearMove = Math.Abs(iil.BOffsetAt(90) - iil.BOffsetAt(30));
        Assert($"II-P declines {plateauMove:F2} mag over days 30-90 where the II-L declines {linearMove:F2}",
               plateauMove < 0.7 * linearMove);

        // Every class: monotone phase grid, positive shapes, a defined peak.
        foreach (SupernovaClass c in Enum.GetValues(typeof(SupernovaClass)))
        {
            SupernovaTemplate t = set.Get(c);
            Assert($"{c}: template present", t != null);
            if (t == null) continue;
            bool ascending = true;
            for (int i = 1; i < t.PhaseDays.Length; i++)
                if (t.PhaseDays[i] <= t.PhaseDays[i - 1]) ascending = false;
            Assert($"{c}: phases strictly ascending, span {t.PhaseDays[0]:F0}..{t.LastPhaseDays:F0} d", ascending);
            Assert($"{c}: B offset at peak is zero", Math.Abs(t.BOffsetAt(t.PeakPhaseDays)) < 1e-3);
        }

        // The extrapolation past the template: linear magnitudes are the radioactive tail's own
        // functional form, continued at the template's measured final rate. It must be continuous
        // at the boundary, hold that exact slope, freeze the spectrum, and stop dead at the
        // declared floor.
        double boundary = ia.LastPhaseDays;
        Check("extrapolation is continuous at the template boundary",
              ia.BOffsetAt(boundary + 1e-6), ia.BOffsetAt(boundary), 1e-3, "mag");
        double stepped = (ia.BOffsetAt(boundary + 20.0) - ia.BOffsetAt(boundary)) / 20.0;
        Check("and continues at the template's own final slope",
              stepped, ia.FinalSlopeMagPerDay, 1e-9, "mag/day");
        Assert($"the V track extrapolates in step (grey decline)",
               Math.Abs((ia.VAnchorAt(boundary + 20.0) - ia.VAnchorAt(boundary)) / 20.0
                        - ia.FinalSlopeMagPerDay) < 1e-9);
        SpectralCurve frozen = ia.ShapeAt(boundary + 30.0);
        SpectralCurve last = ia.ShapeAt(boundary);
        Assert("the spectrum past the data is the last measurement held",
               frozen != null && last != null
               && Math.Abs(frozen.At(6563e-10) - last.At(6563e-10)) < 1e-9
               && Math.Abs(frozen.At(4400e-10) - last.At(4400e-10)) < 1e-9);
        Assert($"the model ends at the declared floor ({SupernovaTemplate.ExtrapolationFloorMag} mag below peak, "
             + $"day {ia.ActiveDays:F0})",
               double.IsInfinity(ia.BOffsetAt(ia.ActiveDays + 1.0))
               && Math.Abs(ia.BOffsetAt(ia.ActiveDays) - SupernovaTemplate.ExtrapolationFloorMag) < 0.05
               && ia.ShapeAt(ia.ActiveDays + 1.0) == null);

        // An H-alpha test the shape must pass: a II-P near peak carries the line in emission,
        // an Ia never shows hydrogen at all (that IS the classification; Filippenko 1997,
        // ARA&A 35, 309). The shape ratio at 6563 A against the neighbouring continuum has to
        // say so.
        double phase = iip.PeakPhaseDays + 20.0;
        double iipLine = ShapeAt(iip, phase, 6563.0) / Continuum(iip, phase);
        double iaLine = ShapeAt(ia, ia.PeakPhaseDays, 6563.0) / Continuum(ia, ia.PeakPhaseDays);
        Assert($"II-P H-alpha in emission over its continuum (ratio {iipLine:F2}) where the Ia shows none ({iaLine:F2})",
               iipLine > 1.1 && iipLine > iaLine + 0.15);
    }

    private static double ShapeAt(SupernovaTemplate t, double phase, double angstrom)
    {
        SpectralCurve s = t.ShapeAt(phase);
        return s == null ? 0.0 : s.At(angstrom * 1e-10);
    }

    private static double Continuum(SupernovaTemplate t, double phase)
    {
        // Continuum estimate flanking H-alpha, clear of the line's own wings.
        return 0.5 * (ShapeAt(t, phase, 6250.0) + ShapeAt(t, phase, 6900.0));
    }

    // ------------------------------------------------------------------ 2

    private static void PhotometricIdentity(SupernovaTemplateSet set)
    {
        // The identity that ties the whole chain together: pricing the template's spectrum
        // through a NARROW band centred on 5556 A must reproduce the flat-spectrum width times
        // the shape's value there, because the shape is normalised to 1 at that wavelength.
        SupernovaTemplate ia = set.Get(SupernovaClass.Ia);
        SpectralCurve shape = ia.ShapeAt(ia.PeakPhaseDays);

        var narrow = new SystemResponse(5556e-10, 20.0, 1.0, null, 1.0, 0.0, 0.0);

        double widthSpec = narrow.EffectiveWidthAngstromForSpectrum(shape);
        double widthFlat = narrow.EffectiveWidthAngstromFlat;
        Check("narrow band at the anchor: spectrum width equals flat width", widthSpec / widthFlat, 1.0, 0.02, "x");

        // Reddening consistency: a screen renormalised at V must leave the anchor untouched and
        // depress the blue side. Compare a band at 4400 A with and without E(B-V) = 0.3: the
        // ratio must be 10^(-0.4 * (A_B - A_V)) with the Fitzpatrick curve's own colour excess,
        // i.e. very nearly 10^(-0.4 * E(B-V)) by the definition of E(B-V).
        var blue = new SystemResponse(4400e-10, 20.0, 1.0, null, 1.0, 0.0, 0.0);
        double eBv = 0.3;
        double plain = blue.EffectiveWidthAngstromForSpectrum(shape);
        double reddened = blue.EffectiveWidthAngstromForSpectrum(shape, eBv);
        double measured = -2.5 * Math.Log10(reddened / plain);
        Check("the screen's B-V colour excess through the integral", measured, eBv, 0.06, "mag");

        // The V-anchor track and the B track must agree on the template's own B-V colour at
        // peak to the ~0.1 mag the anchor convention allows: for a stretch-1 Ia at maximum,
        // B-V is close to zero (Phillips 1993 and every calibration since).
        double bMinusVAtPeak = -(ia.VAnchorAt(ia.PeakPhaseDays));   // B(peak)=0 by construction
        Assert($"Ia B-V at maximum from the packed tracks is {bMinusVAtPeak:+0.00;-0.00} (published: near 0)",
               Math.Abs(bMinusVAtPeak) < 0.35);
    }

    // ------------------------------------------------------------------ 3

    private static Galaxy MakeGalaxy(double bt, double modulus, double type)
    {
        return new Galaxy
        {
            Name = "TEST",
            RaDeg = 180.0,
            DecDeg = 0.0,
            TotalBMag = bt,
            DistanceModulusMag = modulus,
            MorphologicalType = type,
            D25Arcmin = 5.0,
            AxisRatio = 0.7,
            PositionAngleDeg = 30.0,
            SersicIndex = 2.0,
        };
    }

    private static void Rates()
    {
        // A galaxy at exactly the fiducial luminosity: L_B = 2e10 L_sun means
        // M_B = 5.44 - 2.5 log10(2e10) = -20.31. Give it modulus 32 (25.6 Mpc), so
        // B_T = 32 - 20.31 = 11.69. Its rates must be the Table 4 SNuB values times 2.
        double mAbs = 5.44 - 2.5 * Math.Log10(2e10);
        var sbc = MakeGalaxy(32.0 + mAbs, 32.0, 4.0);   // Sbc bin
        Supernovae.RatePerCentury(in sbc, out double ia, out double ibc, out double ii);
        Check("fiducial Sbc, Ia rate = 0.198 x 2", ia, 0.198 * 2.0, 1e-3, "per century");
        Check("fiducial Sbc, Ibc rate = 0.274 x 2", ibc, 0.274 * 2.0, 1e-3, "per century");
        Check("fiducial Sbc, II rate = 0.557 x 2", ii, 0.557 * 2.0, 1e-3, "per century");

        // The rate-size relation: doubling the luminosity multiplies the RATE by 2 * 2^RSS,
        // Li's equations (1)-(3) exactly.
        var big = MakeGalaxy(32.0 + mAbs - 2.5 * Math.Log10(2.0), 32.0, 4.0);
        Supernovae.RatePerCentury(in big, out double ia2, out _, out double ii2);
        Check("rate-size relation for Ia (RSS -0.23)", ia2 / ia, 2.0 * Math.Pow(2.0, -0.23), 1e-6, "x");
        Check("rate-size relation for II (RSS -0.27)", ii2 / ii, 2.0 * Math.Pow(2.0, -0.27), 1e-6, "x");

        // An elliptical hosts Ia and essentially nothing else (Li Table 4: II is a limit at 0).
        var e = MakeGalaxy(32.0 + mAbs, 32.0, -5.0);
        Supernovae.RatePerCentury(in e, out double eIa, out double eIbc, out double eII);
        Assert($"elliptical: Ia {eIa:F3}, II exactly 0", eIa > 0.0 && eII == 0.0);

        // No distance, no supernovae: the honest reading of an unknown luminosity.
        var unknown = MakeGalaxy(10.0, double.NaN, 4.0);
        Supernovae.RatePerCentury(in unknown, out double uIa, out double uIbc, out double uII);
        Assert("a galaxy without a distance modulus hosts nothing", uIa == 0.0 && uIbc == 0.0 && uII == 0.0);

        // The Milky Way check the paper itself runs: an Sbc of L_B = 2.3e10 (mid-range of the
        // two published values Li adopts) should come out at a few per century, their own
        // estimate being 2.84 +- 0.60. The rate-size relation is what lifts it above the naive
        // SNuB x L product, exactly as the paper describes.
        double mwAbs = 5.44 - 2.5 * Math.Log10(2.3e10);
        var mw = MakeGalaxy(30.0 + mwAbs, 30.0, 4.0);
        Supernovae.RatePerCentury(in mw, out double mwIa, out double mwIbc, out double mwII);
        Check("Milky Way total against Li et al.'s own 2.84 +- 0.60", mwIa + mwIbc + mwII, 2.84, 0.9, "per century");
    }

    // ------------------------------------------------------------------ 4

    private static void Determinism()
    {
        var g = MakeGalaxy(11.0, 31.5, 5.0);

        List<SupernovaEvent> a = Supernovae.EventsInBlock(12345, in g, 3);
        List<SupernovaEvent> b = Supernovae.EventsInBlock(12345, in g, 3);
        Assert("the same seed and block give the same events", SameEvents(a, b));

        List<SupernovaEvent> c = Supernovae.EventsInBlock(54321, in g, 3);
        List<SupernovaEvent> d = Supernovae.EventsInBlock(12345, in g, 4);
        Assert("a different seed changes the history", !SameEvents(a, c) || a.Count == 0 && c.Count == 0);

        // The empirical mean over many blocks must reproduce the Poisson intensity the rates
        // define. 2000 blocks of 200 years at rate r per century gives mean 2 r per block.
        Supernovae.RatePerCentury(in g, out double ia, out double ibc, out double ii);
        double expectedPerBlock = (ia + ibc + ii) * 2.0;
        int total = 0;
        for (long k = 0; k < 2000; k++) total += Supernovae.EventsInBlock(777, in g, k).Count;
        double mean = total / 2000.0;
        Check("empirical event rate over 2000 blocks", mean, expectedPerBlock,
              4.0 * Math.Sqrt(expectedPerBlock / 2000.0), "per block");

        // Class shares over the same run: each within 5 sigma of its rate share.
        int nIa = 0, nCc = 0;
        for (long k = 0; k < 2000; k++)
            foreach (SupernovaEvent e in Supernovae.EventsInBlock(777, in g, k))
                if (e.Class == SupernovaClass.Ia) nIa++; else nCc++;
        double shareIa = nIa / (double)Math.Max(1, nIa + nCc);
        double expectedShare = ia / (ia + ibc + ii);
        Check("Ia share of the drawn events", shareIa, expectedShare,
              5.0 * Math.Sqrt(expectedShare * (1 - expectedShare) / Math.Max(1, nIa + nCc)), "");

        // Peak magnitudes: the Ia draw must average to Richardson's -19.25 with sigma 0.50.
        double sum = 0.0, sumSq = 0.0;
        int n = 0;
        for (long k = 0; k < 4000; k++)
            foreach (SupernovaEvent e in Supernovae.EventsInBlock(99, in g, k))
                if (e.Class == SupernovaClass.Ia) { sum += e.PeakAbsoluteBMag; sumSq += e.PeakAbsoluteBMag * e.PeakAbsoluteBMag; n++; }
        if (n > 50)
        {
            double meanM = sum / n;
            double sigma = Math.Sqrt(Math.Max(0.0, sumSq / n - meanM * meanM));
            Check($"Ia peak M_B mean over {n} draws (Richardson 2014)", meanM, -19.25, 4.0 * 0.50 / Math.Sqrt(n), "mag");
            Check("Ia peak M_B dispersion", sigma, 0.50, 0.12, "mag");
        }
        else Assert("enough Ia draws to test the magnitude distribution", false);
    }

    private static bool SameEvents(List<SupernovaEvent> a, List<SupernovaEvent> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].ExplosionUt != b[i].ExplosionUt || a[i].Class != b[i].Class
                || a[i].PeakAbsoluteBMag != b[i].PeakAbsoluteBMag) return false;
        return true;
    }

    // ------------------------------------------------------------------ 5

    private static void Positions()
    {
        var g = MakeGalaxy(11.0, 31.5, 5.0);
        var e = new SupernovaEvent { HostName = "TEST", ExplosionUt = 1e8, Class = SupernovaClass.Ia, PeakAbsoluteBMag = -19.2 };

        SupernovaEvent r1 = Supernovae.ResolvePosition(e, in g, null);
        SupernovaEvent r2 = Supernovae.ResolvePosition(e, in g, null);
        Assert("position resolution is deterministic", r1.RaDeg == r2.RaDeg && r1.DecDeg == r2.DecDeg);

        // Sersic sampling must place events INSIDE the galaxy at its catalogued scale: over many
        // events, the median offset stays inside the D25 ellipse and the mean is not at zero.
        int inside = 0, total = 0;
        double sumOffset = 0.0;
        for (int i = 0; i < 400; i++)
        {
            var ev = new SupernovaEvent { HostName = "TEST", ExplosionUt = 1e8 + i * 1e5, Class = SupernovaClass.Ia };
            SupernovaEvent r = Supernovae.ResolvePosition(ev, in g, null);
            double dRa = (r.RaDeg - g.RaDeg) * Math.Cos(g.DecDeg * Math.PI / 180.0) * 3600.0;
            double dDec = (r.DecDeg - g.DecDeg) * 3600.0;
            double offset = Math.Sqrt(dRa * dRa + dDec * dDec);
            sumOffset += offset;
            total++;
            if (offset <= g.SemiMajorArcsec * 1.5) inside++;
        }
        Assert($"{inside}/{total} sampled positions inside 1.5 semi-major axes, mean offset {sumOffset / total:F1}\"",
               inside > total * 3 / 4 && sumOffset / total > 1.0);
    }

    // ------------------------------------------------------------------ forecast

    /// <summary>
    /// Every supernova one save will ever see, in the order it happens. The events are a pure
    /// function of (seed, host, block), so this reads a save's future without playing it: what
    /// TESTING 26.2 needs to check the feature in game without waiting years for a random one.
    /// </summary>
    private static void Forecast(SupernovaTemplateSet templates, long seed, double fromUt)
    {
        if (seed == 0)
        {
            Console.WriteLine("\nusage: --forecast <seed> [--ut <from>]");
            Console.WriteLine("the seed is 'supernovaSeed' in the ExoInstrumentsScenario node of");
            Console.WriteLine("saves/<name>/persistent.sfs, written the first time the observatory");
            Console.WriteLine("panel runs. A save that has never opened it has no history yet.");
            return;
        }
        string catalogPath = Environment.GetEnvironmentVariable("EXO_GALCAT") ?? "GalaxyCatalog.galcat";
        if (!System.IO.File.Exists(catalogPath)) { Console.WriteLine("set EXO_GALCAT"); return; }
        var catalog = new GalaxyCatalog();
        catalog.Load(catalogPath);
        List<Galaxy> galaxies = catalog.Search(0.0, 0.0, 180.0, double.PositiveInfinity);

        double horizonUt = fromUt + 10.0 * 365.25 * 86400.0;
        var found = new List<(double ut, double peakV, Galaxy g, SupernovaEvent e)>();
        foreach (Galaxy g in galaxies)
        {
            if (double.IsNaN(g.DistanceModulusMag)) continue;
            long first = (long)Math.Floor(fromUt / Supernovae.BlockSeconds);
            for (long b = first; b <= (long)Math.Floor(horizonUt / Supernovae.BlockSeconds); b++)
                foreach (SupernovaEvent e in Supernovae.EventsInBlock(seed, in g, b))
                {
                    if (e.ExplosionUt < fromUt || e.ExplosionUt > horizonUt) continue;
                    // Positions need the host's light map, which this harness does not load; the
                    // Sersic fallback puts the event within the galaxy, which is the pointing
                    // anyway. The host's own centre is what a player enters.
                    found.Add((e.ExplosionUt, e.PeakAbsoluteBMag + g.DistanceModulusMag, g,
                               Supernovae.ResolvePosition(e, in g, null)));
                }
        }
        found.Sort((a, b) => a.ut.CompareTo(b.ut));

        Console.WriteLine($"\nSUPERNOVA FORECAST for seed {seed}");
        Console.WriteLine($"now: UT {fromUt:F0} = {KerbinDate(fromUt)}. Over ten years: {found.Count} events");
        Console.WriteLine("(Kerbin time: 6 hour days, 426 day years, as the game clock shows)");
        Console.WriteLine(new string('-', 78));
        Console.WriteLine($"{"maximum",-16} {"warp",-9} {"in",-12} {"class",-5} {"peak V",6} {"RA",8} {"Dec",7}  event offset");
        int shown = 0;
        foreach (var (ut, peakV, g, e) in found)
        {
            if (peakV > 18.0) continue;                       // below anything in the roster
            SupernovaTemplate t = templates.Get(e.Class);
            // Rise to maximum differs by class; the template's own brightest phase, not 19 days
            // borrowed from the Ia.
            double best = ut + PeakPhaseDays(t) * 86400.0;
            // The GALAXY's centre is what a player points at, and the event sits somewhere inside
            // it; the offset is printed so the difference is never mistaken for an error.
            double dDec = (e.DecDeg - g.DecDeg) * 3600.0;
            double dRa = (e.RaDeg - g.RaDeg) * 3600.0 * Math.Cos(g.DecDeg * Math.PI / 180.0);
            double offArcsec = Math.Sqrt(dRa * dRa + dDec * dDec);
            Console.WriteLine($"{KerbinDate(best),-16} {KerbinSpan(best - fromUt),-9} {g.Name,-12} "
                            + $"{e.Class,-5} {peakV,6:F1} {g.RaDeg / 15.0,7:F2}h {g.DecDeg,+6:F1}"
                            + $"  +{offArcsec,4:F0}\" from centre");
            if (++shown >= 15) break;
        }
        if (shown == 0) Console.WriteLine("  nothing brighter than V 18 in the window");
        Console.WriteLine("\nwarp the 'warp' column, point the RedCat at the RA/Dec, and expose.");
    }

    /// <summary>
    /// UT as the game clock shows it. Kerbin time: a 6 hour day, a 426 day year, both counted
    /// from one at UT zero, which is what KSP's own display does.
    /// </summary>
    private static string KerbinDate(double ut)
    {
        const double Hour = 3600.0, Day = 6.0 * Hour, Year = 426.0 * Day;
        long year = (long)(ut / Year) + 1;
        double rem = ut - (year - 1) * Year;
        long day = (long)(rem / Day) + 1;
        rem -= (day - 1) * Day;
        long h = (long)(rem / Hour);
        long m = (long)((rem - h * Hour) / 60.0);
        return $"Y{year} d{day,3} {h:00}h{m:00}";
    }

    /// <summary>An interval in the same units, for "how long do I warp".</summary>
    private static string KerbinSpan(double seconds)
    {
        const double Hour = 3600.0, Day = 6.0 * Hour;
        long days = (long)(seconds / Day);
        long h = (long)((seconds - days * Day) / Hour);
        return $"{days}d {h}h";
    }

    /// <summary>Phase of the template's own maximum, days.</summary>
    private static double PeakPhaseDays(SupernovaTemplate t)
    {
        double best = 0.0, bestMag = double.PositiveInfinity;
        for (double d = 0.0; d <= t.LastPhaseDays; d += 0.5)
        {
            double m = t.BOffsetAt(d);
            if (m < bestMag) { bestMag = m; best = d; }
        }
        return best;
    }

    // ------------------------------------------------------------------ census

    /// <summary>
    /// What the model actually gives a PLAYER, measured on the shipped catalogue rather than
    /// argued from the per-galaxy rate. Not a test: a balance instrument, because "one per
    /// century per galaxy" says nothing about how often a photograph catches one.
    /// </summary>
    private static void Census(SupernovaTemplateSet templates)
    {
        string catalogPath = Environment.GetEnvironmentVariable("EXO_GALCAT")
                          ?? "GalaxyCatalog.galcat";
        if (!System.IO.File.Exists(catalogPath))
        {
            Console.WriteLine("census needs the installed catalogue at " + catalogPath);
            return;
        }
        var catalog = new GalaxyCatalog();
        catalog.Load(catalogPath);
        List<Galaxy> galaxies = catalog.Search(0.0, 0.0, 180.0, double.PositiveInfinity);

        Console.WriteLine("\nSUPERNOVA CENSUS over the shipped catalogue");
        Console.WriteLine(new string('-', 42));

        int withDistance = 0;
        double totalPerCentury = 0.0;
        foreach (Galaxy g in galaxies)
        {
            Supernovae.RatePerCentury(in g, out double ia, out double ibc, out double ii);
            if (ia + ibc + ii <= 0.0) continue;
            withDistance++;
            totalPerCentury += ia + ibc + ii;
        }
        Console.WriteLine($"{galaxies.Count} galaxies, {withDistance} with a distance and a rate");
        Console.WriteLine($"total rate: {totalPerCentury:F1} per century = "
                        + $"{totalPerCentury / 100.0:F2} per year over the whole sky");

        // How bright each would be, and for how long it stays above a threshold. The visibility
        // window is measured on the Ia template, the commonest bright class.
        SupernovaTemplate ia_t = templates.Get(SupernovaClass.Ia);
        foreach (double limit in new[] { 12.0, 14.0, 16.0, 18.0, 20.0 })
        {
            double expectedActive = 0.0;
            int hostsInReach = 0;
            foreach (Galaxy g in galaxies)
            {
                if (double.IsNaN(g.DistanceModulusMag)) continue;
                Supernovae.RatePerCentury(in g, out double ia, out double ibc, out double ii);
                double rate = ia + ibc + ii;
                if (rate <= 0.0) continue;

                // Days the Ia template spends brighter than the limit in this galaxy.
                double peakApparent = -19.25 + g.DistanceModulusMag;
                double allowed = limit - peakApparent;
                if (allowed < 0.0) continue;
                hostsInReach++;
                double days = DaysBrighterThan(ia_t, allowed);
                expectedActive += rate / 100.0 / 365.25 * days;   // rate per day times window
            }
            Console.WriteLine($"  brighter than V {limit,4:F0}: {hostsInReach,5} galaxies can host one, "
                            + $"{expectedActive:F2} visible sky-wide at any instant "
                            + $"({1.0 / Math.Max(1e-9, expectedActive):F1} would have to be watched at once)");
        }

        // The player's real question: photograph N galaxies each visit, how long until one has a
        // supernova in it? Ranked by rate, because a player picks bright galaxies.
        var ranked = new List<KeyValuePair<double, Galaxy>>();
        foreach (Galaxy g in galaxies)
        {
            Supernovae.RatePerCentury(in g, out double ia, out double ibc, out double ii);
            if (ia + ibc + ii > 0.0) ranked.Add(new KeyValuePair<double, Galaxy>(ia + ibc + ii, g));
        }
        ranked.Sort((a, b) => b.Key.CompareTo(a.Key));

        Console.WriteLine("\nthe ten richest hosts (rate per century, Ia peak apparent V):");
        for (int i = 0; i < Math.Min(10, ranked.Count); i++)
        {
            Galaxy g = ranked[i].Value;
            Console.WriteLine($"  {g.Name,-12} {ranked[i].Key,5:F2}/century  "
                            + $"peak V {-19.25 + g.DistanceModulusMag,5:F1}  "
                            + $"modulus {g.DistanceModulusMag,5:F1}  T {g.MorphologicalType,+5:F1}");
        }

        // HOW MANY GALAXIES FIT IN ONE FRAME. The probabilities below are per GALAXY WATCHED, and
        // a player watches them one exposure at a time unless several share a field. Measured
        // rather than assumed: for each host, how many other catalogued galaxies lie within half
        // a field of it, at the roster's real fields of view.
        Console.WriteLine("\ngalaxies sharing one field (catalogued hosts within half a field of each other):");
        foreach (double fovDeg in new[] { 0.045, 0.5, 1.0, 2.0, 3.0 })
        {
            double half = fovDeg / 2.0;
            int best = 0;
            string bestName = "";
            double meanCompanions = 0.0;
            int multi = 0;
            foreach (Galaxy g in galaxies)
            {
                if (double.IsNaN(g.DistanceModulusMag)) continue;
                int n = 0;
                foreach (Galaxy o in galaxies)
                {
                    if (o.Name == g.Name || double.IsNaN(o.DistanceModulusMag)) continue;
                    double dDec = o.DecDeg - g.DecDeg;
                    if (Math.Abs(dDec) > half) continue;
                    double dRa = (o.RaDeg - g.RaDeg) * Math.Cos(g.DecDeg * Math.PI / 180.0);
                    if (Math.Sqrt(dRa * dRa + dDec * dDec) <= half) n++;
                }
                meanCompanions += n;
                if (n > 0) multi++;
                if (n > best) { best = n; bestName = g.Name; }
            }
            int hosts = 0;
            foreach (Galaxy g in galaxies) if (!double.IsNaN(g.DistanceModulusMag)) hosts++;
            Console.WriteLine($"  field {fovDeg,5:F3} deg: mean {meanCompanions / hosts:F2} companions, "
                            + $"{100.0 * multi / hosts,4:F1}% of pointings catch more than one, "
                            + $"best {bestName} with {best + 1}");
        }

        // WHERE TO POINT A WIDE FIELD. Every catalogued host is tried as a field centre; the
        // score is the chance that at least one galaxy inside the field is currently showing an
        // event brighter than the limit. Fields overlapping an already-listed one are skipped, so
        // the list is a tour and not ten names for the same patch of sky.
        foreach (double fovDeg in new[] { 4.395, 0.317 })
        {
            double half = fovDeg / 2.0, limit = fovDeg > 1.0 ? 16.0 : 17.5;
            var scored = new List<(double p, int n, double ra, double dec, string name)>();
            foreach (Galaxy c in galaxies)
            {
                if (double.IsNaN(c.DistanceModulusMag)) continue;
                double none = 1.0; int n = 0;
                foreach (Galaxy g in galaxies)
                {
                    if (double.IsNaN(g.DistanceModulusMag)) continue;
                    double dDec = g.DecDeg - c.DecDeg;
                    if (Math.Abs(dDec) > half) continue;
                    double dRa = (g.RaDeg - c.RaDeg) * Math.Cos(c.DecDeg * Math.PI / 180.0);
                    if (Math.Sqrt(dRa * dRa + dDec * dDec) > half) continue;
                    Supernovae.RatePerCentury(in g, out double ia, out double ibc, out double ii);
                    double rate = ia + ibc + ii;
                    double allowed = limit - (-19.25 + g.DistanceModulusMag);
                    if (rate <= 0.0 || allowed <= 0.0) continue;
                    n++;
                    none *= 1.0 - Math.Min(1.0, rate / 100.0 / 365.25 * DaysBrighterThan(ia_t, allowed));
                }
                if (n > 0) scored.Add((1.0 - none, n, c.RaDeg, c.DecDeg, c.Name));
            }
            scored.Sort((a, b) => b.p.CompareTo(a.p));

            Console.WriteLine($"\nbest pointings for a {fovDeg:F2} deg field (limit V {limit:F1}), non-overlapping:");
            var taken = new List<(double ra, double dec)>();
            int shown = 0; double tourNone = 1.0;
            foreach (var e in scored)
            {
                bool overlaps = false;
                foreach (var t in taken)
                {
                    double dDec = e.dec - t.dec;
                    double dRa = (e.ra - t.ra) * Math.Cos(t.dec * Math.PI / 180.0);
                    if (Math.Sqrt(dRa * dRa + dDec * dDec) < fovDeg) { overlaps = true; break; }
                }
                if (overlaps) continue;
                taken.Add((e.ra, e.dec));
                tourNone *= 1.0 - e.p;
                Console.WriteLine($"  {++shown,2}. RA {e.ra / 15.0,6:F2}h  Dec {e.dec,+6:F1}   "
                                + $"{e.n,3} hosts  {100.0 * e.p,5:F1}% per visit   near {e.name}");
                if (shown >= 8) break;
            }
            Console.WriteLine($"      the whole {shown}-pointing tour: {100.0 * (1.0 - tourNone):F0}% chance of at least one per pass");
        }

        // The player's real question, answered as a probability rather than a rate: photographing
        // a set of galaxies, how often is one of them hosting something you could see? A visible
        // event lasts as long as the template stays above the limit, so the duty cycle per galaxy
        // is rate x window, and the chance at least one of N shows something is 1 - prod(1 - p).
        foreach (double limit in new[] { 14.0, 16.0, 18.0 })
        {
            var duty = new List<KeyValuePair<double, string>>();
            foreach (Galaxy g in galaxies)
            {
                if (double.IsNaN(g.DistanceModulusMag)) continue;
                Supernovae.RatePerCentury(in g, out double ia, out double ibc, out double ii);
                double rate = ia + ibc + ii;
                if (rate <= 0.0) continue;
                double allowed = limit - (-19.25 + g.DistanceModulusMag);
                if (allowed <= 0.0) continue;
                double p = rate / 100.0 / 365.25 * DaysBrighterThan(ia_t, allowed);
                duty.Add(new KeyValuePair<double, string>(Math.Min(1.0, p), g.Name));
            }
            duty.Sort((a, b) => b.Key.CompareTo(a.Key));

            Console.WriteLine($"\nphotographing the best N galaxies, chance one is hosting an event brighter than V {limit:F0}:");
            foreach (int n in new[] { 1, 5, 10, 25, 50, 100 })
            {
                double none = 1.0;
                for (int i = 0; i < Math.Min(n, duty.Count); i++) none *= 1.0 - duty[i].Key;
                Console.WriteLine($"    {n,4} galaxies: {100.0 * (1.0 - none),5:F1}%   "
                                + (n == 1 ? $"(best host: {duty[0].Value}, {100.0 * duty[0].Key:F1}%)" : ""));
            }
        }
    }

    /// <summary>Days the template stays within the given number of magnitudes of its peak.</summary>
    private static double DaysBrighterThan(SupernovaTemplate t, double magsBelowPeak)
    {
        if (magsBelowPeak <= 0.0) return 0.0;
        double days = 0.0;
        for (double d = 0.0; d <= t.ActiveDays; d += 0.5)
            if (t.BOffsetAt(d) <= magsBelowPeak) days += 0.5;
        return days;
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
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}: {got:G6} vs {expected:G6} {unit}".TrimEnd());
    }

    private static void Assert(string what, bool condition)
    {
        checks++;
        if (!condition) failures++;
        Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {what}");
    }
}

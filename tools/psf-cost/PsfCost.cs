using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using ExoInstruments.Core;

/// <summary>
/// What the PSF kernel costs a capture, and what a cheaper kernel is worth.
///
/// WHY IT EXISTS. Baptiste's report, 2026-08-07: a galaxy photograph through the orbital Hubble
/// at 4x4 binning took seven minutes on a Mac and about one on a desktop PC. The shipped
/// per-stage log answered where the time was outright:
///
///   Reduced a 1024x1025 frame (binning 4) in 424504 ms on 9 worker(s): render readout 44 ms,
///   galaxies 625 ms, smear 0 ms, stars + emission 10764 ms, PSF kernel 410724 ms,
///   PSF convolution 481 ms, coronagraph + speckles 624 ms, detector 1239 ms
///
/// 96.7 per cent of the exposure was in building the kernel. On a pupil with a spider the
/// diffraction term is sampled in two dimensions over the whole 257x257 support, and each of
/// those 66049 pixels is a midpoint average over up to 12x12 nodes, so one sub-band kernel is
/// 9.5 million evaluations of PupilDiffraction.Intensity; a capture builds twelve of them for
/// the passband and, before this harness existed, twelve more that it threw away inside
/// GaussianFwhmForDelivered.
///
/// WHAT IT MEASURES. Three things, because the fix trades on all three:
///
///   --symmetry   the identity the sampler halves its work with: a real pupil's far field obeys
///                I(-theta) = I(theta) exactly. Checked numerically, not assumed.
///   --solve      that bounding the kernel the FWHM solvers build changes none of their answers,
///                over every instrument, binning and sub-band wavelength on the roster.
///   (default)    the cost of the kernel stage per instrument and binning, and the finished
///                diffraction kernel against a CONVERGED reference sampled at four times the
///                shipped node count and no taper - the number that says what the sampling
///                actually gave up.
///
/// The reference is built here from PupilDiffraction directly rather than through OpticalPsf, so
/// it does not inherit the sampling decisions it exists to judge.
///
///   dotnet run -c Release -p:Core=../../ExoInstruments/Core -- [--symmetry|--solve] [--bin N]
///
/// --symmetry and --solve exit non-zero on failure.
/// </summary>
internal static class PsfCost
{
    private const double ArcsecToRad = Math.PI / (180.0 * 3600.0);

    /// <summary>
    /// One pupil from the roster, transcribed from Core/VisualTelescopeCatalog.cs. Literals
    /// rather than the catalogue itself for the same reason tools/poppy-crossvalidation uses
    /// them: the catalogue pulls in the whole Visualization layer, and what is under test here
    /// is the pupil and the sampler, not the roster's plumbing.
    /// </summary>
    private sealed class Pupil
    {
        public string Name;
        public double ApertureM, Obstruction, NativeScaleArcsec, VaneWidthM;
        public int VaneCount;
        public PupilPad[] Pads;
        public double LoNm, HiNm;          // passband the sub-bands are split across
        public double DeliveredFwhmArcsec; // 0 on the ground: nothing to solve for
    }

    private static readonly PupilPad[] HubblePads =
    {
        new PupilPad(0.8921,  0.0000, 0.065),
        new PupilPad(-0.4615, 0.7555, 0.065),
        new PupilPad(-0.4564, -0.7606, 0.065),
    };

    private static readonly Pupil[] Roster =
    {
        // scale = 206265 * pixel / (focal * barlow), all three from the catalogue entry.
        new Pupil { Name = "RC20",        ApertureM = 0.51, Obstruction = 0.39,
                    NativeScaleArcsec = 206265.0 * 4.63e-6 / (0.51 * 6.8 * 4.0),
                    VaneCount = 4, VaneWidthM = 0.0015, LoNm = 400, HiNm = 700 },
        new Pupil { Name = "CDK1000",     ApertureM = 1.000, Obstruction = 0.47,
                    NativeScaleArcsec = 206265.0 * 4.63e-6 / (6.0 * 4.0),
                    VaneCount = 4, VaneWidthM = 0.0025, LoNm = 400, HiNm = 700 },
        new Pupil { Name = "FORS2",       ApertureM = 8.2, Obstruction = 1.116 / 8.2,
                    NativeScaleArcsec = 206265.0 * 15e-6 / (24.556 * 2.0),
                    VaneCount = 4, VaneWidthM = 0.041, LoNm = 400, HiNm = 700 },
        new Pupil { Name = "SPHERE",      ApertureM = 8.2, Obstruction = 1.116 / 8.2,
                    NativeScaleArcsec = 206265.0 * 15e-6 / 1718.7,
                    VaneCount = 4, VaneWidthM = 0.041, LoNm = 400, HiNm = 700 },
        new Pupil { Name = "WFC3/UVIS",   ApertureM = 2.4, Obstruction = 0.330,
                    NativeScaleArcsec = 0.0396,
                    VaneCount = 4, VaneWidthM = 0.022 * 1.2, Pads = HubblePads,
                    LoNm = 400, HiNm = 700, DeliveredFwhmArcsec = 0.067 },
        new Pupil { Name = "WFC3/IR",     ApertureM = 2.4, Obstruction = 0.330,
                    NativeScaleArcsec = Math.Sqrt(0.135 * 0.121),
                    VaneCount = 4, VaneWidthM = 0.022 * 1.2, Pads = HubblePads,
                    LoNm = 900, HiNm = 1600, DeliveredFwhmArcsec = 0.15 },
    };

    private const int SubBands = 12;

    /// <summary>--pupil NAME restricts every mode to one entry, for iterating on one instrument.</summary>
    private static string only;

    private static IEnumerable<Pupil> Selected()
    {
        foreach (Pupil p in Roster)
            if (only == null || p.Name.IndexOf(only, StringComparison.OrdinalIgnoreCase) >= 0)
                yield return p;
    }

    private static int Main(string[] args)
    {
        Console.WriteLine($"{Environment.ProcessorCount} cores reported, {ParallelWork.MaxWorkers} workers used");
        Console.WriteLine();

        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--pupil") only = args[i + 1];

        if (Array.IndexOf(args, "--symmetry") >= 0) return CheckSymmetry();
        if (Array.IndexOf(args, "--solve") >= 0) return CheckSolve();
        if (Array.IndexOf(args, "--convolve") >= 0) return CheckKernelConvolution();

        var bins = new List<int>();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--bin") bins.Add(int.Parse(args[i + 1]));
        if (bins.Count == 0) { bins.Add(1); bins.Add(2); bins.Add(4); }

        Report(bins);
        return 0;
    }

    // ------------------------------------------------------------------ the symmetry identity

    /// <summary>
    /// A telescope pupil is a real transmission function, so its far-field amplitude satisfies
    /// A(-u) = conj(A(u)) and the intensity is an even function of angle. OpticalPsf's sampler
    /// computes half its grid and mirrors the rest on the strength of that, so it is checked
    /// here on the real pupils, including the one whose three mirror pads are the reason the
    /// amplitude has an imaginary part at all.
    /// </summary>
    private static int CheckSymmetry()
    {
        Console.WriteLine("Every reflection the sampler folds its grid on, checked against the pattern.");
        Console.WriteLine("'claims' is what PupilDiffraction works out from its own geometry; the columns");
        Console.WriteLine("are the worst relative disagreement found over 20000 random offsets out to the");
        Console.WriteLine("corner of the widest kernel the pupil can be sampled onto.");
        Console.WriteLine();
        Console.WriteLine("pupil        claims      I(-t)=I(t)   I(-x,y)=I(x,y)   I(y,x)=I(x,y)");
        bool ok = true;
        foreach (Pupil p in Selected())
        {
            var pupil = new PupilDiffraction(p.ApertureM, p.Obstruction, 550e-9,
                                             p.VaneCount, p.VaneWidthM, 0.0, p.Pads);
            double central = 0.0, axis = 0.0, diagonal = 0.0;
            var rng = new Random(12345);
            double reach = 128.0 * Math.Sqrt(2.0) * p.NativeScaleArcsec * 4.0 * ArcsecToRad;
            for (int i = 0; i < 20000; i++)
            {
                double tx = (rng.NextDouble() * 2.0 - 1.0) * reach;
                double ty = (rng.NextDouble() * 2.0 - 1.0) * reach;
                double a = pupil.Intensity(tx, ty);
                if (a <= 0.0) continue;
                central = Math.Max(central, Math.Abs(pupil.Intensity(-tx, -ty) - a) / a);
                axis = Math.Max(axis, Math.Abs(pupil.Intensity(-tx, ty) - a) / a);
                diagonal = Math.Max(diagonal, Math.Abs(pupil.Intensity(ty, tx) - a) / a);
            }

            string claims = pupil.DiagonalMirrorSymmetric ? "octant"
                          : pupil.AxisMirrorSymmetric ? "quadrant" : "half";
            Console.WriteLine($"{p.Name,-12} {claims,-10}  {central,10:E2}   {axis,14:E2}   {diagonal,13:E2}");

            // The central symmetry is exact arithmetic - the same expression with both arguments
            // negated - so it is held to zero. The two mirror symmetries go through the vane
            // direction cosines, where Math.Cos(pi/2) is 6.1e-17 rather than 0, so a vane
            // nominally along y carries an x-component of that size and the reflected intensity
            // differs in the tenth decimal. The fold does not inherit that: it computes one
            // octant and copies it, so the finished kernel is exactly symmetric, which is the
            // pupil's real property. What is checked here is that the symmetry is THERE, to well
            // below anything the kernel carries; a pupil that lacks it disagrees by orders of
            // magnitude, not by 1e-10, which is the failure this exists to catch.
            if (central > 0.0) ok = false;
            if (pupil.AxisMirrorSymmetric && axis > 1e-9) ok = false;
            if (pupil.DiagonalMirrorSymmetric && diagonal > 1e-9) ok = false;
        }
        Console.WriteLine();
        Console.WriteLine(ok ? "PASS: every reflection the sampler folds on holds to machine precision."
                             : "FAIL: a pupil claims a symmetry its own pattern does not have.");
        return ok ? 0 : 1;
    }

    // ------------------------------------------------------------------ kernel convolution

    /// <summary>
    /// `OpticalPsf` composes a PSF by convolving its terms, and above a work budget that
    /// convolution now goes through `FourierConvolution.ConvolveKernels` instead of the direct
    /// sum. Two routes to the same quantity, so the transform is checked against the sum it
    /// replaces, on the shapes the kernel builder actually produces: a two-dimensional
    /// diffraction grid against a radial profile.
    ///
    /// The direct sum here is written out rather than called, because the shipped one is private
    /// and, above the budget, is no longer the path taken.
    /// </summary>
    private static int CheckKernelConvolution()
    {
        Console.WriteLine("the transform against the direct sum, on the shapes a PSF is made of");
        Console.WriteLine(" ra   rb  rOut       direct ms   transform ms   max|d| / peak   sum ratio");
        bool ok = true;
        var rng = new Random(4242);

        foreach (var shape in new[] { (16, 12, 28), (64, 48, 112), (128, 91, 128), (128, 128, 128) })
        {
            int ra = shape.Item1, rb = shape.Item2, rOut = shape.Item3;
            double[] a = RandomProfile(rng, ra, 2.5);   // a peaked, spiky thing, like diffraction
            double[] b = RandomProfile(rng, rb, 9.0);   // a broad one, like the atmosphere

            var sw = Stopwatch.StartNew();
            double[] direct = Direct(a, ra, b, rb, rOut);
            sw.Stop();
            double directMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            double[] fft = FourierConvolution.ConvolveKernels(a, ra, b, rb, rOut);
            sw.Stop();

            if (fft == null) { Console.WriteLine($"{ra,3} {rb,4} {rOut,5}   transform declined (too large)"); continue; }

            double peak = 0.0, worst = 0.0, s1 = 0.0, s2 = 0.0;
            for (int i = 0; i < direct.Length; i++)
            {
                peak = Math.Max(peak, Math.Abs(direct[i]));
                worst = Math.Max(worst, Math.Abs(direct[i] - fft[i]));
                s1 += fft[i];
                s2 += direct[i];
            }
            Console.WriteLine($"{ra,3} {rb,4} {rOut,5}   {directMs,13:F0}   {sw.Elapsed.TotalMilliseconds,12:F0}   "
                            + $"{worst / peak,13:E2}   {s1 / s2,9:F12}");
            if (worst / peak > 1e-12) ok = false;
        }
        Console.WriteLine();
        Console.WriteLine(ok ? "PASS: the transform reproduces the direct sum to double precision."
                             : "FAIL: the two routes disagree by more than rounding.");
        return ok ? 0 : 1;
    }

    private static double[] RandomProfile(Random rng, int r, double scalePx)
    {
        int size = 2 * r + 1;
        var k = new double[size * size];
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                double d = Math.Sqrt((double)dx * dx + (double)dy * dy);
                k[(dy + r) * size + dx + r] = Math.Exp(-d / scalePx) * (0.5 + rng.NextDouble());
            }
        return k;
    }

    private static double[] Direct(double[] a, int ra, double[] b, int rb, int rOut)
    {
        int sizeA = 2 * ra + 1, sizeB = 2 * rb + 1, sizeOut = 2 * rOut + 1;
        var outK = new double[sizeOut * sizeOut];
        for (int ay = -ra; ay <= ra; ay++)
            for (int ax = -ra; ax <= ra; ax++)
            {
                double av = a[(ay + ra) * sizeA + ax + ra];
                if (av <= 0.0) continue;
                for (int by = -rb; by <= rb; by++)
                {
                    int oy = ay + by;
                    if (oy < -rOut || oy > rOut) continue;
                    for (int bx = -rb; bx <= rb; bx++)
                    {
                        int ox = ax + bx;
                        if (ox < -rOut || ox > rOut) continue;
                        outK[(oy + rOut) * sizeOut + ox + rOut] += av * b[(by + rb) * sizeB + bx + rb];
                    }
                }
            }
        return outK;
    }

    // ------------------------------------------------------------------ the bounded solve

    /// <summary>
    /// GaussianFwhmForDelivered and AtmosphericFwhmForDelivered invert a published delivered
    /// width into the broadening term that reproduces it, by bisecting on kernels they build and
    /// then read one row of. They now build those kernels at a bounded support. This replays the
    /// same bisection against FULL-support kernels, through the public builder and the public
    /// measurement, and requires the answers to agree.
    ///
    /// AGREE TO WITHIN THE BISECTION'S OWN LAST STEP, not bit for bit, and the reason is worth
    /// stating because it is the only thing the bound changes. The measurement reads a float32
    /// kernel that Normalise has divided by its own total, and a smaller support has a different
    /// total, so the two kernels' rows differ in the last bits of float32 - about 1e-7 relative.
    /// Where the bisection's midpoint sits within that of the target, the comparison can fall the
    /// other way, and the answer lands one step of the bracket away: 24 halvings of a bracket of
    /// the delivered width itself, so 5e-8 arcsec against a width of order an arcsecond. Neither
    /// answer is the more correct one; the tolerance below is that step with an order of margin.
    /// </summary>
    private static int CheckSolve()
    {
        Console.WriteLine("bounded solve against a full-support bisection");
        Console.WriteLine("pupil        bin   lambda nm   delivered    shipped        full   difference");
        bool ok = true;
        foreach (Pupil p in Selected())
        {
            double delivered = p.DeliveredFwhmArcsec > 0.0 ? p.DeliveredFwhmArcsec : 0.8;
            double worst = 0.0;
            foreach (int bin in new[] { 1, 2, 4 })
            {
                double scale = p.NativeScaleArcsec * bin;
                for (int b = 0; b < SubBands; b++)
                {
                    double lambda = Lambda(p, b);
                    double shipped = OpticalPsf.GaussianFwhmForDelivered(
                        delivered, scale, p.ApertureM, p.Obstruction, lambda, p.VaneCount, p.VaneWidthM);
                    double full = FullSupportGaussianSolve(
                        delivered, scale, p.ApertureM, p.Obstruction, lambda, p.VaneCount, p.VaneWidthM);
                    double diff = Math.Abs(shipped - full);
                    worst = Math.Max(worst, diff);
                    // One bisection step on a bracket of the delivered width, with an order of
                    // margin. See the summary for what the residual is and why it is not a bias.
                    if (diff > 10.0 * delivered / (1 << 24))
                    {
                        ok = false;
                        Console.WriteLine($"{p.Name,-12} {bin,3}   {lambda * 1e9,9:F0}   {delivered,9:F4}   "
                                        + $"{shipped,9:F6}   {full,9:F6}   {diff,10:E2}");
                    }
                }
            }
            Console.WriteLine($"{p.Name,-12}  {3 * SubBands} cases, worst difference {worst:E2} arcsec "
                            + $"({worst / Math.Max(1e-12, delivered):E1} of the width solved for)");
        }
        Console.WriteLine();
        Console.WriteLine(ok ? "PASS: bounding the solve moves no answer beyond the bisection's own step."
                             : "FAIL: bounding the solve changed an answer (listed above).");
        return ok ? 0 : 1;
    }

    /// <summary>The solver as it was before the bound, written out against the public API.</summary>
    private static double FullSupportGaussianSolve(double deliveredFwhm, double scale, double aperture,
                                                   double obstruction, double lambda,
                                                   int vaneCount, double vaneWidth)
    {
        Func<double, double> measured = g =>
        {
            float[] k = OpticalPsf.BuildKernel(scale, aperture, obstruction, lambda, 0.0, 0.0,
                                               vaneCount, vaneWidth, g, out int r);
            return OpticalPsf.MeasureKernelFwhmArcsec(k, r, scale);
        };

        if (deliveredFwhm <= 0.0) return 0.0;
        if (measured(0.0) >= deliveredFwhm) return 0.0;

        double lo = 0.0, hi = deliveredFwhm;
        for (int i = 0; i < 24; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (measured(mid) < deliveredFwhm) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    // ------------------------------------------------------------------ cost and accuracy

    private static double Lambda(Pupil p, int band)
        => (p.LoNm + (p.HiNm - p.LoNm) * (band + 0.5) / SubBands) * 1e-9;

    private static void Report(List<int> bins)
    {
        Console.WriteLine("Cost of the kernel stage as a capture runs it, and what the sampling gave up.");
        Console.WriteLine("The reference is sampled at 4x the shipped node count with no taper, built");
        Console.WriteLine("from PupilDiffraction directly so it does not inherit what it is judging.");
        Console.WriteLine();
        Console.WriteLine("'untapered' is the same sampler's node count everywhere, which is what the");
        Console.WriteLine("shipped kernel was before the taper: the two columns separate what the taper");
        Console.WriteLine("costs from what the node count was already costing.");
        Console.WriteLine();
        Console.WriteLine("'kernel ms' is the shipped builder; 'unfolded' is the same twelve sub-bands sampled");
        Console.WriteLine("with no fold and no taper, i.e. what the builder cost before either.");
        Console.WriteLine();
        Console.WriteLine("                                                  |------- shipped -------|  |----- untapered -----|");
        Console.WriteLine("pupil        bin  nodes  solve ms  kernel ms  unfolded   max|d|/peak  arm    diag   max|d|/peak  arm    diag");
        Console.WriteLine("---------------------------------------------------------------------------------------------------------");

        foreach (Pupil p in Selected())
        {
            foreach (int bin in bins)
            {
                double scale = p.NativeScaleArcsec * bin;
                var probe = new PupilDiffraction(p.ApertureM, p.Obstruction, Lambda(p, SubBands / 2),
                                                 p.VaneCount, p.VaneWidthM, 0.0, p.Pads);
                int nodes = probe.NodeCount(scale * ArcsecToRad);

                double delivered = p.DeliveredFwhmArcsec;
                var sw = Stopwatch.StartNew();
                var gauss = new double[SubBands];
                for (int b = 0; b < SubBands; b++)
                    gauss[b] = delivered > 0.0
                        ? OpticalPsf.GaussianFwhmForDelivered(delivered, scale, p.ApertureM,
                                                              p.Obstruction, Lambda(p, b),
                                                              p.VaneCount, p.VaneWidthM)
                        : 0.0;
                sw.Stop();
                double solveMs = sw.Elapsed.TotalMilliseconds;

                var bands = new List<ChromaticSubBand>(SubBands);
                for (int b = 0; b < SubBands; b++)
                    bands.Add(new ChromaticSubBand
                    {
                        WavelengthMeters = Lambda(p, b),
                        Weight = 1.0,
                        GaussianFwhmArcsec = gauss[b],
                    });

                sw.Restart();
                OpticalPsf.BuildChromaticKernel(
                    scale, p.ApertureM, p.Obstruction, 0.0, Lambda(p, SubBands / 2), 0.0,
                    p.VaneCount, p.VaneWidthM, p.Pads, bands, out int radius);
                sw.Stop();
                double kernelMs = sw.Elapsed.TotalMilliseconds;

                // Compared WITHOUT the Gaussian term. It is a radial convolution this change
                // never touched, and the reference below is the diffraction sampling alone, so
                // leaving it in either side would compare two different quantities. The timing
                // above is the real one, Gaussian included.
                for (int b = 0; b < SubBands; b++)
                {
                    ChromaticSubBand band = bands[b];
                    band.GaussianFwhmArcsec = 0.0;
                    bands[b] = band;
                }
                float[] shipped = OpticalPsf.BuildChromaticKernel(
                    scale, p.ApertureM, p.Obstruction, 0.0, Lambda(p, SubBands / 2), 0.0,
                    p.VaneCount, p.VaneWidthM, p.Pads, bands, out radius);

                double[] reference = ConvergedKernel(p, scale, radius, 4);
                sw.Restart();
                double[] untapered = ConvergedKernel(p, scale, radius, 1);
                sw.Stop();
                double beforeMs = sw.Elapsed.TotalMilliseconds;
                Compare(shipped, reference, radius, out double sumRatio, out double maxRel,
                        out double armRel, out double diagRel);
                Compare(untapered, reference, radius, out _, out double maxRel0,
                        out double armRel0, out double diagRel0);

                Console.WriteLine($"{p.Name,-12} {bin,3}  {nodes,5}  {solveMs,8:F0}  {kernelMs,9:F0} {beforeMs,9:F0}   "
                                + $"{maxRel,11:E2} {armRel,6:P1} {diagRel,6:P1}   "
                                + $"{maxRel0,11:E2} {armRel0,6:P1} {diagRel0,6:P1}");
            }
        }
        Console.WriteLine();
        Console.WriteLine("sum          finished kernel's total against the reference's (both normalised to 1)");
        Console.WriteLine("max|d|/peak  largest per-pixel difference, as a fraction of the kernel's peak");
        Console.WriteLine("arm          worst relative difference along +x, where the diffraction spike runs");
        Console.WriteLine("diagonal     the same at 45 degrees, between the spikes");
    }

    /// <summary>
    /// The chromatic diffraction kernel sampled the expensive way: every pixel at the node count
    /// asked for, no taper, no mirror, each band normalised to unit sum and then weighted, which
    /// is the assembly BuildChromaticKernel performs. Gaussian and atmospheric terms are left out
    /// of both sides: they are radial convolutions this change never touched, and including them
    /// would only dilute the comparison.
    /// </summary>
    /// <param name="nodeMultiple">Multiple of the sampler's OWN node count to use. 1 reproduces what the shipped kernel was before the taper; 4 is the converged reference everything is judged against.</param>
    private static double[] ConvergedKernel(Pupil p, double scaleArcsec, int radius, int nodeMultiple)
    {
        int size = 2 * radius + 1;
        var perBand = new double[SubBands][];

        Parallel.For(0, SubBands, ParallelWork.Options, b =>
        {
            var pupil = new PupilDiffraction(p.ApertureM, p.Obstruction, Lambda(p, b),
                                             p.VaneCount, p.VaneWidthM, 0.0, p.Pads);
            int nodes = nodeMultiple * pupil.NodeCount(scaleArcsec * ArcsecToRad);
            var k = new double[size * size];
            double sum = 0.0;
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    double v = pupil.PixelAveragedIntensityArcsec(
                        dx * scaleArcsec, dy * scaleArcsec, scaleArcsec, nodes);
                    k[(dy + radius) * size + dx + radius] = v;
                    sum += v;
                }
            if (sum > 0.0) for (int i = 0; i < k.Length; i++) k[i] /= sum;
            perBand[b] = k;
        });

        var acc = new double[size * size];
        for (int b = 0; b < SubBands; b++)
            for (int i = 0; i < acc.Length; i++) acc[i] += perBand[b][i] / SubBands;
        return acc;
    }

    private static void Compare(float[] shipped, double[] reference, int radius,
                                out double sumRatio, out double maxRel,
                                out double armRel, out double diagRel)
    {
        var asDouble = new double[shipped.Length];
        for (int i = 0; i < shipped.Length; i++) asDouble[i] = shipped[i];
        Compare(asDouble, reference, radius, out sumRatio, out maxRel, out armRel, out diagRel);
    }

    private static void Compare(double[] shipped, double[] reference, int radius,
                                out double sumRatio, out double maxRel,
                                out double armRel, out double diagRel)
    {
        int size = 2 * radius + 1;
        double peak = reference[radius * size + radius];
        double s1 = 0.0, s2 = 0.0, maxAbs = 0.0;
        for (int i = 0; i < reference.Length; i++)
        {
            s1 += shipped[i];
            s2 += reference[i];
            maxAbs = Math.Max(maxAbs, Math.Abs(shipped[i] - reference[i]));
        }
        sumRatio = s2 > 0.0 ? s1 / s2 : 0.0;
        maxRel = peak > 0.0 ? maxAbs / peak : 0.0;

        armRel = diagRel = 0.0;
        for (int r = 2; r < radius; r++)
        {
            int arm = radius * size + radius + r;
            if (reference[arm] > 1e-11 * peak)
                armRel = Math.Max(armRel, Math.Abs(shipped[arm] - reference[arm]) / reference[arm]);

            int d = (int)Math.Round(r * 0.7071);
            int diag = (radius + d) * size + radius + d;
            if (reference[diag] > 1e-11 * peak)
                diagRel = Math.Max(diagRel, Math.Abs(shipped[diag] - reference[diag]) / reference[diag]);
        }
    }
}

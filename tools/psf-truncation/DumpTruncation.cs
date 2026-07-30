using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;

/// <summary>
/// Measures what each instrument's PSF kernel actually throws away at its truncation radius.
///
/// A truncated-then-renormalised kernel conserves flux by construction, so no photometry test can
/// see the truncation. What it cannot conserve is the SURFACE BRIGHTNESS at the boundary: the
/// profile drops from its last sampled value to zero in one pixel, and around a bright enough
/// source that step is a visible edge with the shape of the kernel's support, a square, because
/// the kernel is stored as a square array and sampled all the way into its corners.
///
/// The numbers here are the ones that decide whether a support is big enough: the enclosed energy
/// inside it, and the profile value at the edge as a fraction of the peak.
///
/// The Kolmogorov profile itself is not re-derived here; it is the shipped
/// OpticalPsf.AtmosphericIntensity, already cross-validated against GalSim in
/// tools/galsim-crossvalidation. What this adds is the radial integral of it.
/// </summary>
static class DumpTruncation
{
    const double ArcsecToRad = Math.PI / (180.0 * 3600.0);

    /// <summary>
    /// The enclosed-energy integral is taken in the profile's own reduced variable
    /// rho = 2*pi*r0*theta/lambda rather than in arcsec, for two reasons.
    ///
    /// First it makes the normalisation exact instead of numerical. OpticalPsf.AtmosphericIntensity
    /// evaluates PSF(rho) = Int_0^inf T(u) J0(rho u) u du, which is the order-zero Hankel transform
    /// of Fried's OTF; that transform is self-reciprocal, so Int_0^inf PSF(rho) rho drho = T(0) = 1
    /// identically. The total energy therefore does not have to be integrated at all, which matters
    /// because it is exactly the far tail that no quadrature of this integrand gets right.
    ///
    /// Second it bounds where the profile may be trusted. The quadrature's step count is capped at
    /// MaxQuadratureSteps, so past some rho it stops resolving J0's oscillation and returns noise
    /// instead of a theta^(-11/3) wing. RhoMax stays well inside that.
    /// </summary>
    const int GridPoints = 6000;
    const double RhoMin = 1e-4;
    const double RhoMax = 200.0;

    static void Main()
    {
        Console.WriteLine("=== AO seeing halo: why a bounded kernel cannot carry it ===\n");
        foreach (var spec in VisualTelescopeCatalog.All)
            if (spec.AdaptiveOpticsHaloSeeingFwhmArcsec > 0.0) HaloReport(spec);

        Console.WriteLine("=== Core kernel: the step left at its own circular boundary ===\n");
        foreach (var spec in VisualTelescopeCatalog.All) CoreReport(spec);

        Console.WriteLine("=== The frame-wide kernel, against the profile it must reproduce ===\n");
        OtfReport();

        Console.WriteLine("=== Overlap-add tiling, against a direct convolution ===\n");
        TilingReport();

        DumpHaloProfile();
    }

    /// <summary>
    /// Puts a single electron in the middle of an empty frame, runs it through
    /// FourierConvolution.ConvolveWithRadialOtf with Fried's OTF, and compares the frame that
    /// comes out with OpticalPsf.AtmosphericIntensity evaluated directly.
    ///
    /// This is the check that the frequency-domain path is the same physics as the real-space one
    /// and not merely a plausible blur: the two share no code beyond the OTF expression itself,
    /// one being a Hankel transform of it evaluated by quadrature and the other a discrete Fourier
    /// transform of it evaluated by FFT. The same run reports what the KERNEL path leaves at its
    /// own boundary, which is the defect the OTF path exists to remove.
    /// </summary>
    static void OtfReport()
    {
        const int w = 1024, h = 1024;
        double scale = 0.0036, fwhm = 0.72, lambda = 700e-9;
        double r0 = OpticalPsf.FriedParameterMeters(fwhm, lambda);
        double radPerPixel = scale * ArcsecToRad;

        var plane = new float[w * h];
        plane[(h / 2) * w + (w / 2)] = 1f;

        double maxLag = Math.Sqrt((double)w * w + (double)h * h);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var table = new OpticalPsf.AtmosphericProfileTable(maxLag, scale, r0, lambda);
        var spectrum = FourierConvolution.RadialKernelSpectrum.Build(w, h, table.AtPixelRadius, 2048L * 2048L);
        long buildMs = sw.ElapsedMilliseconds;
        if (spectrum == null) { Console.WriteLine("  spectrum did not fit the budget"); return; }
        spectrum.Apply(plane, w, h);
        sw.Stop();
        Console.WriteLine($"  frame {w}x{h} at {scale}\"/px, halo FWHM {fwhm}\": grid {spectrum.Nx}x{spectrum.Ny},"
                        + $" prepared in {buildMs} ms, applied in {sw.ElapsedMilliseconds - buildMs} ms");

        // The analytic normalisation: nothing was summed to get it, so the sum is a check on it.
        // What is missing is the flux at lags larger than the sensor, which never reached a pixel.
        Console.WriteLine($"  kernel holds {spectrum.EnclosedFraction:F6} of the source's flux;"
                        + $" {(1.0 - spectrum.EnclosedFraction) * 100.0:F3}% falls at offsets no two sensor pixels span");
        double onFrame = 0.0;
        for (int i = 0; i < plane.Length; i++) onFrame += plane[i];
        Console.WriteLine($"  of that, {onFrame:F6} landed inside the {w}x{h} frame itself");

        // Radial agreement with the profile, in absolute per-pixel fractions, no peak scaling, so
        // the normalisation is under test as well as the shape.
        double worst = 0.0, worstAt = 0.0;
        Console.WriteLine("      r(px)        frame           profile        rel.diff");
        foreach (int r in new[] { 0, 1, 2, 5, 10, 25, 50, 100, 200, 256, 300, 400, 500 })
        {
            // Average the four axis points, so a half-pixel centring error would show up.
            double f = r == 0 ? plane[(h / 2) * w + (w / 2)]
                : 0.25 * (plane[(h / 2) * w + (w / 2 + r)] + plane[(h / 2) * w + (w / 2 - r)]
                        + plane[(h / 2 + r) * w + (w / 2)] + plane[(h / 2 - r) * w + (w / 2)]);
            double model = OpticalPsf.AtmosphericIntensity(r * scale * ArcsecToRad, r0, lambda)
                         * OpticalPsf.AtmosphericPerPixelScale(r0, lambda, scale);
            double rel = model > 0.0 ? Math.Abs(f - model) / model : 0.0;
            if (rel > worst) { worst = rel; worstAt = r; }
            Console.WriteLine($"     {r,5}   {f,14:E4}   {model,14:E4}   {rel,10:E2}");
        }
        Console.WriteLine($"  worst relative difference: {worst:E2} at {worstAt:F0} px\n");

        // What the kernel path leaves at the same radii, for contrast.
        float[] k = OpticalPsf.BuildSeeingHaloKernel(scale, fwhm, lambda, 256, out int kr);
        int ks = 2 * kr + 1;
        Console.WriteLine($"  kernel fallback: radius {kr} px, support ends at {kr * scale:F3}\""
                        + $", value there {k[kr * ks + ks - 1] / k[kr * ks + kr]:E3} of peak"
                        + $" (the step that draws the edge)\n");
    }

    /// <summary>Plate scale of the shipped configuration, including the binning the instrument ships at.</summary>
    static double PlateScale(VisualTelescopeSpec spec, int binning)
        => spec.NativePixelSizeMeters * binning
         / (spec.FocalLengthMeters * spec.BarlowFactor) / ArcsecToRad;

    static int Binning(VisualTelescopeSpec spec) => spec.CameraName.Contains("ZIMPOL") ? 2 : 1;

    static void HaloReport(VisualTelescopeSpec spec)
    {
        int binning = Binning(spec);
        double scale = PlateScale(spec, binning);
        double lambda = spec.LuminanceCentralWavelengthNm * 1e-9;
        double fwhm = spec.AdaptiveOpticsHaloSeeingFwhmArcsec;
        double r0 = OpticalPsf.FriedParameterMeters(fwhm, lambda);

        var profile = new RadialIntegral(r0, lambda);

        int wanted = (int)Math.Ceiling(OpticalPsf.AtmosphericTailRadiusInFwhm * fwhm / scale);
        const int cap = 256;
        int used = Math.Min(cap, wanted);

        Console.WriteLine($"{spec.CameraName}: halo FWHM {fwhm:F3}\", lambda {lambda * 1e9:F0} nm, "
                        + $"plate scale {scale:F5}\"/px at {binning}x{binning}");
        Console.WriteLine($"  the 1e-4 tail rule wants radius {wanted} px "
                        + $"({OpticalPsf.AtmosphericTailRadiusInFwhm:F2} FWHM); the fallback cap is {cap}, giving {used} px"
                        + $" = {used * scale:F4}\" = {used * scale / fwhm:F2} FWHM");
        Console.WriteLine($"  kernel array is {2 * used + 1} x {2 * used + 1} px in a {spec.NativeSensorWidthPx / binning} px frame"
                        + $" ({100.0 * (2.0 * used + 1.0) / (spec.NativeSensorWidthPx / binning):F0}% of its width)");

        double edgeArcsec = used * scale;
        double cornerArcsec = used * Math.Sqrt(2.0) * scale;
        Console.WriteLine($"  enclosed energy inside the inscribed circle: {profile.Enclosed(edgeArcsec) * 100.0:F2}%"
                        + $"   out to the corners: {profile.Enclosed(cornerArcsec) * 100.0:F2}%");

        double peak = profile.Intensity(0.0);
        Console.WriteLine($"  profile at the square's EDGE   ({edgeArcsec:F3}\"): {profile.Intensity(edgeArcsec) / peak:E3} of peak");
        Console.WriteLine($"  profile at the square's CORNER ({cornerArcsec:F3}\"): {profile.Intensity(cornerArcsec) / peak:E3} of peak");

        foreach (double target in new[] { 0.90, 0.95, 0.98, 0.99 })
        {
            double rad = profile.RadiusForEnclosed(target);
            if (double.IsNaN(rad)) { Console.WriteLine($"  {target * 100.0:F0}% enclosed: past the trusted range"); continue; }
            Console.WriteLine($"  {target * 100.0:F0}% enclosed at {rad:F3}\" = {rad / scale:F0} px = {rad / fwhm:F1} FWHM"
                            + $"  (profile there: {profile.Intensity(rad) / peak:E2} of peak)");
        }
        Console.WriteLine($"  energy past the tabulated range: {profile.TailBeyondGrid * 100.0:F3}%\n");
    }

    static void CoreReport(VisualTelescopeSpec spec)
    {
        int binning = Binning(spec);
        double scale = PlateScale(spec, binning);
        double lambda = spec.LuminanceCentralWavelengthNm * 1e-9;

        double delivered = spec.AdaptiveOpticsFwhmArcsec > 0.0
            ? spec.AdaptiveOpticsFwhmArcsec : spec.ZenithSeeingFwhmArcsec;
        double atm = OpticalPsf.AtmosphericFwhmForDelivered(delivered, scale,
            spec.ApertureMeters, spec.SecondaryObstructionFraction, lambda);

        float[] k = OpticalPsf.BuildKernel(scale, spec.ApertureMeters, spec.SecondaryObstructionFraction,
            lambda, atm, 0.0, spec.SpiderVaneCount, spec.SpiderVaneWidthMeters, out int r);
        if (k == null) { Console.WriteLine($"{spec.CameraName}: no kernel"); return; }

        int size = 2 * r + 1;
        double peak = k[r * size + r];
        double edge = k[r * size + (size - 1)];
        double corner = k[(size - 1) * size + (size - 1)];
        double seeingFwhm = delivered > 0.0 ? r * scale / delivered : 0.0;

        Console.WriteLine($"{spec.CameraName}: scale {scale:F5}\"/px, delivered {delivered:F3}\", "
                        + $"kernel radius {r} px = {r * scale:F4}\" = {seeingFwhm:F1} FWHM"
                        + $"{(r >= 128 ? "  <-- at the ceiling" : "")}");
        Console.WriteLine($"  step at the circular boundary: {edge / peak:E3} of peak"
                        + $"   (corner {corner / peak:E3}, zero because the support is now circular)");
    }

    /// <summary>
    /// FourierConvolution.Convolve against a literal O(K^2) convolution of the same image and
    /// kernel.
    ///
    /// Overlap-add is an exact restructuring of linear convolution, so the two must agree to
    /// float round-off. What makes it worth testing separately is the failure mode: every existing
    /// check in this project measures KERNELS, and a tiling bug leaves the kernel perfect while
    /// laying a grid of seams over the frame at the tile pitch. On a smooth, faint, hard-stretched
    /// subject (a nebula), that grid is the only thing with edges in the picture.
    ///
    /// The tile pitch is n - k + 1, which for the small kernels a short focal length produces is
    /// around 60 pixels whatever the binning, so the seams would be four times coarser on screen at
    /// 4x4 than at 1x1 for the same displayed size.
    /// </summary>
    static void TilingReport()
    {
        foreach (int kernelRadius in new[] { 1, 2, 4, 7, 16, 48, 128 })
        {
            const int w = 400, h = 260;

            // A smooth gradient plus one bright point: the gradient is what a nebula looks like to
            // the convolution, and the point is what shows a misplaced tail.
            var image = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    image[y * w + x] = 1000f + 400f * (float)Math.Sin(x * 0.013) * (float)Math.Cos(y * 0.017);
            image[(h / 2) * w + w / 3] += 50000f;

            int k = 2 * kernelRadius + 1;
            var kernel = new float[k * k];
            double sum = 0.0;
            for (int dy = -kernelRadius; dy <= kernelRadius; dy++)
                for (int dx = -kernelRadius; dx <= kernelRadius; dx++)
                {
                    double r2 = (double)dx * dx + (double)dy * dy;
                    double v = Math.Exp(-r2 / (2.0 * Math.Max(1.0, kernelRadius * kernelRadius / 4.0)));
                    kernel[(dy + kernelRadius) * k + dx + kernelRadius] = (float)v;
                    sum += v;
                }
            for (int i = 0; i < kernel.Length; i++) kernel[i] /= (float)sum;

            var direct = DirectConvolve(image, w, h, kernel, kernelRadius);
            var viaFft = (float[])image.Clone();
            FourierConvolution.Convolve(viaFft, w, h, kernel, kernelRadius);

            double worstAbs = 0.0, worstRel = 0.0;
            int worstX = 0, worstY = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double d = Math.Abs(viaFft[y * w + x] - direct[y * w + x]);
                    double rel = d / Math.Max(1e-6, Math.Abs(direct[y * w + x]));
                    if (rel > worstRel) { worstRel = rel; worstX = x; worstY = y; }
                    if (d > worstAbs) worstAbs = d;
                }

            Console.WriteLine($"  kernel radius {kernelRadius,3} px: worst {worstAbs:E2} absolute, "
                            + $"{worstRel:E2} relative at ({worstX},{worstY})"
                            + $"{(worstRel > 1e-4 ? "   <-- SEAMS" : "")}");
        }
        Console.WriteLine();
    }

    static float[] DirectConvolve(float[] image, int w, int h, float[] kernel, int radius)
    {
        int k = 2 * radius + 1;
        var outImage = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double acc = 0.0;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int sy = y - dy;
                    if (sy < 0 || sy >= h) continue;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int sx = x - dx;
                        if (sx < 0 || sx >= w) continue;
                        acc += image[sy * w + sx] * kernel[(dy + radius) * k + dx + radius];
                    }
                }
                outImage[y * w + x] = (float)acc;
            }
        return outImage;
    }

    /// <summary>The long-exposure Kolmogorov profile, tabulated once in reduced units and integrated on a geometric grid.</summary>
    sealed class RadialIntegral
    {
        readonly double[] _rho;
        readonly double[] _cumulative; // Int_0^rho PSF(t) t dt, which tends to 1
        readonly double _r0, _lambda;
        readonly double _rhoPerArcsec;

        public RadialIntegral(double r0, double lambdaMeters)
        {
            _r0 = r0; _lambda = lambdaMeters;
            _rhoPerArcsec = 2.0 * Math.PI * r0 * ArcsecToRad / lambdaMeters;

            _rho = new double[GridPoints];
            _cumulative = new double[GridPoints];
            var psf = new double[GridPoints];

            double ratio = Math.Pow(RhoMax / RhoMin, 1.0 / (GridPoints - 1));
            for (int i = 0; i < GridPoints; i++)
            {
                _rho[i] = RhoMin * Math.Pow(ratio, i);
                psf[i] = PsfAtRho(_rho[i]);
            }

            // Inside the first grid point the profile is flat to far under a part in 10^6, so the
            // cap contributes PSF(0)*rho0^2/2 exactly.
            _cumulative[0] = 0.5 * _rho[0] * _rho[0] * PsfAtRho(0.0);
            for (int i = 1; i < GridPoints; i++)
                _cumulative[i] = _cumulative[i - 1]
                    + 0.5 * (psf[i - 1] * _rho[i - 1] + psf[i] * _rho[i]) * (_rho[i] - _rho[i - 1]);
        }

        double PsfAtRho(double rho)
        {
            // rho = 2*pi*r0*theta/lambda, so r0 = 1 and lambda = 2*pi make rho = theta.
            return OpticalPsf.AtmosphericIntensity(rho, 1.0, 2.0 * Math.PI);
        }

        public double Intensity(double thetaArcsec)
            => Math.Max(0.0, OpticalPsf.AtmosphericIntensity(thetaArcsec * ArcsecToRad, _r0, _lambda));

        /// <summary>Fraction of the profile's total energy inside a radius. The total is 1 by the transform's own identity, not by integration.</summary>
        public double Enclosed(double radiusArcsec)
        {
            double rho = radiusArcsec * _rhoPerArcsec;
            if (rho <= _rho[0]) return _cumulative[0] * (rho * rho) / (_rho[0] * _rho[0]);
            if (rho >= _rho[GridPoints - 1]) return _cumulative[GridPoints - 1];
            int i = Array.BinarySearch(_rho, rho);
            if (i < 0) i = ~i;
            double f = (rho - _rho[i - 1]) / (_rho[i] - _rho[i - 1]);
            return _cumulative[i - 1] + f * (_cumulative[i] - _cumulative[i - 1]);
        }

        /// <summary>Energy the tabulated range itself misses, i.e. beyond RhoMax. Reported so the numbers above carry their own error bar.</summary>
        public double TailBeyondGrid => 1.0 - _cumulative[GridPoints - 1];

        /// <summary>Radius holding a given fraction, or NaN when it lies past the trusted range.</summary>
        public double RadiusForEnclosed(double fraction)
        {
            if (fraction >= _cumulative[GridPoints - 1]) return double.NaN;
            double lo = _rho[0], hi = _rho[GridPoints - 1];
            for (int i = 0; i < 80; i++)
            {
                double mid = Math.Sqrt(lo * hi);
                if (Enclosed(mid / _rhoPerArcsec) < fraction) lo = mid; else hi = mid;
            }
            return Math.Sqrt(lo * hi) / _rhoPerArcsec;
        }
    }

    /// <summary>The bare profile and its enclosed energy, for the Python side to check against GalSim's own Kolmogorov.</summary>
    static void DumpHaloProfile()
    {
        double fwhm = 0.72, lambda = 700e-9;
        double r0 = OpticalPsf.FriedParameterMeters(fwhm, lambda);
        var profile = new RadialIntegral(r0, lambda);

        var sb = new StringBuilder();
        sb.AppendLine("theta_arcsec,intensity,enclosed_fraction");
        for (int i = 0; i <= 1200; i++)
        {
            double t = i * 0.005;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}",
                t, profile.Intensity(t), profile.Enclosed(t)));
        }
        File.WriteAllText("exo_halo_profile.csv", sb.ToString());
        File.WriteAllText("exo_halo_meta.csv", string.Format(CultureInfo.InvariantCulture,
            "fwhm_arcsec,lambda_m,r0_m\n{0:R},{1:R},{2:R}\n", fwhm, lambda, r0));
        Console.WriteLine("written exo_halo_profile.csv, exo_halo_meta.csv");
    }
}

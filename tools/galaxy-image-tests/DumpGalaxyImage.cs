using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;

/// <summary>
/// Puts a packed shape map through the shipped renderer and writes out everything an independent
/// reprojection can be compared against.
///
/// Four things can go wrong between a packed map and a frame, and none of them throws:
///
///   * the transform can be MIRRORED, and a mirrored galaxy is still a galaxy;
///   * it can be affine instead of projective, which is right at the centre of the field and wrong
///     by arcminutes at the edge of a large one;
///   * the flux can fail to be conserved when the map's pixels and the frame's are different
///     sizes, which is a galaxy at the wrong brightness rather than in the wrong place;
///   * a frame pixel covering many map pixels can be point sampled, which aliases the arms into
///     something that still looks like structure.
///
/// So this dumps the deposit, the corner correspondences and the transform, and compare_image.py
/// rebuilds all three from the packed file with astropy's own WCS machinery.
/// </summary>
static class DumpGalaxyImage
{
    static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: dotnet run -p:Core=../../ExoInstruments/Core -- <GalaxyImages.galimg> [name]");
            return;
        }

        var set = new GalaxyImageSet();
        set.Load(args[0]);
        Console.WriteLine("{0} maps, {1}", set.Count, set.Source);

        string name = args.Length > 1 ? args[1] : null;
        if (name == null)
        {
            CheckEveryMap(set);
            return;
        }

        GalaxyImage image = set.Fetch(name);
        if (image == null) { Console.WriteLine("no map for " + name); return; }

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0}: {1} px at {2:F3} arcsec, survey {3}, masked {4:P1}, inside D25 {5:P1}",
            image.Name, image.Size, image.ScaleArcsec, image.SurveyId,
            image.MaskedFraction, image.FluxInsideD25));

        // 1. Every band must sum to one: that is the whole normalisation contract, and it is what
        //    lets the catalogue keep the photometry.
        var sb = new StringBuilder();
        sb.AppendLine("band,wavelength_nm,sum,peak,pixels");
        foreach (GalaxyImageBand band in image.Bands)
        {
            double sum = 0.0, peak = 0.0;
            foreach (float v in band.Values) { sum += v; if (v > peak) peak = v; }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-12} {1,6:F1} nm  sum {2:F9}  peak {3:E3}", band.Label, band.WavelengthNm, sum, peak));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:R},{2:R},{3:R},{4}",
                band.Label, band.WavelengthNm, sum, peak, band.Values.Length));
        }
        File.WriteAllText("exo_bands.csv", sb.ToString());

        // 2. The map's own geometry, so the Python side can check the deprojection against a real
        //    WCS rather than against this file's own arithmetic.
        var corners = new StringBuilder();
        corners.AppendLine("u,v,ra_deg,dec_deg");
        double last = image.Size - 1;
        double[] us = { 0.0, last, 0.0, last, image.CentrePixel, image.CentrePixel + 100.0 };
        double[] vs = { 0.0, 0.0, last, last, image.CentrePixel, image.CentrePixel };
        for (int i = 0; i < us.Length; i++)
        {
            image.MapPixelToRaDec(us[i], vs[i], out double ra, out double dec);
            corners.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R}",
                us[i], vs[i], ra, dec));
        }
        File.WriteAllText("exo_corners.csv", corners.ToString());

        // 3. A frame, built the way the camera builds one, pointed at the galaxy.
        //
        // The basis is the one the camera uses: a boresight, an up and a right that make a
        // right-handed set in the horizontal frame. Here the "horizontal" frame is simply used as
        // an equatorial one with the meridian at zero and the observer on the equator, which makes
        // altitude the declination and azimuth the right ascension; the transform under test does
        // not care which sphere it is, only that the same one is used on both sides.
        // The same galaxy from the catalogue's four numbers, on the same frames, so the two shape
        // models can be compared under everything else being equal. This is the picture the
        // question "is a bright ellipse really all there is?" is asking about.
        var catalogue = new GalaxyCatalog();
        bool haveCatalogue = false;
        Galaxy entry = default(Galaxy);
        if (args.Length > 2)
        {
            catalogue.Load(args[2]);
            haveCatalogue = catalogue.TryGetByName(name, out entry);
            Console.WriteLine(haveCatalogue
                ? string.Format(CultureInfo.InvariantCulture,
                    "catalogue: B_T {0:F2}, D25 {1:F2}', b/a {2:F2}, PA {3:F0}, T {4:+0.0;-0.0}",
                    entry.TotalBMag, entry.D25Arcmin, entry.AxisRatio, entry.PositionAngleDeg,
                    entry.MorphologicalType)
                : "no catalogue entry for " + name + ", so no Sersic comparison");
        }

        foreach (double fovDeg in new[] { 3.0, 1.0, 0.32, 0.08 })
        {
            string tag = fovDeg.ToString("0.00", CultureInfo.InvariantCulture);
            DumpFrame(image, fovDeg, "exo_frame_" + tag + ".bin");
            if (haveCatalogue) DumpSersicFrame(image, entry, fovDeg, "exo_sersic_" + tag + ".bin");
        }
        Console.WriteLine("written exo_bands.csv, exo_corners.csv, exo_frame_*.bin");
    }

    /// <summary>
    /// Reads every map in the file and checks the contract each one has to satisfy.
    ///
    /// A whole packed set is built unattended over hours, from services that fail in ways nothing
    /// warns about; one map with a broken normalisation would put one galaxy at the wrong
    /// brightness and nothing would say so. This walks the lot, which also exercises the lazy
    /// loader's eviction, since the file is far larger than what is held in memory.
    /// </summary>
    static void CheckEveryMap(GalaxyImageSet set)
    {
        Console.WriteLine("checking every map's normalisation and geometry");
        int checkedMaps = 0, failures = 0;
        double worstSum = 0.0;
        string worstName = null;
        double coarsest = 0.0;
        string coarsestName = null;

        foreach (string name in set.Names)
        {
            GalaxyImage image = set.Fetch(name);
            if (image == null || image.Bands == null)
            {
                Console.WriteLine("  " + name + ": FAILED to load");
                failures++;
                continue;
            }
            checkedMaps++;

            foreach (GalaxyImageBand band in image.Bands)
            {
                double sum = 0.0;
                bool finite = true;
                foreach (float v in band.Values)
                {
                    sum += v;
                    if (float.IsNaN(v) || float.IsInfinity(v) || v < 0.0f) finite = false;
                }
                double error = Math.Abs(sum - 1.0);
                if (error > worstSum) { worstSum = error; worstName = name + " " + band.Label; }
                if (error > 1e-5 || !finite)
                {
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  {0} {1}: sum {2:F9}{3}", name, band.Label, sum,
                        finite ? "" : ", and it holds a negative or non-finite value"));
                    failures++;
                }
            }

            if (image.ScaleArcsec > coarsest) { coarsest = image.ScaleArcsec; coarsestName = name; }

            // The tangent point must come back as the catalogued position, or every map is offset.
            image.MapPixelToRaDec(image.CentrePixel, image.CentrePixel,
                                  out double ra, out double dec);
            double offsetArcsec = Math.Sqrt(
                Math.Pow((ra - image.RaDeg) * Math.Cos(dec * Math.PI / 180.0), 2.0)
                + Math.Pow(dec - image.DecDeg, 2.0)) * 3600.0;
            if (offsetArcsec > 1e-6)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0}: the tangent point deprojects {1:E2} arcsec off its own position",
                    name, offsetArcsec));
                failures++;
            }
        }

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0} maps checked, {1} failure(s); worst normalisation error {2:E2} ({3}); "
            + "coarsest sampling {4:F2} arcsec/px ({5})",
            checkedMaps, failures, worstSum, worstName, coarsest, coarsestName));
    }

    const int FrameWidth = 512;
    const int FrameHeight = 384;

    /// <summary>
    /// The same galaxy drawn from its Sersic profile, on the same frame, at the same total flux.
    ///
    /// Deliberately identical in everything but the shape model: same field, same boresight, same
    /// electrons. What differs in the two dumps is only what the two models say about WHERE the
    /// light is.
    /// </summary>
    static void DumpSersicFrame(GalaxyImage image, Galaxy g, double fovDeg, string path)
    {
        BuildFrame(image, fovDeg, out GnomonicProjection projection,
                   out double raDeg, out double decDeg);

        double plateScale = fovDeg * 3600.0 / FrameWidth;
        HorizontalCoordinates altAz = SkyCoordinates.EquatorialToHorizontal(g.RaDeg, g.DecDeg, 0.0, 0.0);
        if (!projection.TryProject(SkyVector.FromHorizontal(g.DecDeg, g.RaDeg),
                                   out double cx, out double cy)) return;

        // Major-axis direction the same way the camera derives it: by projecting a second point
        // one arcminute along the position angle, so parity and field rotation are inherited.
        const double stepDeg = 1.0 / 60.0;
        double pa = g.PositionAngleDeg * Math.PI / 180.0;
        double cosDec = Math.Cos(g.DecDeg * Math.PI / 180.0);
        double ra2 = g.RaDeg + (Math.Abs(cosDec) > 1e-6 ? stepDeg * Math.Sin(pa) / cosDec : 0.0);
        double dec2 = g.DecDeg + stepDeg * Math.Cos(pa);
        if (!projection.TryProject(SkyVector.FromHorizontal(dec2, ra2),
                                   out double tx, out double ty)) return;

        double n = g.SersicIndex > 0.0 ? g.SersicIndex
                                       : GalaxyCatalog.SersicIndexForType(g.MorphologicalType);
        double reArcsec = SersicProfile.EffectiveRadiusFromIsophote(
            g.TotalBMag, g.SemiMajorArcsec, 25.0, n);
        double rePx = double.IsNaN(reArcsec)
            ? (g.SemiMajorArcsec / plateScale) / Math.Max(1e-6, SersicProfile.RadiusForEnclosedFraction(0.9, n))
            : reArcsec / plateScale;
        if (!(rePx > 0.0)) return;

        const double totalElectrons = 1.0e6;
        double radii = GalaxyRenderer.TruncationRadiiForFloor(
            totalElectrons, rePx, g.AxisRatio, n, 1.0, 12.0);
        var plane = new float[FrameWidth * FrameHeight];
        double deposited = GalaxyRenderer.Deposit(
            plane, FrameWidth, FrameHeight, cx, cy, tx - cx, ty - cy,
            rePx, g.AxisRatio, n, totalElectrons, radii);

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  fov {0:F2} deg, Sersic n = {1:F1}, R_e {2:F1} px: deposited {3:P3} of the total",
            fovDeg, n, rePx, deposited / totalElectrons));

        WriteFrame(path, fovDeg, raDeg, decDeg, totalElectrons, deposited,
                   new double[8], new double[4], new double[4], plane);
    }

    static void BuildFrame(GalaxyImage image, double fovDeg, out GnomonicProjection projection,
                           out double raDeg, out double decDeg)
    {
        raDeg = image.RaDeg + 0.25 * fovDeg / Math.Cos(image.DecDeg * Math.PI / 180.0);
        decDeg = image.DecDeg - 0.15 * fovDeg;

        SkyVector boresight = SkyVector.FromHorizontal(decDeg, raDeg);
        SkyVector north = SkyVector.FromHorizontal(decDeg + 0.001, raDeg);
        double dot = north.Dot(boresight);
        SkyVector up = SkyVector.Normalized(north.X - dot * boresight.X,
                                            north.Y - dot * boresight.Y,
                                            north.Z - dot * boresight.Z);
        SkyVector right = SkyVector.Normalized(
            up.Y * boresight.Z - up.Z * boresight.Y,
            up.Z * boresight.X - up.X * boresight.Z,
            up.X * boresight.Y - up.Y * boresight.X);
        projection = new GnomonicProjection(boresight, up, right, fovDeg, FrameWidth, FrameHeight);
    }

    static void WriteFrame(string path, double fovDeg, double raDeg, double decDeg,
                           double totalElectrons, double deposited,
                           double[] h, double[] frameX, double[] frameY, float[] plane)
    {
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(FrameWidth);
            writer.Write(FrameHeight);
            writer.Write(fovDeg);
            writer.Write(raDeg);
            writer.Write(decDeg);
            writer.Write(totalElectrons);
            writer.Write(deposited);
            for (int i = 0; i < 8; i++) writer.Write(h[i]);
            for (int i = 0; i < 4; i++) { writer.Write(frameX[i]); writer.Write(frameY[i]); }
            foreach (float v in plane) writer.Write(v);
        }
    }

    static void DumpFrame(GalaxyImage image, double fovDeg, string path)
    {
        // Boresight offset deliberately from the galaxy's centre, so a mirrored or transposed
        // transform cannot hide behind symmetry.
        double raDeg = image.RaDeg + 0.25 * fovDeg / Math.Cos(image.DecDeg * Math.PI / 180.0);
        double decDeg = image.DecDeg - 0.15 * fovDeg;

        SkyVector boresight = SkyVector.FromHorizontal(decDeg, raDeg);
        // North on the sensor, and east to its right, both made orthogonal to the boresight.
        SkyVector north = SkyVector.FromHorizontal(decDeg + 0.001, raDeg);
        double dot = north.Dot(boresight);
        SkyVector up = SkyVector.Normalized(north.X - dot * boresight.X,
                                            north.Y - dot * boresight.Y,
                                            north.Z - dot * boresight.Z);
        SkyVector right = SkyVector.Normalized(
            up.Y * boresight.Z - up.Z * boresight.Y,
            up.Z * boresight.X - up.X * boresight.Z,
            up.X * boresight.Y - up.Y * boresight.X);

        var projection = new GnomonicProjection(boresight, up, right, fovDeg, FrameWidth, FrameHeight);

        double last = image.Size - 1;
        var mapU = new double[] { 0.0, last, 0.0, last };
        var mapV = new double[] { 0.0, 0.0, last, last };
        var frameX = new double[4];
        var frameY = new double[4];
        for (int i = 0; i < 4; i++)
        {
            image.MapPixelToRaDec(mapU[i], mapV[i], out double ra, out double dec);
            if (!projection.TryProject(SkyVector.FromHorizontal(dec, ra), out frameX[i], out frameY[i]))
            {
                Console.WriteLine("  corner " + i + " does not project at fov " + fovDeg);
                return;
            }
        }

        double[] h = GalaxyImageRenderer.SolveFrameToMap(frameX, frameY, mapU, mapV);
        if (h == null) { Console.WriteLine("  degenerate transform at fov " + fovDeg); return; }

        const double totalElectrons = 1.0e6;
        var plane = new float[FrameWidth * FrameHeight];
        double deposited = GalaxyImageRenderer.Deposit(
            plane, FrameWidth, FrameHeight, image, h,
            image.Bands[0].WavelengthNm, totalElectrons, frameX, frameY);

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  fov {0:F2} deg: deposited {1:E6} of {2:E1} electrons ({3:P3} of the total)",
            fovDeg, deposited, totalElectrons, deposited / totalElectrons));

        // The wavelength blend must not move flux. Both stored maps sum to one, so any convex
        // combination of them does too, and a filter sitting between the two measured bands has to
        // collect exactly what either of them would: the interpolation is allowed to change WHERE
        // the light is and nothing else.
        if (image.Bands.Length > 1)
        {
            double mid = 0.5 * (image.Bands[0].WavelengthNm + image.Bands[1].WavelengthNm);
            var blended = new float[FrameWidth * FrameHeight];
            double blendedTotal = GalaxyImageRenderer.Deposit(
                blended, FrameWidth, FrameHeight, image, h, mid, totalElectrons, frameX, frameY);
            var other = new float[FrameWidth * FrameHeight];
            double otherTotal = GalaxyImageRenderer.Deposit(
                other, FrameWidth, FrameHeight, image, h,
                image.Bands[1].WavelengthNm, totalElectrons, frameX, frameY);

            double differing = 0.0, peak = 0.0;
            for (int i = 0; i < blended.Length; i++)
            {
                differing += Math.Abs(blended[i] - plane[i]);
                if (blended[i] > peak) peak = blended[i];
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "    blend at {0:F0} nm: total {1:P4} of the first band's, and {2:P2} of the flux "
                + "moved between bands",
                mid, blendedTotal / Math.Max(deposited, 1e-30),
                differing / Math.Max(2.0 * deposited, 1e-30)));
        }

        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(FrameWidth);
            writer.Write(FrameHeight);
            writer.Write(fovDeg);
            writer.Write(raDeg);
            writer.Write(decDeg);
            writer.Write(totalElectrons);
            writer.Write(deposited);
            for (int i = 0; i < 8; i++) writer.Write(h[i]);
            for (int i = 0; i < 4; i++) { writer.Write(frameX[i]); writer.Write(frameY[i]); }
            foreach (float v in plane) writer.Write(v);
        }
    }
}

using System;
using System.Globalization;
using System.IO;
using System.Text;
using ExoInstruments.Core;

/// <summary>
/// Reproduces SolarSystemCameraTexture.DepositEmissionField's own per-pixel chain, outside the
/// game, and writes the rayleighs it reads for every pixel of a frame.
///
/// Every component of that chain has been validated in isolation -- the projection, the Galactic
/// rotation to 6e-13 deg, the HEALPix interpolation exactly against healpy over 21336 directions.
/// What has never been run end to end is the COMPOSITION, over a real frame, at a real pointing.
/// A frame of the Horsehead showed patches where the deposited emission is near zero while the map
/// holds 290 R, and only the whole chain together can produce that.
/// </summary>
static class DumpDeposit
{
    static void Main(string[] args)
    {
        string mapPath = args.Length > 0 ? args[0]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library/Application Support/Steam/steamapps/common/Kerbal Space Program",
                "GameData/ExoInstruments/PluginData/HalphaMap.emission");
        var map = new EmissionMap();
        map.Load(mapPath);
        Console.WriteLine($"map: nside {map.Nside}, {map.Source}");

        // The user's own configuration: RedCat at 1x1, pointed at the Horsehead.
        const int w = 4144, h = 2822;
        const double plateScale = 3.8200;
        const double raDeg = 85.25, decDeg = -2.20472;
        const double latitudeDeg = 28.53;

        // The frame geometry the camera builds: the aim direction in the site's own
        // (north, east, up) basis, and a gnomonic projection about it.
        double meridianRaDeg = raDeg;                       // target on the meridian
        HorizontalCoordinates altAz = SkyCoordinates.EquatorialToHorizontal(
            raDeg, decDeg, meridianRaDeg, latitudeDeg);
        Console.WriteLine($"aim: alt {altAz.AltitudeDeg:F4} az {altAz.AzimuthDeg:F4}");

        SkyVector boresight = SkyVector.FromHorizontal(altAz.AltitudeDeg, altAz.AzimuthDeg);

        // An orthonormal camera frame about that boresight: "up" is the zenith direction with the
        // boresight component removed, "right" completes it. The roll is not the game's exact one,
        // and does not need to be -- the question is whether the CHAIN produces holes, not where
        // they land.
        SkyVector zenith = SkyVector.FromHorizontal(90.0, 0.0);
        double d = zenith.Dot(boresight);
        SkyVector up = SkyVector.Normalized(zenith.X - d * boresight.X,
                                            zenith.Y - d * boresight.Y,
                                            zenith.Z - d * boresight.Z);
        SkyVector right = SkyVector.Normalized(
            up.Y * boresight.Z - up.Z * boresight.Y,
            up.Z * boresight.X - up.X * boresight.Z,
            up.X * boresight.Y - up.Y * boresight.X);

        double fovDeg = w * plateScale / 3600.0;
        var projection = new GnomonicProjection(boresight, up, right, fovDeg, w, h);
        var rotation = HorizontalToGalactic.Build(meridianRaDeg, latitudeDeg);
        Console.WriteLine($"field {fovDeg:F3} deg wide; rotation valid: {rotation.IsValid}");

        EmissionMap.AllocateScratch(out long[] px, out double[] wt);

        long nan = 0, zero = 0, ok = 0;
        double min = double.MaxValue, max = 0.0;
        var sb = new StringBuilder();
        sb.AppendLine("x,y,l_deg,b_deg,rayleighs");

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                SkyVector dir = projection.Deproject(x + 0.5, y + 0.5);
                rotation.ToGalactic(dir, out double l, out double b);
                double r = map.RayleighsAtGalactic(l, b, px, wt);

                if (double.IsNaN(r)) nan++;
                else if (!(r > 0.0)) zero++;
                else { ok++; if (r < min) min = r; if (r > max) max = r; }

                // A coarse dump for the Python side, plus every pixel of the band in full.
                bool inBand = y >= 1600 && y < 1700 && x >= 2000 && x < 2500;
                if ((x % 16 == 0 && y % 16 == 0) || (inBand && x % 2 == 0 && y % 2 == 0))
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1},{2:R},{3:R},{4:R}", x, y, l, b, r));
            }
        }

        Console.WriteLine($"\npixels: {ok} with a value, {zero} at zero, {nan} NaN");
        Console.WriteLine($"rayleighs over the frame: {min:F1} to {max:F1}");

        File.WriteAllText("exo_deposit_field.csv", sb.ToString());
        Console.WriteLine("written exo_deposit_field.csv");
    }
}

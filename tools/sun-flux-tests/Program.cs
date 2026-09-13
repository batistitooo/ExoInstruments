using System;
using ExoInstruments.Core;

/// <summary>
/// Headless checks for the Sun's own flux and the distance reflected sunlight is scaled against:
/// PhotonFluxModel.SunApparentMagnitude, HomeStarOrbitMeters and SunReferenceDistanceMeters.
/// Compiles PhotonFluxModel alone, so nothing else in Core can stop it building.
///
/// Run: dotnet run -p:Core=../../ExoInstruments/Core
/// </summary>
class Program
{
    // Stock semi-major axes and the Mun's radius, metres.
    const double KerbinSma = 13599840256.0;
    const double JoolSma = 68773560320.0;
    const double MunRadius = 200000.0, MunSma = 12000000.0;
    // Real Solar System's Earth.
    const double RssEarthSma = 149598261150.0;
    // Physics.cfg solarLuminosityAtHome on a stock install, W/m2.
    const double StockFluxAtHome = 1360.0;

    // A body in a parent chain, standing in for CelestialBody.
    sealed class Body
    {
        public readonly Body Parent;
        public readonly double Sma;
        public Body(Body parent, double sma) { Parent = parent; Sma = sma; }
    }

    static double Walk(Body home, Body star)
        => PhotonFluxModel.HomeStarOrbitMeters(home, star, b => b.Parent, b => b.Sma);

    static int failures = 0;

    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + "   " + detail);
        if (!ok) failures++;
    }

    static void Main()
    {
        double atAu = PhotonFluxModel.SunApparentMagnitude(PhotonFluxModel.AuMeters, PhotonFluxModel.AuMeters);
        Check("1 AU gives the photometric constant",
              Math.Abs(atAu - PhotonFluxModel.SunApparentMagnitudeV) < 1e-12, $"{atAu:F12}");

        double near = PhotonFluxModel.SunApparentMagnitude(1.0e10, PhotonFluxModel.AuMeters);
        double far = PhotonFluxModel.SunApparentMagnitude(1.0e11, PhotonFluxModel.AuMeters);
        Check("ten times the distance is exactly five magnitudes",
              Math.Abs(far - near - 5.0) < 1e-12, $"{far - near:F12}");

        // The flux a body reflects is the flux the Sun sends, so a full phase albedo 1 body sits below
        // the Sun by its own solid angle term and nothing else.
        double mun = PhotonFluxModel.ApparentMagnitude(1.0, MunRadius, KerbinSma, MunSma, 0.0, KerbinSma);
        double sunAtMun = PhotonFluxModel.SunApparentMagnitude(KerbinSma, KerbinSma);
        double expected = -2.5 * Math.Log10((MunRadius / MunSma) * (MunRadius / MunSma));
        Check("reflected and direct sunlight share one scale",
              Math.Abs((mun - sunAtMun) - expected) < 1e-9, $"{mun - sunAtMun:F9} vs {expected:F9} mag");

        // The parent walk, on stock, a moon home world, RSS, and a chain that never reaches the star.
        var kerbol = new Body(null, 0.0);
        var kerbin = new Body(kerbol, KerbinSma);
        var munBody = new Body(kerbin, MunSma);
        double stockOrbit = Walk(kerbin, kerbol);
        Check("stock home orbit is Kerbin's semi-major axis", stockOrbit == KerbinSma, $"{stockOrbit:F0} m");
        double auOverKerbin = PhotonFluxModel.AuMeters / stockOrbit;
        Check("the AU is 121x Kerbin's orbit in flux", Math.Abs(auOverKerbin * auOverKerbin - 121.00) < 0.005,
              $"(AU/a)^2 = {auOverKerbin * auOverKerbin:F4}");
        Check("a moon home world is lit at its planet's distance", Walk(munBody, kerbol) == KerbinSma,
              $"{Walk(munBody, kerbol):F0} m");

        var sun = new Body(null, 0.0);
        var earth = new Body(sun, RssEarthSma);
        double rssReference = PhotonFluxModel.SunReferenceDistanceMeters(Walk(earth, sun), StockFluxAtHome);
        Check("RSS puts the reference at 1 AU", Math.Abs(rssReference / PhotonFluxModel.AuMeters - 1.0) < 5e-4,
              $"{rssReference / PhotonFluxModel.AuMeters:F6} AU");

        var orphan = new Body(new Body(null, 0.0), KerbinSma);
        double lost = Walk(orphan, kerbol);
        Check("a chain that never reaches the star falls back to the AU",
              double.IsNaN(lost) && PhotonFluxModel.SunReferenceDistanceMeters(lost, StockFluxAtHome) == PhotonFluxModel.AuMeters,
              $"walk {lost}, reference {PhotonFluxModel.SunReferenceDistanceMeters(lost, StockFluxAtHome):F0} m");

        // solarLuminosityAtHome enters as a flux ratio against 1361 W/m2.
        double stockReference = PhotonFluxModel.SunReferenceDistanceMeters(stockOrbit, StockFluxAtHome);
        double fromKerbin = PhotonFluxModel.SunApparentMagnitude(KerbinSma, stockReference);
        Check("the Sun from Kerbin at stock 1360 W/m2 is 0.0008 mag fainter than -26.74",
              Math.Abs(fromKerbin - (-26.74 + 2.5 * Math.Log10(1361.0 / 1360.0))) < 1e-9, $"V = {fromKerbin:F4}");
        double doubled = PhotonFluxModel.SunApparentMagnitude(
            KerbinSma, PhotonFluxModel.SunReferenceDistanceMeters(stockOrbit, 2722.0));
        Check("twice the solar constant at home is 0.753 mag brighter",
              Math.Abs(doubled - (-26.74 - 0.7526)) < 5e-4, $"V = {doubled:F4}");

        // Pinned numbers, so a reference that drifts cannot pass.
        double auFromKerbin = PhotonFluxModel.SunApparentMagnitude(PhotonFluxModel.AuMeters, KerbinSma) + 26.74;
        Check("the AU seen against Kerbin's orbit is 5.207 mag fainter", Math.Abs(auFromKerbin - 5.2070) < 5e-4,
              $"{auFromKerbin:F4} mag");
        double jool = PhotonFluxModel.SunApparentMagnitude(JoolSma, KerbinSma) + 26.74;
        Check("the Sun at Jool is 3.519 mag fainter than at Kerbin", Math.Abs(jool - 3.5195) < 5e-4, $"{jool:F4} mag");

        Check("no distance, no signal",
              double.IsPositiveInfinity(PhotonFluxModel.SunApparentMagnitude(0.0, KerbinSma))
              && PhotonFluxModel.CollectedElectrons(PhotonFluxModel.SunApparentMagnitude(0.0, KerbinSma), 1000.0, 300.0, 1.0) == 0.0,
              "+Infinity, 0 e-");

        // What the black frame was missing: LORRI's 300 cm2 at 1 ms, a nominal 2000 A width.
        double electrons = PhotonFluxModel.CollectedElectrons(fromKerbin, 2000.0, 300.0, 0.001);
        Check("the Sun from Kerbin delivers a signal", electrons > 1e12, $"{electrons:E3} e- over the whole disc");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}

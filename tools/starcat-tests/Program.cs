using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using ExoInstruments.Core;

// The rendered star catalogue at the depth of all of Gaia DR3: that the mapped reader finds what the file
// holds, that a magnitude tier is exactly the main file down to its cut, that the tiers alone stand in for
// it, and what a frame costs.
static class Program
{
    static int failures;

    // Restates SolarSystemCameraTexture.MaxStarCandidatesPerFrame.
    const long FrameBudget = 10000000;

    const string Fixture = "../bandpass-wcs-tests/fixtures/gaia_cone_test.starcat";

    // The names and cuts the sky-data release installs.
    const string Stem = "GaiaStarCatalog";
    static readonly double[] ReleaseCuts = { 13.0, 15.0, 17.0, 19.0 };

    static void Check(string name, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + "   " + detail);
        if (!ok) failures++;
    }

    static void Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.WriteLine($"runtime {Environment.Version}, {(Type.GetType("Mono.Runtime") != null ? "Mono" : ".NET")}");
        Console.WriteLine();

        // --cost runs only the timings, which is what changes when the renderer is tuned.
        bool costOnly = Array.IndexOf(args, "--cost") >= 0;
        if (!costOnly)
        {
            TestAgainstReference("Committed fixture (format v2, 923 stars)", Fixture, fixture: true);
            TestSyntheticTiersOnly();
        }

        string installed = InstalledCatalogue();
        if (installed == null)
        {
            Console.WriteLine("  SKIP  no installed catalogue (set EXOINSTRUMENTS_STARCAT to point at one)");
        }
        else
        {
            if (!costOnly)
            {
                TestAgainstReference("Installed catalogue: " + installed, installed, fixture: false);
                TestTiers(installed);
                TestInstalledTiersOnly(installed);
                TestForeignMain(installed);
            }
            TestCost(installed);
        }

        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    static string InstalledCatalogue()
    {
        string fromEnv = Environment.GetEnvironmentVariable("EXOINSTRUMENTS_STARCAT");
        if (!string.IsNullOrEmpty(fromEnv)) return File.Exists(fromEnv) ? fromEnv : null;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string root in new[] {
            "/Library/Application Support/Steam/steamapps/common/Kerbal Space Program",
            "/.steam/steam/steamapps/common/Kerbal Space Program",
            "/.local/share/Steam/steamapps/common/Kerbal Space Program",
        })
        {
            string path = home + root + "/GameData/ExoInstruments/PluginData/GaiaStarCatalog.starcat";
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // ------------------------------------------------------------------ 1. The reader against the file
    //
    // The reference reads every record of the bands a cone overlaps straight off the disk, with no RA
    // bracketing and no mapping, and applies the same membership test. Agreement to the last bit on every
    // star is the check.

    static void TestAgainstReference(string title, string path, bool fixture)
    {
        Console.WriteLine(title);
        var reference = new ReferenceReader(path);
        var catalog = new RenderedStarCatalog();
        catalog.Load(path);
        Check("loads, mapped", catalog.IsLoaded && catalog.Count == reference.Count,
              $"{catalog.Count:N0} stars, header says {reference.Count:N0}");

        var cones = new List<double[]>();
        if (fixture)
        {
            cones.Add(new[] { 266.4, -29.0, 0.35 });
            cones.Add(new[] { 266.4, -29.0, 0.12 });
            cones.Add(new[] { 266.1, -28.8, 0.2 });
        }
        else
        {
            cones.Add(new[] { 266.4, -29.0, 0.3 });     // Galactic centre, the densest band
            cones.Add(new[] { 10.0, 89.95, 0.3 });      // over the north pole
            cones.Add(new[] { 200.0, -89.9, 0.3 });     // over the south pole
            cones.Add(new[] { 0.02, 10.0, 0.3 });       // straddling 0h
            cones.Add(new[] { 359.98, -40.0, 0.3 });
            var rng = new Random(20260914);
            for (int i = 0; i < 16; i++)
            {
                double dec = Math.Asin(2.0 * rng.NextDouble() - 1.0) * 180.0 / Math.PI;
                cones.Add(new[] { 360.0 * rng.NextDouble(), dec, 0.05 + 0.25 * rng.NextDouble() });
            }
        }

        int agreed = 0, stars = 0;
        string firstMismatch = null;
        long candidatesShort = 0;
        foreach (double[] cone in cones)
        {
            foreach (double limit in new[] { 99.0, 13.5, 18.25 })
            {
                List<RenderedStar> expected = reference.Cone(cone[0], cone[1], cone[2], limit);
                var listed = new List<RenderedStar>();
                catalog.Search(cone[0], cone[1], cone[2], limit, listed);
                long visited = 0;
                catalog.Search(cone[0], cone[1], cone[2], limit, (Action<RenderedStar>)(s => visited++));

                bool same = SameStars(expected, listed) && visited == listed.Count;
                if (same) agreed++;
                else if (firstMismatch == null)
                    firstMismatch = $"RA {cone[0]:F2} Dec {cone[1]:F2} r {cone[2]:F2} V<{limit}: reference {expected.Count}, search {listed.Count}, visitor {visited}";
                stars += expected.Count;

                long candidates = catalog.CountCandidates(cone[0], cone[1], cone[2]);
                if (candidates < listed.Count) candidatesShort++;
            }
        }
        int searches = cones.Count * 3;
        Check("every search returns exactly the stars the file holds in the cone", agreed == searches,
              firstMismatch ?? $"{searches} searches, {stars:N0} stars, identical in position, magnitude, colour and reddening");
        Check("the candidate count is never below what the search returns", candidatesShort == 0,
              $"{candidatesShort} of {searches} came in under");
        catalog.Dispose();
        Console.WriteLine();
    }

    static bool SameStars(List<RenderedStar> a, List<RenderedStar> b)
    {
        if (a.Count != b.Count) return false;
        var ka = Keys(a);
        var kb = Keys(b);
        for (int i = 0; i < ka.Count; i++)
            if (ka[i] != kb[i]) return false;
        return true;
    }

    static List<string> Keys(List<RenderedStar> stars)
    {
        var keys = new List<string>(stars.Count);
        foreach (RenderedStar s in stars)
        {
            keys.Add(string.Join(",", Bits(s.RaDeg), Bits(s.DecDeg), Bits(s.VMag), Bits(s.ColorIndexBV), Bits(s.ReddeningEBv)));
        }
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    static string Bits(double v) => BitConverter.DoubleToInt64Bits(v).ToString("x16");

    sealed class ReferenceReader
    {
        public readonly int Count;
        readonly string path;
        readonly int bands, recordBytes;
        readonly double width;
        readonly uint[] start;
        readonly long recordsAt;

        public ReferenceReader(string path)
        {
            this.path = path;
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                reader.ReadBytes(8);
                int version = reader.ReadInt32();
                Count = reader.ReadInt32();
                bands = reader.ReadInt32();
                width = reader.ReadSingle();
                start = new uint[bands + 1];
                for (int i = 0; i <= bands; i++) start[i] = reader.ReadUInt32();
                recordBytes = version >= 3 ? 14 : 12;
                recordsAt = reader.BaseStream.Position;
            }
        }

        int Band(double dec)
        {
            int b = (int)((dec + 90.0) / width);
            return b < 0 ? 0 : (b >= bands ? bands - 1 : b);
        }

        public List<RenderedStar> Cone(double ra, double dec, double radius, double limit)
        {
            var found = new List<RenderedStar>();
            double cosRadius = Math.Cos(radius * Math.PI / 180.0);
            double sinC = Math.Sin(dec * Math.PI / 180.0), cosC = Math.Cos(dec * Math.PI / 180.0);
            ushort faintest = RenderedStarCatalog.ToMagMilli(limit);
            using (var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)))
            {
                for (int b = Band(dec - radius); b <= Band(dec + radius); b++)
                {
                    reader.BaseStream.Seek(recordsAt + (long)start[b] * recordBytes, SeekOrigin.Begin);
                    for (uint i = start[b]; i < start[b + 1]; i++)
                    {
                        uint raFixed = reader.ReadUInt32();
                        int decFixed = reader.ReadInt32();
                        ushort v = reader.ReadUInt16();
                        short bv = reader.ReadInt16();
                        ushort ebv = recordBytes == 14 ? reader.ReadUInt16() : (ushort)65535;
                        if (v > faintest) continue;
                        double starRa = raFixed * (360.0 / 4294967296.0);
                        double starDec = decFixed * (180.0 / 4294967296.0);
                        double decRad = starDec * Math.PI / 180.0;
                        double dRa = (starRa - ra) * Math.PI / 180.0;
                        if (sinC * Math.Sin(decRad) + cosC * Math.Cos(decRad) * Math.Cos(dRa) < cosRadius) continue;
                        found.Add(new RenderedStar
                        {
                            RaDeg = starRa,
                            DecDeg = starDec,
                            VMag = v / 1000.0 - 2.0,
                            ColorIndexBV = bv == -32768 ? double.NaN : bv / 1000.0,
                            ReddeningEBv = ebv == 65535 ? double.NaN : ebv / 1000.0,
                        });
                    }
                }
            }
            return found;
        }
    }

    // ------------------------------------------------------------------ 2. Magnitude tiers

    static void TestTiers(string path)
    {
        Console.WriteLine("Magnitude tiers");
        var tiered = new TieredStarCatalog();
        tiered.Load(path);
        foreach (string refused in tiered.Report) Console.WriteLine("        refused: " + refused);
        List<double> cuts = tiered.TierCuts;
        if (cuts.Count == 0)
        {
            Console.WriteLine("  SKIP  no tiers beside the catalogue (tools/tier_star_catalog.py cuts them)");
            Console.WriteLine();
            return;
        }
        Console.WriteLine("        tiers at V " + string.Join(", ", cuts.ConvertAll(c => c.ToString(CultureInfo.InvariantCulture))));
        Check("every tier beside the catalogue is accepted", tiered.Report.Count == 0, $"{tiered.Report.Count} refused");

        var main = new RenderedStarCatalog();
        main.Load(path);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path));
        string stem = Path.GetFileNameWithoutExtension(path);
        var rng = new Random(314159);

        foreach (double cut in cuts)
        {
            var tier = new RenderedStarCatalog();
            tier.Load(Path.Combine(directory, stem + ".V" + cut.ToString(CultureInfo.InvariantCulture) + ".starcat"));
            int agreed = 0, searches = 0;
            long stars = 0;
            string mismatch = null;
            for (int i = 0; i < 12; i++)
            {
                double dec = Math.Asin(2.0 * rng.NextDouble() - 1.0) * 180.0 / Math.PI;
                double ra = 360.0 * rng.NextDouble(), radius = 0.1 + 0.9 * rng.NextDouble();
                foreach (double limit in new[] { cut, cut - 1.3 })
                {
                    var fromMain = new List<RenderedStar>();
                    var fromTier = new List<RenderedStar>();
                    main.Search(ra, dec, radius, limit, fromMain);
                    tier.Search(ra, dec, radius, limit, fromTier);
                    searches++;
                    stars += fromMain.Count;
                    if (SameStars(fromMain, fromTier)) agreed++;
                    else if (mismatch == null)
                        mismatch = $"RA {ra:F2} Dec {dec:F2} r {radius:F2} V<{limit}: main {fromMain.Count}, tier {fromTier.Count}";
                }
            }
            Check($"V {cut} tier holds exactly the main file's stars down to its cut", agreed == searches,
                  mismatch ?? $"{searches} searches, {stars:N0} stars identical");
            tier.Dispose();
        }

        // A wide field, where the tier is the whole point.
        {
            var fromMain = new List<RenderedStar>();
            main.Search(266.4, -29.0, 3.2, cuts[0], fromMain);
            var fromTiered = new List<RenderedStar>();
            StarFieldPlan wide = tiered.Search(266.4, -29.0, 3.2, cuts[0], long.MaxValue, fromTiered.Add);
            Check("a RedCat 51 field at the Galactic centre is exact through the shallowest tier",
                  SameStars(fromMain, fromTiered) && !wide.LimitedByBudget,
                  $"{fromTiered.Count:N0} stars from {wide.Source}, reading {wide.Candidates:N0} records "
                  + $"where the main file reads {main.CountCandidates(266.4, -29.0, 3.2):N0}");
        }

        string first = stem + ".V" + cuts[0].ToString(CultureInfo.InvariantCulture) + ".starcat";
        StarFieldPlan below = tiered.Plan(80.0, 20.0, 0.3, cuts[0] - 0.5, long.MaxValue);
        StarFieldPlan at = tiered.Plan(80.0, 20.0, 0.3, cuts[0], long.MaxValue);
        StarFieldPlan past = tiered.Plan(80.0, 20.0, 0.3, cuts[cuts.Count - 1] + 0.001, long.MaxValue);
        Check("the shallowest complete file serves a search", below.Source == first && at.Source == first
                  && past.Source == Path.GetFileName(path),
              $"V {cuts[0] - 0.5} and V {cuts[0]} from {at.Source}, V {cuts[cuts.Count - 1] + 0.001} from {past.Source}");

        // BRITE toward the Galactic centre: the depth asked for is past the budget, so the field steps down
        // to a tier that fits and says so.
        StarFieldPlan brite = tiered.Plan(266.4, -29.0, 17.6, 18.0, FrameBudget);
        Check("a field past the budget steps down whole to the deepest tier that fits",
              brite.LimitedByBudget && brite.Candidates <= FrameBudget && brite.LimitVMag < 18.0
                  && cuts.Contains(brite.LimitVMag),
              $"BRITE at V 18: {brite.Source}, V {brite.LimitVMag}, {brite.Candidates:N0} records");

        main.Dispose();
        tiered.Dispose();
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ 3. Tiers without the main file
    //
    // The compact download installs the tiers and no main file: the deepest tier is then the base and caps
    // every frame. A synthetic all-sky catalogue checks that anywhere; links to the installed tiers check it
    // at full scale.

    const int SyntheticBands = 180;
    const float SyntheticBandWidth = 1.0f;

    struct Record
    {
        public uint Ra;
        public int Dec;
        public ushort V;
        public short Bv;
        public ushort Ebv;
        public int Band;
    }

    static string TierName(double cut) => Stem + ".V" + cut.ToString(CultureInfo.InvariantCulture) + ".starcat";

    static void TestSyntheticTiersOnly()
    {
        Console.WriteLine("Tiers without the main file: a synthetic all-sky catalogue of 400,000 stars");
        string root = TempDirectory();
        string full = Path.Combine(root, "full"), tiers = Path.Combine(root, "tiers");
        try
        {
            Directory.CreateDirectory(full);
            Directory.CreateDirectory(tiers);
            List<Record> sky = SyntheticSky(400000, 20260914);
            WriteCatalogue(Path.Combine(full, Stem + ".starcat"), sky);
            foreach (double cut in ReleaseCuts)
            {
                ushort faintest = RenderedStarCatalog.ToMagMilli(cut);
                List<Record> tier = sky.FindAll(s => s.V <= faintest);
                WriteCatalogue(Path.Combine(full, TierName(cut)), tier);
                WriteCatalogue(Path.Combine(tiers, TierName(cut)), tier);
            }

            var cones = new List<double[]>
            {
                new[] { 266.4, -29.0, 3.0 },
                new[] { 10.0, 89.5, 4.0 },      // over the north pole
                new[] { 200.0, -89.9, 2.0 },    // over the south pole
                new[] { 0.5, 10.0, 5.0 },       // straddling 0h
            };
            var rng = new Random(271828);
            for (int i = 0; i < 6; i++)
            {
                double dec = Math.Asin(2.0 * rng.NextDouble() - 1.0) * 180.0 / Math.PI;
                cones.Add(new[] { 360.0 * rng.NextDouble(), dec, 1.0 + 7.0 * rng.NextDouble() });
            }
            CheckTiersOnly(Path.Combine(full, Stem + ".starcat"), tiers, cones, 3000);

            // A deepest tier that does not load leaves the next one as the base.
            string broken = Path.Combine(root, "broken");
            Directory.CreateDirectory(broken);
            File.Copy(Path.Combine(tiers, TierName(13.0)), Path.Combine(broken, TierName(13.0)));
            File.Copy(Path.Combine(tiers, TierName(15.0)), Path.Combine(broken, TierName(15.0)));
            File.WriteAllBytes(Path.Combine(broken, TierName(17.0)), new byte[64]);
            var fallback = new TieredStarCatalog();
            fallback.Load(Path.Combine(broken, Stem + ".starcat"));
            Check("a deepest tier that does not load is refused and the next one is the base",
                  fallback.BaseName == TierName(15.0) && fallback.DepthVMag == 15.0
                      && string.Join(",", fallback.TierCuts) == "13"
                      && fallback.Report.Count == 1 && fallback.Report[0].StartsWith(TierName(17.0) + ":"),
                  $"base {fallback.BaseName}, tiers at V {string.Join(", ", fallback.TierCuts)}, refused: {string.Join(" ", fallback.Report)}");
            fallback.Dispose();

            string empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            var none = new TieredStarCatalog();
            string thrown = "nothing";
            try
            {
                none.Load(Path.Combine(empty, Stem + ".starcat"));
            }
            catch (Exception e)
            {
                thrown = e.GetType().Name;
            }
            Check("with neither a main file nor a tier the load throws FileNotFoundException",
                  thrown == nameof(FileNotFoundException) && !none.IsLoaded, "threw " + thrown);

            CheckMovedMainLink(root, tiers);
            CheckStrayTiers(root, tiers);
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (Exception e) { Console.WriteLine("        could not remove " + root + ": " + e.Message); }
        }
        Console.WriteLine();
    }

    // A main file linking to a catalogue that has moved: File.Exists still says it is there.
    static void CheckMovedMainLink(string root, string tiers)
    {
        string directory = Path.Combine(root, "moved-main");
        Directory.CreateDirectory(directory);
        string link = Path.Combine(directory, Stem + ".starcat");
        if (!TryLink(link, Path.Combine(root, "gone", "GaiaAllSky.starcat"))) return;
        try
        {
            foreach (double cut in ReleaseCuts)
                File.Copy(Path.Combine(tiers, TierName(cut)), Path.Combine(directory, TierName(cut)));
            var linked = new TieredStarCatalog();
            string error = LoadError(linked, link);
            StarFieldPlan deep = error == null ? linked.Plan(266.4, -29.0, 3.0, 22.0, long.MaxValue) : null;
            Check("a main file linking to a moved file counts as missing, and the tiers stand in",
                  error == null && linked.BaseName == TierName(19.0) && string.Join(",", linked.TierCuts) == "13,15,17"
                      && linked.Report.Count == 1 && linked.Report[0].StartsWith(Stem + ".starcat:")
                      && deep.LimitVMag == 19.0 && deep.LimitedByCatalogue,
                  error ?? $"File.Exists says {File.Exists(link)}; base {linked.BaseName}, tiers at V "
                         + $"{string.Join(", ", linked.TierCuts)}, V 22 served to V {deep.LimitVMag}; {string.Join(" ", linked.Report)}");
            linked.Dispose();

            foreach (double cut in ReleaseCuts) File.Delete(Path.Combine(directory, TierName(cut)));
            var alone = new TieredStarCatalog();
            error = LoadError(alone, link);
            Check("with that link and no tier the load throws FileNotFoundException, and Report says why",
                  error != null && error.StartsWith(nameof(FileNotFoundException) + ":")
                      && alone.Report.Count == 1 && alone.Report[0].StartsWith(Stem + ".starcat:"),
                  $"{error ?? "loaded"}; report: {string.Join(" ", alone.Report)}");
        }
        finally
        {
            File.Delete(link);
        }
    }

    // Without a main file nothing vouches for the deepest tier, so the base is the one the others agree with.
    static void CheckStrayTiers(string root, string tiers)
    {
        // A deeper file holding only the V 13 stars.
        string deep = StrayDirectory(root, "stray-deep", tiers, ReleaseCuts, 21.0);
        var withDeep = new TieredStarCatalog();
        string error = LoadError(withDeep, Path.Combine(deep, Stem + ".starcat"));
        Check("a stray deeper tier the others disagree with is refused, and the V 19 tier stays the base",
              error == null && withDeep.BaseName == TierName(19.0) && withDeep.DepthVMag == 19.0
                  && string.Join(",", withDeep.TierCuts) == "13,15,17"
                  && withDeep.Report.Count == 1 && withDeep.Report[0].StartsWith(TierName(21.0) + ":"),
              error ?? Describe(withDeep));
        withDeep.Dispose();

        // The V 17 tier replaced by the V 13 stars.
        string shallow = StrayDirectory(root, "stray-shallow", tiers, new[] { 13.0, 15.0, 19.0 }, 17.0);
        var withShallow = new TieredStarCatalog();
        error = LoadError(withShallow, Path.Combine(shallow, Stem + ".starcat"));
        Check("a shallower tier that disagrees with the base is refused, and the V 19 tier stays the base",
              error == null && withShallow.BaseName == TierName(19.0) && string.Join(",", withShallow.TierCuts) == "13,15"
                  && withShallow.Report.Count == 1 && withShallow.Report[0].StartsWith(TierName(17.0) + ":"),
              error ?? Describe(withShallow));
        withShallow.Dispose();

        // Two files that disagree, with no third to side with either.
        string pair = StrayDirectory(root, "stray-pair", tiers, new[] { 19.0 }, 21.0);
        var withPair = new TieredStarCatalog();
        error = LoadError(withPair, Path.Combine(pair, Stem + ".starcat"));
        Check("between two tiers that disagree and nothing else, the deeper is the base",
              error == null && withPair.BaseName == TierName(21.0) && withPair.TierCuts.Count == 0
                  && withPair.Report.Count == 1 && withPair.Report[0].StartsWith(TierName(19.0) + ":"),
              error ?? Describe(withPair));
        withPair.Dispose();
    }

    // Copies of the good tiers at cuts, plus the V 13 tier under the name of strayCut.
    static string StrayDirectory(string root, string name, string tiers, double[] cuts, double strayCut)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        foreach (double cut in cuts)
            File.Copy(Path.Combine(tiers, TierName(cut)), Path.Combine(directory, TierName(cut)));
        File.Copy(Path.Combine(tiers, TierName(13.0)), Path.Combine(directory, TierName(strayCut)));
        return directory;
    }

    static string LoadError(TieredStarCatalog catalog, string path)
    {
        try
        {
            catalog.Load(path);
            return null;
        }
        catch (Exception e)
        {
            return e.GetType().Name + ": " + e.Message;
        }
    }

    static string Describe(TieredStarCatalog catalog) =>
        $"base {catalog.BaseName}, "
        + (catalog.TierCuts.Count > 0 ? $"tiers at V {string.Join(", ", catalog.TierCuts)}" : "no tiers kept")
        + $", refused: {string.Join(" ", catalog.Report)}";

    static void TestInstalledTiersOnly(string installed)
    {
        Console.WriteLine("Tiers without the main file: links to the installed tiers");
        List<string> targets = InstalledTiers(installed);
        if (targets == null)
        {
            Console.WriteLine();
            return;
        }

        string root = TempDirectory();
        try
        {
            for (int i = 0; i < ReleaseCuts.Length; i++)
            {
                if (!TryLink(Path.Combine(root, TierName(ReleaseCuts[i])), targets[i])) return;
            }

            var cones = new List<double[]>
            {
                new[] { 266.4, -29.0, 0.3 },    // Galactic centre, the densest band
                new[] { 10.0, 89.95, 0.3 },
                new[] { 200.0, -89.9, 0.3 },
                new[] { 0.02, 10.0, 0.3 },
                new[] { 359.98, -40.0, 0.3 },
            };
            var rng = new Random(271828);
            for (int i = 0; i < 6; i++)
            {
                double dec = Math.Asin(2.0 * rng.NextDouble() - 1.0) * 180.0 / Math.PI;
                cones.Add(new[] { 360.0 * rng.NextDouble(), dec, 0.1 + 0.3 * rng.NextDouble() });
            }
            CheckTiersOnly(installed, root, cones, FrameBudget);
        }
        finally
        {
            RemoveLinks(root);
            Console.WriteLine();
        }
    }

    // A main file from elsewhere beside the release tiers refuses every tier holding more stars than it,
    // which is why the download scripts remove it. Here the G < 13 build setup_data.py used to install.
    static void TestForeignMain(string installed)
    {
        Console.WriteLine("A foreign main file beside the release tiers");
        string g13 = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(installed)), Stem + "-G13.starcat");
        if (!File.Exists(g13))
        {
            Console.WriteLine($"  SKIP  no {Path.GetFileName(g13)} beside the catalogue");
            Console.WriteLine();
            return;
        }
        List<string> targets = InstalledTiers(installed);
        if (targets == null)
        {
            Console.WriteLine();
            return;
        }

        string root = TempDirectory();
        try
        {
            if (!TryLink(Path.Combine(root, Stem + ".starcat"), g13)) return;
            for (int i = 0; i < ReleaseCuts.Length; i++)
            {
                if (!TryLink(Path.Combine(root, TierName(ReleaseCuts[i])), targets[i])) return;
            }

            var catalog = new TieredStarCatalog();
            catalog.Load(Path.Combine(root, Stem + ".starcat"));
            foreach (string refused in catalog.Report) Console.WriteLine("        refused: " + refused);
            bool deepRefused = true;
            foreach (double cut in new[] { 15.0, 17.0, 19.0 })
            {
                if (catalog.TierCuts.Contains(cut) || !catalog.Report.Exists(r => r.StartsWith(TierName(cut) + ":")))
                    deepRefused = false;
            }
            Check("a G 13 main file refuses the V 15, 17 and 19 tiers", deepRefused && !catalog.IsDepthLimited,
                  $"{catalog.Count:N0} stars, tiers kept at V {string.Join(", ", catalog.TierCuts)}");
            catalog.Dispose();
        }
        finally
        {
            RemoveLinks(root);
            Console.WriteLine();
        }
    }

    // The same checks at either scale. tierDirectory holds GaiaStarCatalog.V13 to V19 and no main file;
    // mainPath is the all-sky file they were cut from.
    static void CheckTiersOnly(string mainPath, string tierDirectory, List<double[]> cones, long budget)
    {
        string baseName = TierName(19.0);
        string mainName = Path.GetFileName(mainPath);
        var tiersOnly = new TieredStarCatalog();
        tiersOnly.Load(Path.Combine(tierDirectory, Stem + ".starcat"));
        foreach (string refused in tiersOnly.Report) Console.WriteLine("        refused: " + refused);
        var withMain = new TieredStarCatalog();
        withMain.Load(mainPath);
        var main = new RenderedStarCatalog();
        main.Load(mainPath);
        var deepest = new RenderedStarCatalog();
        deepest.Load(Path.Combine(tierDirectory, baseName));

        List<double> cuts = tiersOnly.TierCuts;
        Check("with no main file the V 19 tier is the base and the shallower tiers are kept",
              tiersOnly.IsLoaded && tiersOnly.BaseName == baseName && tiersOnly.Count == deepest.Count
                  && tiersOnly.Report.Count == 0 && string.Join(",", cuts) == "13,15,17",
              $"base {tiersOnly.BaseName}, {tiersOnly.Count:N0} stars, tiers at V {string.Join(", ", cuts)}, "
              + $"{tiersOnly.Report.Count} refused");
        Check("it declares depth V 19, and the main file declares none",
              tiersOnly.IsDepthLimited && tiersOnly.DepthVMag == 19.0
                  && !withMain.IsDepthLimited && double.IsPositiveInfinity(withMain.DepthVMag) && withMain.BaseName == mainName,
              $"V {tiersOnly.DepthVMag} without it, V {withMain.DepthVMag} with {withMain.BaseName}");

        int served16 = 0, served22 = 0, unchanged22 = 0;
        long stars16 = 0, stars19 = 0, stars22 = 0;
        string wrong16 = null, wrong22 = null, wrongMain = null;
        foreach (double[] cone in cones)
        {
            string where = $"RA {cone[0]:F2} Dec {cone[1]:F2} r {cone[2]:F2}";

            var at16 = new List<RenderedStar>();
            StarFieldPlan p16 = tiersOnly.Search(cone[0], cone[1], cone[2], 16.0, long.MaxValue, at16.Add);
            List<RenderedStar> main16 = Cone(main, cone, 16.0);
            stars16 += main16.Count;
            if (p16.Source == TierName(17.0) && p16.LimitVMag == 16.0 && !p16.LimitedByCatalogue && !p16.LimitedByBudget
                && SameStars(main16, at16))
                served16++;
            else if (wrong16 == null)
                wrong16 = $"{where}: {p16.Source} to V {p16.LimitVMag}, {at16.Count} stars where the main file has {main16.Count}";

            var at22 = new List<RenderedStar>();
            StarFieldPlan p22 = tiersOnly.Search(cone[0], cone[1], cone[2], 22.0, long.MaxValue, at22.Add);
            List<RenderedStar> main19 = Cone(main, cone, 19.0);
            stars19 += main19.Count;
            if (p22.Source == baseName && p22.LimitVMag == 19.0 && p22.RequestedVMag == 22.0 && p22.LimitedByCatalogue
                && !p22.LimitedByBudget && SameStars(main19, at22))
                served22++;
            else if (wrong22 == null)
                wrong22 = $"{where}: {p22.Source} to V {p22.LimitVMag}, {at22.Count} stars where the main file has {main19.Count} to V 19";

            var withMain22 = new List<RenderedStar>();
            StarFieldPlan m22 = withMain.Search(cone[0], cone[1], cone[2], 22.0, long.MaxValue, withMain22.Add);
            List<RenderedStar> main22 = Cone(main, cone, 22.0);
            stars22 += main22.Count;
            if (m22.Source == mainName && m22.LimitVMag == 22.0 && !m22.LimitedByCatalogue && !m22.LimitedByBudget
                && SameStars(main22, withMain22))
                unchanged22++;
            else if (wrongMain == null)
                wrongMain = $"{where}: {m22.Source} to V {m22.LimitVMag}, {withMain22.Count} stars where the main file has {main22.Count}";
        }
        Check("V 16 is served by the V 17 tier with exactly the main file's stars", served16 == cones.Count,
              wrong16 ?? $"{cones.Count} cones, {stars16:N0} stars identical");
        Check("V 22 is served to V 19 by the base, flagged as the catalogue's limit, with the main file's stars to V 19",
              served22 == cones.Count, wrong22 ?? $"{cones.Count} cones, {stars19:N0} stars identical");
        Check("with the main file V 22 is unchanged: the main file serves it, unflagged", unchanged22 == cones.Count,
              wrongMain ?? $"{cones.Count} cones, {stars22:N0} stars identical");

        // Under the budget each field steps down exactly as it does with the main file; only the main
        // file's own place goes to the base.
        int plans = 0, stepped = 0;
        string wrongPlan = null;
        foreach (var (ra, dec) in new[] { (266.4, -29.0), (202.47, 47.2) })
        foreach (double radius in new[] { 0.3, 3.2, 17.6 })
        foreach (double limit in new[] { 13.0, 16.0, 17.5, 19.0, 22.0 })
        {
            StarFieldPlan m = withMain.Plan(ra, dec, radius, limit, budget);
            StarFieldPlan t = tiersOnly.Plan(ra, dec, radius, limit, budget);
            bool mainServes = m.Source == mainName;
            string source = mainServes ? baseName : m.Source;
            double depth = mainServes ? 19.0 : m.LimitVMag;
            bool right = t.Source == source && t.LimitVMag == depth
                         && t.LimitedByBudget == (depth < Math.Min(limit, 19.0))
                         && t.LimitedByCatalogue == (limit > 19.0) && !m.LimitedByCatalogue
                         && (t.Candidates <= budget || t.OverBudget);
            plans++;
            if (t.LimitedByBudget) stepped++;
            if (!right && wrongPlan == null)
                wrongPlan = $"RA {ra} Dec {dec} r {radius} V {limit}: with the main file {m.Source} to V {m.LimitVMag}, "
                          + $"without it {t.Source} to V {t.LimitVMag}{(t.LimitedByBudget ? ", budget" : "")}{(t.LimitedByCatalogue ? ", catalogue" : "")}";
        }
        StarFieldPlan brite = tiersOnly.Plan(266.4, -29.0, 17.6, 22.0, budget);
        Check("under the budget a field steps down to the same tier as with the main file",
              wrongPlan == null && stepped > 0,
              wrongPlan ?? $"{plans} plans, {stepped} stepped down; BRITE at the Galactic centre to V 22: {brite.Source} "
                         + $"to V {brite.LimitVMag}, {brite.Candidates:N0} records");

        deepest.Dispose();
        main.Dispose();
        withMain.Dispose();
        tiersOnly.Dispose();
    }

    static List<RenderedStar> Cone(RenderedStarCatalog catalog, double[] cone, double limit)
    {
        var found = new List<RenderedStar>();
        catalog.Search(cone[0], cone[1], cone[2], limit, found);
        return found;
    }

    // The installed tiers beside the catalogue, in ReleaseCuts order, or null after a SKIP line.
    static List<string> InstalledTiers(string installed)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(installed));
        string stem = Path.GetFileNameWithoutExtension(installed);
        var targets = new List<string>();
        foreach (double cut in ReleaseCuts)
        {
            string target = Path.Combine(directory, stem + ".V" + cut.ToString(CultureInfo.InvariantCulture) + ".starcat");
            if (!File.Exists(target))
            {
                Console.WriteLine($"  SKIP  no {Path.GetFileName(target)} beside the catalogue");
                return null;
            }
            targets.Add(target);
        }
        return targets;
    }

    // Stars spread evenly over the sphere, mostly faint, with some exactly at each release cut, in file order.
    static List<Record> SyntheticSky(int count, int seed)
    {
        var rng = new Random(seed);
        var sky = new List<Record>(count);
        for (int i = 0; i < count; i++)
        {
            double dec = Math.Asin(2.0 * rng.NextDouble() - 1.0) * 180.0 / Math.PI;
            double v = i % 97 == 0 ? ReleaseCuts[i / 97 % ReleaseCuts.Length] : 4.0 + 18.0 * Math.Pow(rng.NextDouble(), 0.35);
            long decFixed = (long)Math.Round(dec * 4294967296.0 / 180.0);
            var star = new Record
            {
                Ra = (uint)(rng.NextDouble() * 4294967296.0),
                Dec = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, decFixed)),
                V = RenderedStarCatalog.ToMagMilli(v),
                Bv = i % 7 == 0 ? (short)-32768 : (short)rng.Next(-300, 2000),
                Ebv = i % 5 == 0 ? (ushort)65535 : (ushort)rng.Next(0, 1500),
            };
            double decoded = star.Dec * (180.0 / 4294967296.0);
            star.Band = Math.Min(SyntheticBands - 1, Math.Max(0, (int)((decoded + 90.0) / SyntheticBandWidth)));
            sky.Add(star);
        }
        sky.Sort((a, b) => a.Band != b.Band ? a.Band.CompareTo(b.Band) : a.Ra.CompareTo(b.Ra));
        return sky;
    }

    // Format v3, laid out the way tools/pack_gaia_catalog.py writes it.
    static void WriteCatalogue(string path, List<Record> stars)
    {
        var starts = new uint[SyntheticBands + 1];
        foreach (Record s in stars) starts[s.Band + 1]++;
        for (int b = 0; b < SyntheticBands; b++) starts[b + 1] += starts[b];
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("EXOSTAR1"));
            writer.Write(3);
            writer.Write(stars.Count);
            writer.Write(SyntheticBands);
            writer.Write(SyntheticBandWidth);
            foreach (uint start in starts) writer.Write(start);
            foreach (Record s in stars)
            {
                writer.Write(s.Ra);
                writer.Write(s.Dec);
                writer.Write(s.V);
                writer.Write(s.Bv);
                writer.Write(s.Ebv);
            }
        }
    }

    static string TempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "starcat-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    // Links, so the loader sees the real files without copying gigabytes. False after a SKIP line.
    static bool TryLink(string link, string target)
    {
        try
        {
#if NET6_0_OR_GREATER
            File.CreateSymbolicLink(link, target);
            return true;
#else
            if (Path.DirectorySeparatorChar == '\\')
            {
                Console.WriteLine("  SKIP  this runtime makes no links on Windows");
                return false;
            }
            if (symlink(target, link) == 0) return true;
            Console.WriteLine($"  SKIP  could not link {link} (errno {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return false;
#endif
        }
        catch (Exception e)
        {
            Console.WriteLine($"  SKIP  could not link {link} ({e.Message})");
            return false;
        }
    }

#if !NET6_0_OR_GREATER
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    static extern int symlink(string target, string link);
#endif

    // Deletes the links themselves, never what they point at.
    static void RemoveLinks(string root)
    {
        try
        {
            foreach (string link in Directory.GetFiles(root)) File.Delete(link);
            Directory.Delete(root, false);
        }
        catch (Exception e)
        {
            Console.WriteLine("        could not remove " + root + ": " + e.Message);
        }
    }

    // ------------------------------------------------------------------ 4. What a frame costs

    static void TestCost(string path)
    {
        Console.WriteLine($"What a frame's star field costs, budget {FrameBudget:N0} records");
        var tiered = new TieredStarCatalog();
        var load = Stopwatch.StartNew();
        tiered.Load(path);
        load.Stop();
        Console.WriteLine($"        load with {tiered.TierCuts.Count} tiers checked: {load.Elapsed.TotalMilliseconds:F0} ms");

        var fields = new[] { ("Galactic centre", 266.4, -29.0), ("M51", 202.47, 47.2) };
        var cones = new[] { ("RC20", 0.3), ("RedCat 51", 3.2), ("BRITE", 17.6) };
        bool withinBudget = true;
        foreach (var (field, ra, dec) in fields)
        foreach (var (instrument, radius) in cones)
        foreach (double limit in new[] { 13.0, 17.0, 19.0, 22.0 })
        {
            long found = 0;
            var watch = Stopwatch.StartNew();
            StarFieldPlan plan = tiered.Search(ra, dec, radius, limit, FrameBudget, s => found++);
            watch.Stop();
            if (plan.Candidates > FrameBudget && !plan.OverBudget) withinBudget = false;
            if (plan.OverBudget) withinBudget = false;
            Console.WriteLine($"        {field,-16}{instrument,-10} V<{limit,4:F1}  {plan.Source,-30} to V {plan.LimitVMag,4:F1}"
                            + $"{(plan.LimitedByBudget ? " (budget)" : "         ")} {plan.Candidates,12:N0} read {found,12:N0} kept {watch.Elapsed.TotalMilliseconds,8:F0} ms");
        }
        Check("no field reads past the budget once tiers are installed", withinBudget || tiered.TierCuts.Count == 0, "");

        // Streamed deposit, the frame's real per-star work: photometry through a response, two projections
        // and the splat. The heaviest cases the budget admits.
        foreach (var (label, radius, limit, fovDeg, areaCm2, exposure, cutoff, rotationDeg) in new[] {
            ("BRITE in orbit, 1 s, Galactic centre", 17.6, 18.0, 29.53, 7.07, 1.0, 0.77, 0.0),
            ("RedCat 51 guided, 300 s, Galactic centre", 3.2, 22.0, 4.40, 20.4, 300.0, 1.5, 0.0),
            ("RedCat 51 unguided, 60 s, Galactic centre", 3.7, 21.0, 4.40, 20.4, 60.0, 1.5, 1.0),
        })
        {
            var response = new SystemResponse(552.5e-9, 2650.0, 1.0, null, 0.90, 1.0, 650.0);
            var reddening = new ReddenedResponseCache(response);
            const int width = 1002, height = 668;
            var plane = new float[width * height];
            // Observer at the field's declination with the field on the meridian: the boresight is the zenith.
            var bore = SkyVector.FromHorizontal(90.0, 0.0);
            var right = SkyVector.FromHorizontal(0.0, 90.0);
            var up = SkyVector.Normalized(bore.Y * right.Z - bore.Z * right.Y,
                                          bore.Z * right.X - bore.X * right.Z,
                                          bore.X * right.Y - bore.Y * right.X);
            var projection = new GnomonicProjection(bore, up, right, fovDeg, width, height);
            Func<RenderedStar, double> electronsFor = s => StellarPhotometry.CollectedElectrons(
                s.VMag, s.ColorIndexBV, s.ReddeningEBv, response, reddening, areaCm2, exposure, 1.0);

            // The frame's own budget, trail included, the way SolarSystemCameraTexture sets it from the drift of the
            // field centre across the sensor.
            double trailPx = 0.0;
            if (rotationDeg > 0.0)
            {
                var from = SkyCoordinates.EquatorialToHorizontal(266.4, -29.0, 266.4 - 0.5 * rotationDeg, -29.0);
                var to = SkyCoordinates.EquatorialToHorizontal(266.4, -29.0, 266.4 + 0.5 * rotationDeg, -29.0);
                projection.TryProject(SkyVector.FromHorizontal(from.AltitudeDeg, from.AzimuthDeg), out double fx, out double fy);
                projection.TryProject(SkyVector.FromHorizontal(to.AltitudeDeg, to.AzimuthDeg), out double tx, out double ty);
                trailPx = Math.Sqrt((tx - fx) * (tx - fx) + (ty - fy) * (ty - fy));
            }
            long budget = (long)(FrameBudget / StarFieldRenderer.RelativeDepositCost(trailPx));

            long visited = 0, landed = 0;
            var watch = Stopwatch.StartNew();
            StarFieldPlan plan = tiered.Search(266.4, -29.0, radius, limit, budget, s =>
            {
                visited++;
                if (StarFieldRenderer.DepositStar(plane, width, height, s, projection, 266.4 - 0.5 * rotationDeg,
                                                  266.4 + 0.5 * rotationDeg, -29.0, cutoff, electronsFor))
                    landed++;
            });
            watch.Stop();
            double seconds = watch.Elapsed.TotalSeconds;
            Console.WriteLine($"        {label}: {trailPx:F0} px trails, budget {budget:N0}, {plan.Source} to V {plan.LimitVMag:F1}, "
                            + $"{visited:N0} stars streamed, {landed:N0} deposited, {seconds:F1} s "
                            + $"({visited / Math.Max(1e-9, seconds) / 1e6:F2} M stars/s)");
            Check($"{label} streams in under 60 s", seconds < 60.0, $"{seconds:F1} s");
        }

        tiered.Dispose();
        Console.WriteLine();
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ExoInstruments.Core;

/// <summary>
/// Times the two loads the Space Center waits on when it first opens, and records everything they
/// produce, so a faster version can be shown to produce the same bytes.
///
/// Measured in KSP on 2026-09-13: LoadEmissionPatches took 60 s and the BSC5 merge 13 s.
/// </summary>
static class LoadProfile
{
    static int Main(string[] args)
    {
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library/Application Support/Steam/steamapps/common/Kerbal Space Program",
            "GameData/ExoInstruments/PluginData");
        string only = "all", dump = null, compare = null;
        int repeat = 1;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--data") data = args[++i];
            else if (a == "--only") only = args[++i];
            else if (a == "--dump") dump = args[++i];
            else if (a == "--compare") compare = args[++i];
            else if (a == "--repeat") repeat = Math.Max(1, int.Parse(args[++i], CultureInfo.InvariantCulture));
            else if (a == "--workers") ParallelWork.UseWorkers(int.Parse(args[++i], CultureInfo.InvariantCulture));
            else { Console.Error.WriteLine("unknown argument: " + a); return 2; }
        }

        Console.WriteLine("runtime " + RuntimeName() + ", " + Environment.ProcessorCount + " cores, "
                          + ParallelWork.MaxWorkers + " workers, repeat " + repeat);
        if (dump != null) Directory.CreateDirectory(dump);

        bool same = true;
        if (only == "all" || only == "patches") same &= Patches(data, repeat, dump, compare);
        if (only == "all" || only == "merge") same &= Merge(data, repeat, dump, compare);

        long peak = Process.GetCurrentProcess().PeakWorkingSet64;
        if (peak > 0) Console.WriteLine("\npeak working set " + (peak >> 20) + " MB");
        if (compare != null)
            Console.WriteLine(same ? "\nRESULT: identical to " + compare : "\nRESULT: DIFFERS from " + compare);
        return same ? 0 : 1;
    }

    // ------------------------------------------------------------ patches

    static bool Patches(string data, int repeat, string dump, string compare)
    {
        string mapPath = Path.Combine(data, "HalphaMap.emission");
        string setPath = Path.Combine(data, "HalphaPatches.patchset");
        var t = new Timings();
        byte[] rejected = null, calibrated = null;
        bool stable = true;
        int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);

        for (int iter = 0; iter < repeat; iter++)
        {
            // The same calls, in the same order, as ExoInstrumentsGUI.LoadEmissionMap and LoadEmissionPatches.
            t.Start();
            var map = new EmissionMap();
            map.Load(mapPath);
            t.Lap("composite Load");
            var set = new EmissionPatchSet();
            set.Load(setPath);
            t.Lap("patch Load");
            set.RejectOutliers();
            t.Lap("RejectOutliers");
            byte[] r = SerializePatches(set);
            t.Start();
            set.CalibrateAgainst(map);
            t.Lap("CalibrateAgainst");
            byte[] c = SerializePatches(set);

            if (iter == 0) { rejected = r; calibrated = c; }
            else if (!Same(r, rejected) || !Same(c, calibrated))
            {
                Console.WriteLine("  run " + (iter + 1) + " produced different bytes from run 1");
                stable = false;
            }
        }

        t.Print("H-alpha patches");
        Console.WriteLine("  GC collections: gen0 " + (GC.CollectionCount(0) - gc0) + ", gen2 " + (GC.CollectionCount(2) - gc2));
        Console.WriteLine("  after RejectOutliers   sha256 " + Sha(rejected));
        Console.WriteLine("  after CalibrateAgainst sha256 " + Sha(calibrated));

        if (dump != null)
        {
            File.WriteAllBytes(Path.Combine(dump, "patches-rejected.bin"), rejected);
            File.WriteAllBytes(Path.Combine(dump, "patches-calibrated.bin"), calibrated);
        }
        bool same = stable;
        if (compare != null)
        {
            same &= ComparePatches("after RejectOutliers", File.ReadAllBytes(Path.Combine(compare, "patches-rejected.bin")), rejected);
            same &= ComparePatches("after CalibrateAgainst", File.ReadAllBytes(Path.Combine(compare, "patches-calibrated.bin")), calibrated);
        }
        return same;
    }

    static byte[] SerializePatches(EmissionPatchSet set)
    {
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(set.PatchCount);
            w.Write(set.RejectedCells);
            w.Write(set.CalibratedCells);
            foreach (EmissionPatchSet.Patch p in set.Patches)
            {
                w.Write(p.Name ?? "");
                w.Write(p.Nside);
                WritePlane(w, p.Values);
                int planes = p.ExtraValues != null ? p.ExtraValues.Length : 0;
                w.Write(planes);
                for (int q = 0; q < planes; q++) WritePlane(w, p.ExtraValues[q]);
            }
            w.Flush();
            return ms.ToArray();
        }
    }

    static void WritePlane(BinaryWriter w, ushort[] values)
    {
        int n = values != null ? values.Length : -1;
        w.Write(n);
        if (n <= 0) return;
        var bytes = new byte[n * 2];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        w.Write(bytes);
    }

    static ushort[] ReadPlane(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0) return null;
        var values = new ushort[n];
        byte[] bytes = r.ReadBytes(n * 2);
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    static bool ComparePatches(string label, byte[] golden, byte[] actual)
    {
        if (Same(golden, actual)) { Console.WriteLine("  " + label + ": identical"); return true; }
        Console.WriteLine("  " + label + ": DIFFERENT");
        using (var a = new BinaryReader(new MemoryStream(golden)))
        using (var b = new BinaryReader(new MemoryStream(actual)))
        {
            int na = a.ReadInt32(), nb = b.ReadInt32();
            int ra = a.ReadInt32(), rb = b.ReadInt32();
            int ca = a.ReadInt32(), cb = b.ReadInt32();
            Console.WriteLine($"    patches {na} vs {nb}, rejected cells {ra} vs {rb}, calibrated cells {ca} vs {cb}");
            if (na != nb) return false;
            for (int i = 0; i < na; i++)
            {
                string name = a.ReadString();
                b.ReadString();
                a.ReadInt32();
                b.ReadInt32();
                ReportPlane(name + " H-alpha", ReadPlane(a), ReadPlane(b));
                int pa = a.ReadInt32(), pb = b.ReadInt32();
                if (pa != pb) { Console.WriteLine($"    {name}: {pa} vs {pb} extra planes"); return false; }
                for (int q = 0; q < pa; q++) ReportPlane(name + " plane " + q, ReadPlane(a), ReadPlane(b));
            }
        }
        return false;
    }

    static void ReportPlane(string label, ushort[] a, ushort[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
        {
            if (a != null || b != null) Console.WriteLine("    " + label + ": shape differs");
            return;
        }
        int diff = 0, first = -1;
        double worst = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            if (first < 0) first = i;
            diff++;
            double da = Float16.ToDouble(a[i]), db = Float16.ToDouble(b[i]);
            double rel = Math.Abs(db - da) / Math.Max(Math.Abs(da), 1e-30);
            if (!(rel <= worst)) worst = rel;
        }
        if (diff > 0)
            Console.WriteLine($"    {label}: {diff} of {a.Length} cells differ, first at {first}, worst relative change {worst:G3}");
    }

    // ------------------------------------------------------------ catalogue merge

    static bool Merge(string data, int repeat, string dump, string compare)
    {
        var t = new Timings();
        string text = null;
        bool stable = true;
        int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);

        for (int iter = 0; iter < repeat; iter++)
        {
            // ExoInstrumentsGUI.LoadCatalog: LoadExoplanetCatalog, Thin, then MergeWithBackgroundStars.
            t.Start();
            string csv = File.ReadAllText(Path.Combine(data, "ExoplanetCatalog.csv"));
            t.Lap("read ExoplanetCatalog.csv");
            CsvLoadResult planets = ExoplanetCsvLoader.LoadFromCsv(csv);
            t.Lap("LoadFromCsv");
            List<StarTarget> thinned = CatalogDensityThinner.Thin(planets.Targets);
            t.Lap("CatalogDensityThinner.Thin");
            string tsv = File.ReadAllText(Path.Combine(data, "BrightStarCatalog.tsv"));
            t.Lap("read BrightStarCatalog.tsv");
            BackgroundStarLoadResult bsc = BackgroundStarCatalogLoader.LoadFromTsv(tsv);
            t.Lap("LoadFromTsv");
            CatalogMergeResult merge = StarCatalogMerger.Merge(thinned, bsc.Entries);
            t.Lap("StarCatalogMerger.Merge");

            string s = DescribeMerge(planets, thinned, bsc, merge);
            if (iter == 0)
            {
                text = s;
                Console.WriteLine($"\n  {planets.Loaded} planet rows, {thinned.Count} after thinning, {bsc.Loaded} BSC stars, "
                                  + $"{merge.Merged.Count} targets");
            }
            else if (s != text)
            {
                Console.WriteLine("  run " + (iter + 1) + " produced a different merge from run 1");
                stable = false;
            }
        }

        t.Print("BSC5 merge (the Space Center log timed the last three rows)");
        Console.WriteLine("  GC collections: gen0 " + (GC.CollectionCount(0) - gc0) + ", gen2 " + (GC.CollectionCount(2) - gc2));
        Console.WriteLine("  merge sha256 " + Sha(Encoding.UTF8.GetBytes(text)));

        if (dump != null) File.WriteAllText(Path.Combine(dump, "merge.txt"), text);
        bool same = stable;
        if (compare != null)
        {
            string golden = File.ReadAllText(Path.Combine(compare, "merge.txt"));
            if (golden == text) Console.WriteLine("  merge: identical");
            else
            {
                same = false;
                string[] a = golden.Split('\n'), b = text.Split('\n');
                int diffs = 0;
                for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
                {
                    string was = i < a.Length ? a[i] : "<missing>", now = i < b.Length ? b[i] : "<missing>";
                    if (was == now) continue;
                    if (diffs++ < 8) Console.WriteLine($"    line {i + 1}\n      was {Clip(was)}\n      now {Clip(now)}");
                }
                Console.WriteLine("  merge: DIFFERENT, " + diffs + " line(s)");
            }
        }
        return same;
    }

    // Every observable output: counters, logs, list order, object identity and every property value.
    static string DescribeMerge(CsvLoadResult planets, List<StarTarget> thinned,
                                BackgroundStarLoadResult bsc, CatalogMergeResult merge)
    {
        var sb = new StringBuilder();
        sb.AppendLine("csv " + Properties(planets));
        sb.AppendLine("thinned " + thinned.Count);
        sb.AppendLine("bsc " + Properties(bsc));
        sb.AppendLine("merge " + Properties(merge));
        AppendList(sb, "MatchLog", merge.MatchLog);
        AppendList(sb, "AmbiguousNameKeys", merge.AmbiguousNameKeys);
        AppendList(sb, "UnmatchedBrightHosts", merge.UnmatchedBrightHosts);

        var source = new Dictionary<object, string>(ReferenceComparer.Instance);
        for (int i = 0; i < thinned.Count; i++)
            if (!source.ContainsKey(thinned[i])) source[thinned[i]] = "exo#" + i;
        for (int k = 0; k < bsc.Entries.Count; k++)
            if (!source.ContainsKey(bsc.Entries[k].Target)) source[bsc.Entries[k].Target] = "bsc#" + k;

        sb.AppendLine("merged " + merge.Merged.Count);
        for (int i = 0; i < merge.Merged.Count; i++)
        {
            StarTarget m = merge.Merged[i];
            string from;
            if (!source.TryGetValue(m, out from)) from = "new";
            sb.Append(i).Append(' ').Append(from).Append(' ').AppendLine(Properties(m));
        }

        sb.AppendLine("bsc entries " + bsc.Entries.Count);
        for (int k = 0; k < bsc.Entries.Count; k++)
        {
            BackgroundStarEntry e = bsc.Entries[k];
            sb.Append(k).Append(" hr=").Append(e.HrNumber)
              .Append(" hd=").Append(Value(e.HdNumber))
              .Append(" keys=").Append(Value(e.NameKeys))
              .Append(" teff=").Append(Value(e.DerivedTeffK))
              .Append(' ').AppendLine(Properties(e.Target));
        }
        return sb.ToString();
    }

    static void AppendList(StringBuilder sb, string name, List<string> list)
    {
        sb.AppendLine(name + " " + list.Count);
        foreach (string line in list) sb.AppendLine("  " + Value(line));
    }

    // Public instance properties sorted by name; lists are printed element by element.
    static string Properties(object o)
    {
        var parts = new List<string>();
        foreach (PropertyInfo p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                    .OrderBy(q => q.Name, StringComparer.Ordinal))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
            object v;
            try { v = p.GetValue(o, null); }
            catch (TargetInvocationException e) { parts.Add(p.Name + "=!" + e.InnerException.GetType().Name); continue; }
            if (o is StarTarget || !(v is IList)) parts.Add(p.Name + "=" + Value(v));
            else parts.Add(p.Name + "=count " + ((IList)v).Count);
        }
        return string.Join(" ", parts.ToArray());
    }

    // Doubles as their bits, so the text is the same whichever runtime formats it.
    static string Value(object v)
    {
        if (v == null) return "null";
        if (v is double) return BitConverter.DoubleToInt64Bits((double)v).ToString("X16", CultureInfo.InvariantCulture);
        if (v is float) return BitConverter.DoubleToInt64Bits((float)v).ToString("X16", CultureInfo.InvariantCulture);
        var s = v as string;
        if (s != null) return "\"" + s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
        var f = v as IFormattable;
        if (f != null) return f.ToString(null, CultureInfo.InvariantCulture);
        var seq = v as IEnumerable;
        if (seq != null)
        {
            var items = new List<string>();
            foreach (object x in seq) items.Add(Value(x));
            return "[" + string.Join(",", items.ToArray()) + "]";
        }
        return v.ToString();
    }

    // ------------------------------------------------------------ helpers

    static string RuntimeName()
    {
        string arch = RuntimeInformation.ProcessArchitecture.ToString();
        Type mono = Type.GetType("Mono.Runtime");
        if (mono == null) return ".NET " + Environment.Version + " " + arch;
        MethodInfo display = mono.GetMethod("GetDisplayName", BindingFlags.NonPublic | BindingFlags.Static);
        return "Mono " + (display != null ? (string)display.Invoke(null, null) : "?") + " " + arch;
    }

    static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static string Sha(byte[] bytes)
    {
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant().Substring(0, 16);
    }

    static string Clip(string s) => s.Length > 400 ? s.Substring(0, 400) + "..." : s;

    sealed class Timings
    {
        readonly List<string> order = new List<string>();
        readonly Dictionary<string, List<double>> ms = new Dictionary<string, List<double>>();
        readonly Stopwatch sw = new Stopwatch();

        public void Start() { sw.Restart(); }

        public void Lap(string phase)
        {
            double elapsed = sw.Elapsed.TotalMilliseconds;
            List<double> list;
            if (!ms.TryGetValue(phase, out list)) { ms[phase] = list = new List<double>(); order.Add(phase); }
            list.Add(elapsed);
            sw.Restart();
        }

        // First run is what KSP pays (cold JIT); best shows steady state when repeated.
        public void Print(string title)
        {
            Console.WriteLine("\n" + title);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-30} {1,10} {2,10}", "phase", "first ms", "best ms"));
            double firstTotal = 0.0, bestTotal = 0.0;
            foreach (string phase in order)
            {
                List<double> list = ms[phase];
                firstTotal += list[0];
                bestTotal += list.Min();
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-30} {1,10:F0} {2,10:F0}", phase, list[0], list.Min()));
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-30} {1,10:F0} {2,10:F0}", "total", firstTotal, bestTotal));
        }
    }

    sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();
        public new bool Equals(object a, object b) => ReferenceEquals(a, b);
        public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
    }
}

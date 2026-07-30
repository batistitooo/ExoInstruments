using System;
using System.Diagnostics;
using System.IO;
using ExoInstruments.Core;

/// <summary>
/// Runs the shipped patch repair over an installed patch set and counts what is left, so "no
/// artefacts" is a number rather than an impression.
///
/// An artefact is defined without reference to its cause, because chasing causes one at a time did
/// not converge: a cell whose value departs from the median of its own eight neighbours by more
/// than a factor two either way is inconsistent with the sky around it at a scale the survey
/// resolves, and diffuse emission is by definition not that.
/// </summary>
static class DumpPatch
{
    static void Main(string[] args)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library/Application Support/Steam/steamapps/common/Kerbal Space Program",
            "GameData/ExoInstruments/PluginData");
        var composite = new EmissionMap();
        composite.Load(Path.Combine(dir, "HalphaMap.emission"));
        var set = new EmissionPatchSet();
        set.Load(Path.Combine(dir, "HalphaPatches.patchset"));
        Console.WriteLine($"{set.PatchCount} patches at nside {set.Nside} ({set.ResolutionArcmin:F2} arcmin)");
        Console.WriteLine($"composite at nside {composite.Nside} ({composite.ResolutionArcmin:F2} arcmin)\n");

        Console.WriteLine($"{"patch",-24} {"cells",8} {"outliers before",16} {"after",8}");
        int beforeAll = 0;
        foreach (var patch in set.Patches)
            beforeAll += CountOutliers(set, patch, out int _);

        var sw = Stopwatch.StartNew();
        set.RejectOutliers();
        long rejectMs = sw.ElapsedMilliseconds;
        set.CalibrateAgainst(composite);
        sw.Stop();

        int afterAll = 0;
        foreach (var patch in set.Patches)
        {
            int after = CountOutliers(set, patch, out int cells);
            afterAll += after;
            Console.WriteLine($"{patch.Name,-24} {cells,8} {"",16} {after,8}");
        }

        Console.WriteLine($"\noutlier cells: {beforeAll} before, {afterAll} after");
        Console.WriteLine($"cells rejected: {set.RejectedCells}, gain-matched: {set.CalibratedCells}");
        Console.WriteLine($"cost at load: {rejectMs} ms to reject, "
                        + $"{sw.ElapsedMilliseconds - rejectMs} ms to calibrate");

        // The contract, verified: at the composite's own beam the patch must reproduce it.
        double worst = 0.0;
        int checkedCells = 0;
        foreach (var patch in set.Patches)
        {
            foreach (var group in set.ComposeGroups(patch, composite.Nside))
            {
                double target = composite.RawCellValue(group.Key);
                if (!(target > 0.0) || group.Value.Count == 0) continue;
                double sum = 0.0;
                foreach (double v in group.Value) sum += v;
                double dev = Math.Abs(sum / group.Value.Count / target - 1.0);
                if (dev > worst) worst = dev;
                checkedCells++;
            }
        }
        Console.WriteLine($"contract: {checkedCells} composite cells checked, worst departure {worst:E2}");
    }

    /// <summary>Cells departing from the median of their own neighbours by more than a factor two either way.</summary>
    static int CountOutliers(EmissionPatchSet set, EmissionPatchSet.Patch patch, out int cells)
    {
        cells = patch.CellCount;
        return set.CountNeighbourOutliers(patch, 5.0);
    }
}

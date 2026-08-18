using System;
using System.Diagnostics;
using System.Collections.Generic;
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
        // EXO_SKIP_CALIBRATE measures what the packer's own regression achieves on its own. The
        // contract check below compares group means against the very cell values CalibrateAgainst
        // forces them onto, so with the step enabled it verifies what it just imposed and cannot
        // say whether the step was needed.
        bool skipCalibrate = Environment.GetEnvironmentVariable("EXO_SKIP_CALIBRATE") == "1";
        if (!skipCalibrate) set.CalibrateAgainst(composite);
        else Console.WriteLine("(CalibrateAgainst skipped)");
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

        // THE CONTRACT, AT THE SCALE THE COMPOSITE ACTUALLY RESOLVES. This used to compare each
        // group's mean against the composite's RAW cell value, which is the very quantity the old
        // per-cell gain forced them onto: it verified what had just been imposed and could not say
        // whether imposing it was right. It was not. Finkbeiner's northern sky is WHAM at a one
        // degree beam, so its 3.44' cells there are interpolation, and matching a 26" patch to them
        // cell by cell flattened the patch's contrast onto the reference's.
        //
        // The honest statement is distributional and at the beam: over cells the composite can
        // constrain, the patch must agree with it in the median, and the agreement must not depend
        // on how bright the patch is. A gain that rises on faint sky and falls on bright is
        // contrast being erased, whatever the mean says.
        var deviations = new List<double>();
        var faint = new List<double>();
        var bright = new List<double>();
        foreach (var patch in set.Patches)
        {
            foreach (var group in set.ComposeGroups(patch, composite.Nside))
            {
                double target = composite.RawCellValue(group.Key);
                if (!(target > 0.0) || group.Value.Count == 0) continue;
                double sum = 0.0;
                foreach (double v in group.Value) sum += v;
                double mean = sum / group.Value.Count;
                deviations.Add(Math.Abs(mean / target - 1.0));
                if (mean < 5.0) faint.Add(mean / target);
                else if (mean > 60.0) bright.Add(mean / target);
            }
        }
        deviations.Sort();
        faint.Sort();
        bright.Sort();
        double Median(List<double> v) => v.Count == 0 ? double.NaN : v[v.Count / 2];
        Console.WriteLine($"contract at the composite's beam over {deviations.Count} cells: "
                        + $"median departure {Median(deviations) * 100:F1}%, "
                        + $"90th percentile {(deviations.Count > 0 ? deviations[(int)(0.9 * (deviations.Count - 1))] * 100 : 0):F1}%");
        Console.WriteLine($"contrast preserved: patch/composite is {Median(faint):F2} on sky under 5 R "
                        + $"and {Median(bright):F2} over 60 R "
                        + $"(ratio {Median(faint) / Median(bright):F1}; 1.0 means the patch kept its own contrast)");
    }

    /// <summary>Cells departing from the median of their own neighbours by more than a factor two either way.</summary>
    static int CountOutliers(EmissionPatchSet set, EmissionPatchSet.Patch patch, out int cells)
    {
        cells = patch.CellCount;
        return set.CountNeighbourOutliers(patch, 5.0);
    }
}

using System;
using System.Threading.Tasks;

namespace ExoInstruments.Core
{
    /// <summary>
    /// How this pipeline is allowed to use the machine's other cores, in one place.
    ///
    /// WHY A POLICY RATHER THAN A Parallel.For AT EACH SITE. A capture is not the only thing
    /// running: KSP's own main thread is rendering the game behind the photograph, and the frame
    /// pipeline already lives on a background Task so the game keeps its frame rate while an
    /// exposure integrates. Taking every core would trade a faster photograph for a stuttering
    /// game, which is the wrong trade for something the player watches. One worker is therefore
    /// left for the game, and every parallel stage in the pipeline asks here rather than deciding
    /// for itself.
    ///
    /// WHAT PARALLELISM IS AND IS NOT ALLOWED TO CHANGE. Splitting a loop across cores must not
    /// change the frame. That rules out accumulating a shared total from several threads, because
    /// floating-point addition is not associative and the answer would then depend on the order
    /// the operating system happened to schedule them in: the same seed would no longer give the
    /// same frame, which is the property the whole stochastic pipeline is built on (see
    /// Pcg32.MixSeed). Every parallel stage in this codebase therefore either writes to
    /// per-element storage that no other worker touches, or accumulates per ROW and sums the rows
    /// afterwards in row order, which is a fixed order independent of the thread count. Where a
    /// stage draws random numbers, it is left serial unless its stream can be partitioned
    /// deterministically.
    ///
    /// Pure C# with no Unity dependency, like the rest of Core.
    /// </summary>
    public static class ParallelWork
    {
        /// <summary>
        /// Workers a pipeline stage may use: every core but one, so the game keeps a thread of its
        /// own. One on a single-core machine, where the parallel path then costs nothing beyond
        /// the check that skips it.
        /// </summary>
        public static int MaxWorkers { get; private set; } = Math.Max(1, Environment.ProcessorCount - 1);

        /// <summary>The options every Parallel.For in the pipeline runs under.</summary>
        public static ParallelOptions Options { get; private set; } =
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };

        /// <summary>
        /// Pins the worker count.
        ///
        /// FOR A HARNESS, not for the mod, and it exists because the guarantee above is one that
        /// has to be CHECKED rather than asserted: a stage claiming its result does not depend on
        /// how the work was divided can be run at one worker and at many and the two frames
        /// compared bit for bit. tools/capture-profile does exactly that. It is also what lets the
        /// same harness report what the machine's other cores are actually buying.
        /// </summary>
        public static void UseWorkers(int workers)
        {
            MaxWorkers = Math.Max(1, workers);
            Options = new ParallelOptions { MaxDegreeOfParallelism = MaxWorkers };
        }

        /// <summary>
        /// Whether a loop of this many elementary operations is worth splitting.
        ///
        /// Starting workers and joining them costs tens of microseconds, so a short loop comes
        /// out slower in parallel than serially. The threshold is deliberately generous: every
        /// stage that asks is a frame-sized loop when it matters at all, and one that falls under
        /// it was never the reason a capture was slow.
        /// </summary>
        public static bool Worthwhile(long operations) => MaxWorkers > 1 && operations >= 200000L;
    }
}

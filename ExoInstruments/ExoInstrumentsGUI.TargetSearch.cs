using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;
using ExoInstruments.Core;
using ExoInstruments.Visualization;

namespace ExoInstruments
{
    /// <summary>
    /// The target search panel: the right-hand half of the target-selection view, alongside the sky
    /// chart on the left.
    ///
    /// WHY IT REPLACED THE FILTER BAR. The old control above the chart was a name filter over the
    /// star catalogue and nothing else. It could not find a galaxy, a nebula, a cluster or a planet,
    /// because none of those is a star; it matched by raw substring, so "NGC 24" lit up NGC 247 and
    /// two hundred others; and it produced no list, only a scatter of highlighted dots to hunt for
    /// on the chart. Everything the mod can point at now goes through one index (Core/TargetSearch*)
    /// and comes back as a ranked, clickable list.
    ///
    /// WHAT THE PANEL OWNS AND WHAT IT DOES NOT. All of the searching is in Core and is pure C#,
    /// tested headless by tools/search-tests. This file is the KSP layer: it builds the index from
    /// what is installed (including the solar-system bodies, the one source that needs the game),
    /// runs it off the main thread, draws the list, and turns a click into a pointing.
    /// </summary>
    public partial class ExoInstrumentsGUI
    {
        private TargetSearchIndex targetIndex;
        private Task<TargetSearchIndex> targetIndexTask;
        // Set when something happened that changes what the index should contain: a career scan revealing a
        // star, or a catalogue arriving late.
        private bool targetIndexStale = true;

        private string targetSearchText = "";
        // Every match, which is what the chart highlights.
        private List<SearchResult> targetSearchMatches = new List<SearchResult>();
        // The first MaxSearchResultsShown of them, which is what the list draws.
        private List<SearchResult> targetSearchResults = new List<SearchResult>();
        private TargetQuery targetSearchQuery = TargetQuery.Parse("");
        private int targetSearchTotal;
        private Vector2 scrollPosSearch;
        // Why the last click on a result did not point the telescope, or null.
        private string searchSelectionError;

        // The query text changed and the results have not caught up yet. The re-run is deferred to the next
        // Layout event rather than done where the change is noticed. IMGUI builds its layout once per frame and
        // replays it for every other event, so changing the NUMBER of rows in the middle of a key or mouse
        // event is the classic way to desynchronise a GUILayout group from the layout it was measured with.
        private bool targetSearchDirty;

        // Rows drawn at once. IMGUI lays out every row it is given whether or not it is inside the scroll view,
        // so this is a real cost, not a cosmetic cap.
        private const int MaxSearchResultsShown = 120;

        // The one-click type filters, chosen as the categories an observer picks an instrument for.
        private static readonly string[] QuickFilters = { "planet", "moon", "star", "galaxy", "nebula", "cluster" };

        // What the chart should light up: the search results, by identity for catalogue stars and
        // by quantised position for everything else (a chart point carries a copy of the catalogue
        // struct, not the same object, so identity is not available there).
        private readonly HashSet<StarTarget> highlightedStars = new HashSet<StarTarget>();
        private readonly HashSet<long> highlightedSkyPositions = new HashSet<long>();
        private readonly HashSet<CelestialBody> highlightedBodies = new HashSet<CelestialBody>();
        private bool searchHighlightActive;

        // Quantised sky position, a tenth of an arcsecond, used to match a result against a chart marker.
        private static long SkyPositionKey(double raDeg, double decDeg)
        {
            if (double.IsNaN(raDeg) || double.IsNaN(decDeg)) return long.MinValue;
            long ra = (long)Math.Round(raDeg * 36000.0);
            long dec = (long)Math.Round((decDeg + 90.0) * 36000.0);
            return unchecked(ra * 100000003L + dec);
        }

        // --- building the index -------------------------------------------------

        /// <summary>
        /// Starts the index build on a background Task, same treatment and for the same reason as
        /// the sky chart: sixteen thousand targets, each of which needs its designations normalised
        /// and its constellation resolved through a frame change, is a tenth of a second of work on
        /// a desktop and several times that on the game's runtime. On the main thread that is a
        /// visible freeze the first time the observatory window opens.
        ///
        /// Everything the Task touches is either pure C# or a snapshot taken here, on the main
        /// thread: the career fog state, the display names, and the solar-system bodies.
        /// </summary>
        void MaybeStartTargetIndexBuild()
        {
            if (!targetIndexStale || targetIndexTask != null || catalog == null) return;
            targetIndexStale = false;

            var catalogSnapshot = catalog;
            GalaxyCatalog galaxies = SolarSystemCameraTexture.GalaxyCatalog;
            List<SearchTarget> bodies = BuildBodySearchTargets();

            // Career fog, snapshotted rather than queried from the Task: IsIdentityHidden reads
            // KSP's game state and the scenario module, and neither is ours to touch off-thread.
            bool fog = CareerFogActive;
            bool scenarioMissing = fog && ExoInstrumentsScenario.Instance == null;
            var scanned = new HashSet<string>();
            if (fog && !scenarioMissing)
            {
                ExoInstrumentsScenario scenario = ExoInstrumentsScenario.Instance;
                foreach (StarTarget star in catalogSnapshot)
                    if (star.CatalogKey != null && scenario.IsScanned(star.CatalogKey))
                        scanned.Add(star.CatalogKey);
            }

            Func<StarTarget, bool> hidden = star =>
                fog && (scenarioMissing || star.CatalogKey == null || !scanned.Contains(star.CatalogKey));
            Func<StarTarget, string> displayName = star => hidden(star)
                ? (star.RaDeg.HasValue && star.DecDeg.HasValue
                    ? "Unscanned " + StarNames.ProvisionalDesignation(star.RaDeg.Value, star.DecDeg.Value)
                    : "Unscanned target")
                : star.Name;

            targetIndexTask = Task.Run(() =>
            {
                var index = new TargetSearchIndex();
                foreach (SearchTarget body in bodies) index.Add(body);
                TargetCatalogue.AddStars(index, catalogSnapshot, displayName, hidden);
                TargetCatalogue.AddDeepSky(index);
                TargetCatalogue.AddGalaxies(index, galaxies);
                TargetCatalogue.AddCrossIdentifiedObjects(index);
                TargetCatalogue.AddStarProperNames(index);
                return index;
            });
        }

        /// <summary>Applies a finished index build. Main thread only, like every other Poll* here.</summary>
        void PollTargetIndexTask()
        {
            if (targetIndexTask == null || !targetIndexTask.IsCompleted) return;
            if (targetIndexTask.IsFaulted)
            {
                Debug.LogError("[ExoInstruments] Target index build failed: " + targetIndexTask.Exception);
                targetIndexTask = null;
                return;
            }
            targetIndex = targetIndexTask.Result;
            targetIndexTask = null;
            RunTargetSearch();
        }

        /// <summary>
        /// The bodies of whatever planet pack is installed, read on the main thread because
        /// CelestialBody is KSP's.
        ///
        /// A body has no fixed right ascension: it moves through the constellations, which is
        /// what a planet IS, so it carries no position here and is filtered out of any
        /// constellation search rather than being given a position that would be wrong within the
        /// hour.
        /// </summary>
        List<SearchTarget> BuildBodySearchTargets()
        {
            var targets = new List<SearchTarget>();
            if (FlightGlobals.Bodies == null) return targets;

            CelestialBody home = FlightGlobals.GetHomeBody();
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == null) continue;

                TargetKind kind = body.isStar ? TargetKind.SolarSystemStar
                    : body.referenceBody != null && body.referenceBody.isStar ? TargetKind.SolarSystemPlanet
                    : TargetKind.SolarSystemMoon;

                var aliases = new List<string> { body.bodyName };
                string localised = body.displayName;
                if (!string.IsNullOrEmpty(localised))
                {
                    // KSP suffixes localised body names with "^N" for grammatical agreement.
                    int caret = localised.IndexOf('^');
                    if (caret > 0) localised = localised.Substring(0, caret);
                    if (localised != body.bodyName) aliases.Add(localised);
                }

                targets.Add(new SearchTarget
                {
                    DisplayName = body.bodyName,
                    Designation = body.bodyName,
                    Kind = kind,
                    TypeLabel = DescribeBody(body, home, kind),
                    Provenance = "installed planet pack",
                    Payload = body,
                    Aliases = aliases.ToArray(),
                });
            }
            return targets;
        }

        static string DescribeBody(CelestialBody body, CelestialBody home, TargetKind kind)
        {
            string what = kind == TargetKind.SolarSystemStar ? "star of this system"
                : kind == TargetKind.SolarSystemPlanet ? "planet"
                : "moon of " + (body.referenceBody != null ? body.referenceBody.bodyName : "unknown");
            if (body == home) what += ", where the observatory stands";
            return what + string.Format(CultureInfo.InvariantCulture, ", radius {0:N0} km", body.Radius / 1000.0);
        }

        // --- running a search ---------------------------------------------------

        /// <summary>
        /// Re-runs the current query. Called when the text changes, when a filter button is
        /// pressed, and when the index finishes building; not every frame, because a query is a scan of
        /// every target, and IMGUI would run it several times per keystroke.
        /// </summary>
        void RunTargetSearch(bool rerenderChart = true)
        {
            targetSearchQuery = TargetQuery.Parse(targetSearchText);
            if (targetIndex == null)
            {
                targetSearchResults.Clear();
                targetSearchTotal = 0;
                return;
            }

            RefreshResultAltitudes();
            searchSelectionError = null;
            targetSearchMatches = targetIndex.QueryAll(targetSearchQuery);
            targetSearchTotal = targetSearchMatches.Count;
            targetSearchResults = targetSearchMatches.Count <= MaxSearchResultsShown
                ? targetSearchMatches
                : targetSearchMatches.GetRange(0, MaxSearchResultsShown);
            RefreshSearchHighlights(rerenderChart);
        }

        /// <summary>
        /// Brings every target's current altitude up to date, so "alt:&gt;30" means what it says
        /// and the list can show what is actually up.
        ///
        /// One transform per target over the whole index, which is the same work the sky chart
        /// already does each refresh; it runs here rather than there because a filter has to see
        /// every target, not only the ones above the horizon that the chart plots.
        /// </summary>
        void RefreshResultAltitudes()
        {
            if (targetIndex == null) return;

            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return;

            // From orbit there is no horizon; the analogue of "degrees up" is degrees of
            // clearance off the host body's limb, negative while occulted. Same column, same
            // alt: filter, the honest replacement of the same physical question.
            if (ObservingPlatform.IsSpaceBased)
            {
                if (!TryBuildChartObserver(out ChartObserverSnapshot snap) || snap.HostOccluderIndex < 0) return;
                SkyOccluder host = snap.Occluders[snap.HostOccluderIndex];

                foreach (SearchTarget entry in targetIndex.Entries)
                {
                    var orbBody = entry.Payload as CelestialBody;
                    if (orbBody != null)
                    {
                        if (orbBody == snap.Host) { entry.AltitudeDeg = double.NaN; continue; }
                        Vector3d toBody = orbBody.position - snap.ObserverPos;
                        if (toBody.magnitude < 1.0) { entry.AltitudeDeg = double.NaN; continue; }
                        SkyVector bodyDir = snap.Frame.WorldToEquatorialVector(toBody);
                        entry.AltitudeDeg = OrbitalVisibility.SeparationDeg(bodyDir, host.Direction)
                                          - host.AngularRadiusDeg;
                        continue;
                    }
                    if (double.IsNaN(entry.RaDeg) || double.IsNaN(entry.DecDeg))
                    {
                        entry.AltitudeDeg = double.NaN;
                        continue;
                    }
                    SkyVector dir = SkyChartProjection.DirectionFromEquatorial(entry.RaDeg, entry.DecDeg);
                    entry.AltitudeDeg = OrbitalVisibility.SeparationDeg(dir, host.Direction)
                                      - host.AngularRadiusDeg;
                }
                return;
            }

            double meridianRaDeg = SkyCoordinates.ComputeLocalMeridianRaDeg(
                Planetarium.GetUniversalTime(), home.rotationPeriod, home.initialRotation,
                ObservatorySite.LongitudeDeg);

            foreach (SearchTarget entry in targetIndex.Entries)
            {
                var body = entry.Payload as CelestialBody;
                if (body != null)
                {
                    entry.AltitudeDeg = TryComputeBodyAltAz(body, out double alt, out _) ? alt : double.NaN;
                    continue;
                }
                if (double.IsNaN(entry.RaDeg) || double.IsNaN(entry.DecDeg))
                {
                    entry.AltitudeDeg = double.NaN;
                    continue;
                }
                entry.AltitudeDeg = SkyCoordinates.EquatorialToHorizontal(
                    entry.RaDeg, entry.DecDeg, meridianRaDeg, ObservatorySite.LatitudeDeg).AltitudeDeg;
            }
        }

        /// <summary>
        /// Rebuilds the sets the sky chart lights up, and re-renders it, so the chart and the list
        /// are two views of one search rather than two unrelated controls.
        /// </summary>
        void RefreshSearchHighlights(bool rerenderChart)
        {
            highlightedStars.Clear();
            highlightedSkyPositions.Clear();
            highlightedBodies.Clear();
            searchHighlightActive = !targetSearchQuery.IsEmpty;

            // Every match, not the shown page: see TargetSearchIndex.QueryAll.
            foreach (SearchResult result in targetSearchMatches)
            {
                var star = result.Target.Payload as StarTarget;
                if (star != null) highlightedStars.Add(star);
                var body = result.Target.Payload as CelestialBody;
                if (body != null) highlightedBodies.Add(body);
                long key = SkyPositionKey(result.Target.RaDeg, result.Target.DecDeg);
                if (key != long.MinValue) highlightedSkyPositions.Add(key);
            }

            // Nothing to re-raster into if the panel is closed; the next refresh after it opens
            // rebuilds the chart anyway.
            if (rerenderChart && windowVisible) RefreshSkyChartHighlights();
        }

        /// <summary>
        /// Whether a chart point should be drawn as a search match. With no search running, everything is a
        /// match, which is what keeps every star clickable.
        /// </summary>
        bool IsSearchHighlighted(SkyChartPoint point)
        {
            if (!searchHighlightActive) return true;
            if (point.Target != null) return highlightedStars.Contains(point.Target);
            if (point.IsDeepSky)
                return highlightedSkyPositions.Contains(SkyPositionKey(point.DeepSky.RaDeg, point.DeepSky.DecDeg));
            return false;
        }

        /// <summary>Same question for a solar-system body, which the chart carries separately from its points.</summary>
        bool IsSearchHighlightedBody(CelestialBody body)
            => !searchHighlightActive || highlightedBodies.Contains(body);

        // --- drawing ------------------------------------------------------------

        /// <summary>
        /// The panel itself: a search box, one-click type filters, and the results.
        ///
        /// Shown in the right column while a target is being chosen, opposite the chart, so that
        /// looking something up and seeing where it is in the sky are the same glance.
        /// </summary>
        void DrawTargetSearchPanel()
        {
            // The one point in the frame where the result list is allowed to change length.
            if (targetSearchDirty && Event.current.type == EventType.Layout)
            {
                targetSearchDirty = false;
                RunTargetSearch();
            }

            GUILayout.Label("Find a target", sectionHeaderStyle);

            if (targetIndex == null)
            {
                GUILayout.Label(targetIndexTask != null
                    ? "Building the target index..."
                    : "The target index is not available yet.", smallCaptionStyle);
                return;
            }

            GUILayout.Label($"{targetIndex.Count} targets: the planets and moons of this system, the star "
                          + "catalogue, the nebulae, the galaxies, and every Messier and named NGC/IC object.",
                          smallCaptionStyle);

            GUILayout.BeginHorizontal();
            string typed = GUILayout.TextField(targetSearchText, GUILayout.Height(28));
            if (typed != targetSearchText)
            {
                targetSearchText = typed;
                targetSearchDirty = true;
            }
            if (GUILayout.Button("Clear", GUILayout.Width(70), GUILayout.Height(28))
                && targetSearchText.Length > 0)
            {
                targetSearchText = "";
                targetSearchDirty = true;
            }
            GUILayout.EndHorizontal();

            DrawQuickFilters();

            GUILayout.Label("Type a name or a designation: Andromeda, M31, NGC 224, Vega, 51 Peg, Duna. "
                          + "Filters: type:galaxy, in:Ori, mag:<9, alt:>30"
                          + (ObservingPlatform.IsSpaceBased ? " (alt = degrees off the host body's limb here)." : "."),
                          smallCaptionStyle);

            foreach (string bad in targetSearchQuery.Unrecognised)
                GUILayout.Label($"\"{bad}\" is not a filter this search understands, so it was ignored. "
                              + "Type: is one of " + string.Join(", ", TargetKinds.FilterWords)
                              + "; in: takes a constellation; mag: and alt: take a number.", smallCaptionStyle);

            if (searchSelectionError != null) GUILayout.Label(searchSelectionError, smallCaptionStyle);

            GUILayout.Space(4);
            GUILayout.Label(DescribeResultCount());

            scrollPosSearch = GUILayout.BeginScrollView(scrollPosSearch, GUILayout.Height(SearchResultsHeight));
            foreach (SearchResult result in targetSearchResults) DrawSearchResultRow(result.Target);
            if (targetSearchResults.Count == 0)
                GUILayout.Label("Nothing matches. Check the spelling, or widen the filters.", smallCaptionStyle);
            GUILayout.EndScrollView();
        }

        // As much of the column as the panel's own controls leave, so a taller screen shows more results rather
        // than more empty panel.
        private float SearchResultsHeight => Mathf.Max(240f, ColumnContentHeight - 260f);

        string DescribeResultCount()
        {
            if (targetSearchQuery.IsEmpty)
                return $"All {targetSearchTotal} targets, brightest first"
                     + (targetSearchTotal > targetSearchResults.Count
                        ? $" (showing {targetSearchResults.Count}; type to narrow)" : "");
            if (targetSearchTotal == 0) return "No match";
            return targetSearchTotal <= targetSearchResults.Count
                ? $"{targetSearchTotal} match" + (targetSearchTotal == 1 ? "" : "es")
                : $"{targetSearchTotal} matches, best {targetSearchResults.Count} shown";
        }

        void DrawQuickFilters()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Show only", GUILayout.Width(70));
            foreach (string filter in QuickFilters)
            {
                bool active = IsQuickFilterActive(filter);
                if (GUILayout.Toggle(active, " " + filter, GUILayout.Width(78)) != active)
                    ToggleQuickFilter(filter);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Whether the box already carries this filter, compared as a WHOLE token: "type:planet" is
        /// a substring of "type:planetarynebula", and a substring test would light the planet button
        /// up for a search about planetary nebulae.
        /// </summary>
        bool IsQuickFilterActive(string filter)
        {
            foreach (string token in targetSearchText.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(token, "type:" + filter, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// A filter button writes into the search box rather than keeping state of its own, so
        /// there is exactly one place a query lives and the buttons and the typed text can never
        /// disagree about what is being searched.
        /// </summary>
        void ToggleQuickFilter(string filter)
        {
            string token = "type:" + filter;
            var kept = new List<string>();
            bool removed = false;
            foreach (string word in targetSearchText.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(word, token, StringComparison.OrdinalIgnoreCase)) { removed = true; continue; }
                kept.Add(word);
            }
            if (!removed) kept.Add(token);
            targetSearchText = string.Join(" ", kept.ToArray());
            targetSearchDirty = true;
        }

        void DrawSearchResultRow(SearchTarget target)
        {
            bool isCurrent = IsCurrentTarget(target);

            // The whole row is the button. A separate "point here" control would put a click target
            // inside a list whose every entry is already a thing to point at, and the row is where
            // the eye already is.
            // "> " rather than an arrow glyph: Unity's rich text understands <b>, <i>, <size> and
            // <color> and nothing else, and the stock font is not guaranteed to carry an arrow.
            // The rest of this GUI marks the current selection the same way.
            string label = (isCurrent ? "<b>> " : "<b>") + target.DisplayName + "</b>"
                         + "\n<size=10>" + DescribeTarget(target) + "</size>";
            if (GUILayout.Button(label, searchResultStyle))
            {
                searchSelectionError = null;
                SelectSearchResult(target);
            }
        }

        /// <summary>
        /// The one line under each result: what it is, how bright, how big, where, and out of which
        /// catalogue. The provenance is not decoration: two rows in this list can come from
        /// sources measured to entirely different standards, and the observer is entitled to know
        /// which is which before spending a night on one.
        /// </summary>
        string DescribeTarget(SearchTarget target)
        {
            var parts = new List<string> { target.TypeLabel };

            string altitude = DescribeAltitude(target);
            if (altitude.Length > 0) parts.Add(altitude);

            if (!double.IsNaN(target.Magnitude))
                parts.Add(string.Format(CultureInfo.InvariantCulture, "mag {0:F1}", target.Magnitude));
            if (!double.IsNaN(target.MajorArcmin) && target.MajorArcmin > 0.0)
                parts.Add(DescribeExtent(target.MajorArcmin));
            if (target.Constellation != null)
                parts.Add("in " + (Constellations.NameOf(target.Constellation) ?? target.Constellation));
            if (!double.IsNaN(target.RaDeg) && !double.IsNaN(target.DecDeg))
                parts.Add(SexagesimalCoordinates.Format(target.RaDeg, target.DecDeg));
            parts.Add(target.Provenance);

            return string.Join(" | ", parts.ToArray());
        }

        static string DescribeExtent(double majorArcmin)
        {
            if (majorArcmin >= 60.0)
                return string.Format(CultureInfo.InvariantCulture, "{0:F1} deg across", majorArcmin / 60.0);
            if (majorArcmin >= 1.0)
                return string.Format(CultureInfo.InvariantCulture, "{0:F0}' across", majorArcmin);
            return string.Format(CultureInfo.InvariantCulture, "{0:F0}\" across", majorArcmin * 60.0);
        }

        string DescribeAltitude(SearchTarget target)
        {
            if (double.IsNaN(target.AltitudeDeg)) return "";
            if (ObservingPlatform.IsSpaceBased)
            {
                return target.AltitudeDeg < 0.0
                    ? string.Format(CultureInfo.InvariantCulture, "occulted, {0:F0} deg inside the limb", -target.AltitudeDeg)
                    : string.Format(CultureInfo.InvariantCulture, "{0:F0} deg off the limb", target.AltitudeDeg);
            }
            return target.AltitudeDeg < 0.0
                ? string.Format(CultureInfo.InvariantCulture, "{0:F0} deg below horizon", -target.AltitudeDeg)
                : string.Format(CultureInfo.InvariantCulture, "{0:F0} deg up", target.AltitudeDeg);
        }

        /// <summary>Whether the telescope is already on this result, matched the same way the sky chart matches its own markers.</summary>
        bool IsCurrentTarget(SearchTarget target)
        {
            var body = target.Payload as CelestialBody;
            if (body != null) return selectedPhotographyTarget.IsBody && selectedPhotographyTarget.Body == body;

            var star = target.Payload as StarTarget;
            if (star != null && selectedStar == star) return true;

            if (double.IsNaN(target.RaDeg) || !selectedPhotographyTarget.IsEquatorial) return false;
            return Math.Abs(selectedPhotographyTarget.RaDeg - target.RaDeg) < 1e-6
                && Math.Abs(selectedPhotographyTarget.DecDeg - target.DecDeg) < 1e-6;
        }

        /// <summary>
        /// Turns a clicked result into a pointing, through the same two paths the sky chart already
        /// uses: a body is tracked live, and everything else is a fixed direction.
        ///
        /// A catalogue star additionally becomes the SELECTED star, because the detection
        /// instruments need a catalogue entry with a period and a mass, not a direction. Anything
        /// else clears that selection, for the same reason: there is no catalogue entry to hand
        /// them.
        /// </summary>
        void SelectSearchResult(SearchTarget target)
        {
            var body = target.Payload as CelestialBody;
            if (body != null)
            {
                SelectPhotographyBody(body);
                return;
            }

            var star = target.Payload as StarTarget;
            if (star != null)
            {
                selectedStar = star;
                SelectPhotographyStar(star);
                return;
            }

            if (double.IsNaN(target.RaDeg) || double.IsNaN(target.DecDeg))
            {
                searchSelectionError = target.DisplayName + " has no position on record, so the "
                                     + "telescope cannot be aimed at it.";
                return;
            }
            SelectPhotographyTarget(
                SkyTarget.FromEquatorial(target.RaDeg, target.DecDeg, target.DisplayName),
                clearStarSelection: true);
        }
    }
}

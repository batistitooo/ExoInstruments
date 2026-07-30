using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using KSP.UI.Screens;
using KSP.UI.Screens.DebugToolbar;
using ExoInstruments.Core;
using ExoInstruments.Session;
using ExoInstruments.Visualization;

namespace ExoInstruments
{
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class ExoInstrumentsGUI : MonoBehaviour
    {
        private ApplicationLauncherButton button;
        private bool windowVisible = false;
        private Vector2 scrollPosLeft;
        private Vector2 scrollPosRight;

        private List<StarTarget> catalog;
        private StarTarget selectedStar;
        private int selectedObservatoryIndex = 0;
        private bool observatoryMenuOpen = false;
        private InstrumentSpec SelectedInstrument => Observatories.All[selectedObservatoryIndex];
        private ObservationSession session;
        private RvObservationSession rvSession;
        private ImagingObservationSession imagingSession;
        private List<TransitDetectionStage> lastTransitStages;
        private TransitTimingVariations.TtvAnalysisResult lastTtvResult;
        private List<RvDetectionStage> lastRvStages;
        private RossiterMcLaughlin.RmFitResult lastRmResult;
        private StarTarget lastRmPlanet;
        private DirectImagingResult lastImagingResult;
        private string searchFilter = "";
        private const int MaxShownInList = 40; // IMGUI chokes rendering thousands of buttons per frame

        // Sibling lookup is an O(catalog) scan; cache it per selected star since
        // DrawObservatorySelector re-runs every IMGUI pass.
        private StarTarget rvSystemCacheKey;
        private List<StarTarget> rvSystemCache;

        private Texture2D rawPlotTexture;
        // One phase-folded plot per transit-search stage, parallel to lastTransitStages.
        private readonly List<Texture2D> transitPhaseFoldedTextures = new List<Texture2D>();
        private readonly List<LightCurvePlotRange> transitPhaseFoldedRanges = new List<LightCurvePlotRange>();
        private Texture2D ttvPlotTexture;
        private RvPlotRange ttvPlotRange;
        private Texture2D rvRawPlotTexture;
        // One phase-folded plot per prewhitening stage, parallel to lastRvStages.
        private readonly List<Texture2D> rvPhaseFoldedTextures = new List<Texture2D>();
        private readonly List<RvPlotRange> rvPhaseFoldedRanges = new List<RvPlotRange>();
        private Texture2D imagingTexture;
        private double imagingFovArcsec;
        // Forward-simulated campaign predictions (deterministic given the observing
        // geometry, but recomputed on the texture-refresh throttle since each one
        // re-runs the night/airmass integrator over upcoming days). NaN = not computed.
        private double imagingDetectionUt = double.NaN;
        private double imagingNextWindowUt = double.NaN;
        // Cap the 5-sigma forward search at ~400 Kerbin days of additional wall
        // clock; beyond that the campaign is not a sane use of flagship time.
        private const double ImagingPredictionMaxWallSeconds = 400.0 * 21600.0;
        private LightCurvePlotRange rawPlotRange;
        private RvPlotRange rvRawPlotRange;
        private const int PlotWidth = 500;
        private const int PlotHeight = 160;
        private const int ImagingTextureSize = 400; // square: it's a sky image, not a time/phase series
        private const float RawPlotRefreshIntervalSeconds = 1f;
        private float nextRawPlotRefreshTime = 0f;
        // The imaging frame is a full per-pixel raster (400x400, several
        // transcendental calls per pixel), expensive enough that computing it
        // synchronously on the main thread stalls the frame that's rendering the
        // game (tens of milliseconds), which shows up as a visible hitch/flash.
        // ComputePixels runs on a background Task instead (see RefreshImagingTexture);
        // this field just tracks that in-flight computation so Update() doesn't
        // start a second one before the first lands.
        private const float ImagingRefreshIntervalSeconds = 1f;
        private float nextImagingRefreshTime = 0f;
        private double lastImagingRefreshUt = double.NaN;
        private Task<ImagingRenderResult> imagingRenderTask;
        // Bumped every time a new imaging session starts (or the session ends).
        // A background render captures the generation it was started under; if a
        // new session begins before the old one's Task finishes, the stale result
        // is discarded instead of overwriting the new session's texture.
        private int imagingRenderGeneration = 0;

        /// <summary>Everything a background imaging refresh produces, applied to fields together once the Task completes.</summary>
        private struct ImagingRenderResult
        {
            public Color[] Pixels;
            public double FovArcsec;
            public double DetectionUt;
            public double NextWindowUt;
            public int Generation;
        }

        private Texture2D skyChartTexture;
        private List<SkyChartPoint> cachedSkyChartPoints = new List<SkyChartPoint>();
        // Solar-system bodies currently plotted on the chart, kept for click
        // hit-testing (the visible dots are baked into the chart texture; this
        // list only carries identity + position so a click can resolve which
        // body was hit). Rebuilt each refresh on the main thread.
        private List<(CelestialBody Body, double AltDeg, double AzDeg)> cachedChartBodies
            = new List<(CelestialBody, double, double)>();
        private const int SkyChartWidth = 640;
        private const int SkyChartHeight = 640;
        // Full refresh re-transforms the whole catalog (thousands of background
        // stars once merged in) AND re-rasters a 640x640 canvas, same
        // background-Task treatment as the imaging frame, for the same reason.
        private const float SkyChartRefreshIntervalSeconds = 1f;
        private float nextSkyChartRefreshTime = 0f;
        private double lastSkyChartRefreshUt = double.NaN;
        private Task<(List<SkyChartPoint> Points, Color[] Pixels)> skyChartRenderTask;

        // Sky chart camera: zoom 1 = whole sky fits the view (old fixed behavior).
        // Pan is in raw (unzoomed) dome-projection pixel space; see SkyChartView.
        private float skyChartZoom = 1f;
        private Vector2 skyChartPan = new Vector2(SkyChartWidth / 2f, SkyChartHeight / 2f);
        private bool skyChartDragging = false;
        private Vector2 skyChartDragStartMouse;
        private Vector2 skyChartDragStartPan;
        private float skyChartDragDistance = 0f;
        private const float SkyChartMinZoom = 1f;
        private const float SkyChartMaxZoom = 15f;
        private const float SkyChartZoomSensitivity = 0.08f;
        private const float SkyChartDragClickThreshold = 5f; // pixels of movement before a click becomes a drag
        private StarTarget hoveredSkyChartStar;
        private DeepSkyObject? hoveredDeepSky;

        // Observing-quality forecast heatmap for the selected (target, instrument)
        // pairing: rows = nights ahead, columns = time of night. Recomputed on a
        // background Task (it re-runs the conditions evaluator over thousands of
        // cells, same treatment as the sky chart) whenever the selection changes
        // or the clock has advanced meaningfully since the last compute.
        private Texture2D forecastTexture;
        private ObservingForecast.ForecastResult forecastResult;
        private Task<(ObservingForecast.ForecastResult Forecast, Color[] Pixels, StarTarget Star, int InstrumentIndex)> forecastRenderTask;
        private StarTarget forecastRenderedStar;
        private int forecastRenderedInstrumentIndex = -1;
        // What the on-screen grid was actually computed for (set when a compute
        // lands, not when it starts), the guard DrawForecastPanel trusts.
        private StarTarget forecastAppliedStar;
        private int forecastAppliedInstrumentIndex = -1;
        private double forecastComputedUt = double.NaN;
        // Set when a heatmap click issues a warp, cleared once it lands,
        // the only on-screen sign a click actually did something, since the
        // heatmap itself only refreshes every ForecastRefreshUtSeconds and
        // gave no feedback that a warp was even running.
        private double forecastWarpTargetUt = double.NaN;

        // Solar-system-body forecast: same heatmap widget/rendering
        // (ForecastTexture/ObservingForecast.ForecastResult reused as-is), but
        // computed synchronously; it's one body's altitude timeline, not a
        // whole catalog, so a background Task would be pure overhead.
        private Texture2D photoForecastTexture;
        private ObservingForecast.ForecastResult photoForecastResult;
        private SkyTarget photoForecastAppliedTarget;
        private double photoForecastComputedUt = double.NaN;
        private double photoForecastWarpTargetUt = double.NaN;

        private const int ForecastNights = 12;
        private const int ForecastColumns = 128;
        private const int ForecastWidth = 640;
        private const int ForecastRowPixels = 14;
        private const int ForecastHeight = ForecastNights * ForecastRowPixels;
        // Recompute once the clock has moved one column-width past the last
        // compute. Tied to the actual cell resolution (nightSeconds / columns,
        // matching ObservingForecast.Compute's cellSeconds with its default
        // fallback body-rotation length) rather than a fraction of a whole
        // night: the previous quarter-night threshold (5400s) was ~32 columns
        // coarser than the grid it gated, so the "now" edge and the highlighted
        // cell sat frozen for up to 1.5 in-game hours, highly visible during
        // any warp faster than a few hundred x; then jumped a third of a
        // night in one frame.
        private const double ForecastRefreshUtSeconds = 21600.0 / ForecastColumns;

        // Fullscreen layout.
        private const float LeftColumnWidth = 720f;
        private const float ColumnGap = 24f;
        private const float HeaderReservedHeight = 110f;
        private float ColumnContentHeight => Mathf.Max(200f, Screen.height - HeaderReservedHeight);

        private GUIStyle headerLabelStyle;
        private GUIStyle fullscreenWindowStyle;
        private GUIStyle plotTitleStyle;
        private GUIStyle axisLabelRightStyle;
        private GUIStyle axisLabelLeftStyle;
        private GUIStyle smallCaptionStyle;
        private GUIStyle wrappedLabelStyle;
        private GUIStyle sectionHeaderStyle;
        private bool stylesInitialized = false;

        // Stand-in for clicking a not-yet-built observatory building: type this
        // in the KSP debug console (Alt+F12 -> Console, or backtick) to open the window.
        private const string ConsoleCommand = "exoinstruments_open";
        private const string InputLockId = "ExoInstrumentsObservatoryLock";

        // R_sun / R_earth — converts a transit depth into an estimated planet radius.
        private const double SolarRadiusToEarthRadii = 109.2;

        private readonly SolarSystemCameraTexture solarSystemCamera = new SolarSystemCameraTexture();
        private readonly AstroImageStack astroStack = new AstroImageStack();
        private int stackBatchSize = 5;
        private int stackBatchRemaining = 0;
        /// <summary>Exposures started in the current batch, and subs collected from it. Tracked separately because the series pipelines: an exposure starts while the previous frame is still being reduced.</summary>
        private int stackBatchQueued;
        private int stackBatchCollected;
        private bool stackAlignSubs = true;
        private bool stackLuckyImaging = false;
        private bool saveDiagnosticFrames = false;
        private ColourCompositeMode compositeMode = ColourCompositeMode.TrueColour;
        private StackExportMode exportMode = StackExportMode.Composite;
        private string compositeReport;
        private Texture2D stackedCompositeTexture;
        private Color[] lastComposedPixels;
        private string stackComposeError;
        private string stackExportReport;
        private string stackBatchInterruptedMessage;
        private SkyTarget selectedPhotographyTarget;

        /// <summary>The target's body, or null when the telescope is pointed at a fixed sky position.</summary>
        private CelestialBody selectedPhotographyBody => selectedPhotographyTarget.Body;

        // Hand-entered coordinates, so the telescope can be pointed at anything with a published
        // position rather than only at what the chart happens to carry a marker for.
        private string manualRaText = "";
        private string manualDecText = "";
        private string manualTargetError;
        // Mirrors session/rvSession/imagingSession's role for photography: set
        // by "Start Observation" once a body is selected AND the RC20 is the
        // active instrument, cleared by the right-column Stop button. Nothing
        // renders in the right column until this is true.
        private bool photographySessionActive;

        // Same constant as StarTarget.EstimatedRvSemiAmplitudeMps, used to invert a
        // measured K back into an implied Mp*sin(i) for the RV scan report.
        private const double RvSemiAmplitudeConstantMps = 28.4329;

        void Awake()
        {
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
        }

        void Start()
        {
            catalog = LoadCatalog();
            LoadRenderedStarCatalog();
            LoadDustMap();
            LoadEmissionMap();
            LoadEmissionPatches();
            LoadGalaxyCatalog();
            DebugScreenConsole.AddConsoleCommand(
                ConsoleCommand,
                args => OpenObservatoryWindow(),
                "ExoInstruments: opens the observatory console (stand-in for clicking the building)");

            // The building itself is a persistent (DontDestroyOnLoad) real KSC
            // facility built by ExoObservatoryFacility, not owned by this
            // per-scene addon; we just subscribe to its click event.
            ExoObservatoryBuilding.Clicked += OnObservatoryBuildingClicked;

            // ExoObservatoryTelescopeTracker.TrackedBody is a static field, so it
            // survives scene reloads; without this, a target selected in an
            // earlier SpaceCentre visit keeps the telescope (and doors) pointed
            // off-rest even though this fresh GUI instance's own
            // selectedPhotographyBody correctly starts null.
            ApplyTelescopeTracking();
        }

        private void OnObservatoryBuildingClicked()
        {
            Debug.Log("[Exoplanets] ExoObservatoryBuilding.Clicked received by ExoInstrumentsGUI, opening window.");
            OpenObservatoryWindow();
        }

        private List<StarTarget> LoadCatalog()
        {
            List<StarTarget> exoplanetTargets = LoadExoplanetCatalog();
            int beforeThinning = exoplanetTargets.Count;
            exoplanetTargets = CatalogDensityThinner.Thin(exoplanetTargets);
            Debug.Log($"[ExoInstruments] Density thinning: {beforeThinning} real hosts -> {exoplanetTargets.Count} " +
                      "(caps dense survey fields like Kepler so they don't visibly clump on the sky chart).");
            return MergeWithBackgroundStars(exoplanetTargets);
        }

        /// <summary>
        /// Loads the Tycho-2 catalogue that gets DRAWN into photographs.
        ///
        /// Entirely separate from the Bright Star Catalogue loaded below, which stays exactly as
        /// it is: the exoplanet instruments deliberately search a small, sparse list so that
        /// finding a transit remains a tractable game. This one exists only so that a photograph
        /// has a real star field behind its subject, and nothing in the detection pipeline ever
        /// reads it. A missing or unreadable file simply means no star field.
        /// </summary>
        private void LoadRenderedStarCatalog()
        {
            // The star catalogue is USER-SUPPLIED and nothing ships. A Tycho-2 file used to,
            // and it was the worst of both worlds: 29.3 MB carried to deliver about four stars
            // per RC20 frame, where a real 30 s sub holds hundreds. Gaia can deliver the real
            // thing but cannot ship either, at 443 MB for G < 14 and 1.9 GB for G < 16 in this
            // format. So the choice is a real star field or an honestly empty one, and building
            // it is one command: see tools/pack_gaia_catalog.py and the README.
            string path = KSPUtil.ApplicationRootPath
                        + "GameData/ExoInstruments/PluginData/GaiaStarCatalog.starcat";
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    Debug.Log("[ExoInstruments] No star catalogue installed, so photographs will "
                            + "have an empty sky behind their subject. To render a real star "
                            + "field, build one with tools/pack_gaia_catalog.py and place it at "
                            + path + "; see the README for depths and their memory cost.");
                    return;
                }
                var catalog = new RenderedStarCatalog();
                catalog.Load(path);
                SolarSystemCameraTexture.StarCatalog = catalog;
                Debug.Log($"[ExoInstruments] Rendered star field: {catalog.Count} Gaia DR3 stars loaded.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ExoInstruments] Failed to load the rendered star catalog: {e.Message}");
            }
        }

        /// <summary>
        /// Optional all-sky reddening map. Like the star catalogue nothing ships, and with no file
        /// installed every query returns NaN and the header simply omits the keywords.
        /// </summary>
        private void LoadDustMap()
        {
            string path = KSPUtil.ApplicationRootPath
                        + "GameData/ExoInstruments/PluginData/DustMap.dustmap";
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    Debug.Log("[ExoInstruments] No dust map installed, so frames carry no sight-line "
                            + "extinction. Build one with tools/pack_dust_map.py and place it at " + path);
                    return;
                }
                var map = new DustMap();
                map.Load(path);
                SolarSystemCameraTexture.DustMap = map;
                Debug.Log($"[ExoInstruments] Dust map: nside {map.Nside} "
                        + $"({map.ResolutionArcmin:F1} arcmin), {map.Source}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ExoInstruments] Failed to load the dust map: {e.Message}");
            }
        }

        /// <summary>Optional all-sky emission-line map. Same treatment as the dust map: nothing ships, absence is silent.</summary>
        private void LoadEmissionMap()
        {
            string path = KSPUtil.ApplicationRootPath
                        + "GameData/ExoInstruments/PluginData/HalphaMap.emission";
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    Debug.Log("[ExoInstruments] No emission map installed, so narrowband frames show "
                            + "no diffuse gas. Build one with tools/pack_halpha_map.py and place it at " + path);
                    return;
                }
                var map = new EmissionMap();
                map.Load(path);
                SolarSystemCameraTexture.EmissionMap = map;
                Debug.Log($"[ExoInstruments] Emission map: {map.LineName} at nside {map.Nside} "
                        + $"({map.ResolutionArcmin:F1} arcmin), {map.Source}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ExoInstruments] Failed to load the emission map: {e.Message}");
            }
        }

        private void LoadEmissionPatches()
        {
            string path = KSPUtil.ApplicationRootPath
                        + "GameData/ExoInstruments/PluginData/HalphaPatches.patchset";
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    Debug.Log("[ExoInstruments] No high-resolution emission patches installed, so every "
                            + "field is drawn at the all-sky map's 6 arcmin beam. Build them with "
                            + "tools/pack_shassa_patches.py and place the result at " + path);
                    return;
                }
                var set = new EmissionPatchSet();
                set.Load(path);
                SolarSystemCameraTexture.EmissionPatches = set;
                // Two steps, in this order. First throw out what cannot be sky -- cells
                // inconsistent with their own neighbourhood, whatever produced them. Then enforce
                // the contract: at the composite's own beam a patch must say exactly what the
                // composite says, which is what leaves no seam anywhere, including at its rim.
                set.RejectOutliers();
                set.CalibrateAgainst(SolarSystemCameraTexture.EmissionMap);
                Debug.Log($"[ExoInstruments] Emission patches: {set.PatchCount} regions at nside {set.Nside} "
                        + $"({set.ResolutionArcmin:F2} arcmin), {set.Source}"
                        + (set.RejectedCells > 0
                            ? $" | {set.RejectedCells} outlier cells rejected"
                            : "")
                        + (set.CalibratedCells > 0
                            ? $" | {set.CalibratedCells} cells gain-matched to the composite at its own beam"
                            : ""));
            }
            catch (Exception e)
            {
                Debug.LogError($"[ExoInstruments] Failed to load the emission patches: {e.Message}");
            }
        }

        private void LoadGalaxyCatalog()
        {
            string path = KSPUtil.ApplicationRootPath
                        + "GameData/ExoInstruments/PluginData/GalaxyCatalog.galcat";
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    Debug.Log("[ExoInstruments] No galaxy catalogue installed, so photographs show no "
                            + "galaxies. Build one with tools/pack_galaxy_catalog.py and place it at " + path);
                    return;
                }
                var catalog = new GalaxyCatalog();
                catalog.Load(path);
                SolarSystemCameraTexture.GalaxyCatalog = catalog;
                Debug.Log($"[ExoInstruments] Galaxy catalogue: {catalog.Count} galaxies, {catalog.Source}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ExoInstruments] Failed to load the galaxy catalogue: {e.Message}");
            }
        }

        private List<StarTarget> LoadExoplanetCatalog()
        {
            string path = KSPUtil.ApplicationRootPath + "GameData/ExoInstruments/PluginData/ExoplanetCatalog.csv";
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    Debug.LogWarning($"[ExoInstruments] Catalog file not found at {path}. No targets loaded.");
                    return new List<StarTarget>();
                }
                string csvText = System.IO.File.ReadAllText(path);
                var result = ExoplanetCsvLoader.LoadFromCsv(csvText);
                Debug.Log($"[ExoInstruments] Loaded {result.Loaded} targets from real catalog " +
                          $"(skipped {result.SkippedNoStarData} missing star data, {result.SkippedNoMagnitude} missing magnitude, " +
                          $"{result.NoCoordinates} loaded without sky coordinates, won't appear on the sky chart).");
                return result.Targets;
            }
            catch (Exception e)
            {
                Debug.LogError($"[ExoInstruments] Failed to load catalog: {e.Message}");
                return new List<StarTarget>();
            }
        }

        /// <summary>
        /// Folds the Yale Bright Star Catalogue into the target list as decoy
        /// stars (HasPlanet = false), deduplicated against real planet hosts by
        /// StarCatalogMerger. On any failure the exoplanet catalog alone is
        /// returned; the mod stays fully usable without decoys.
        /// </summary>
        private List<StarTarget> MergeWithBackgroundStars(List<StarTarget> exoplanetTargets)
        {
            string path = KSPUtil.ApplicationRootPath + "GameData/ExoInstruments/PluginData/BrightStarCatalog.tsv";
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    Debug.LogWarning($"[ExoInstruments] Background star catalog not found at {path}. Sky will only show known planet hosts.");
                    return exoplanetTargets;
                }
                var bsc = BackgroundStarCatalogLoader.LoadFromTsv(System.IO.File.ReadAllText(path));
                CatalogMergeResult merge = StarCatalogMerger.Merge(exoplanetTargets, bsc.Entries);
                Debug.Log($"[ExoInstruments] Background stars: {bsc.Loaded} loaded from BSC5; " +
                          $"deduplicated {merge.MatchedByHd} by HD, {merge.MatchedByHr} by HR, {merge.MatchedByName} by name, {merge.MatchedByPosition} by position; " +
                          $"{merge.DecoysKept} decoys kept, {merge.Merged.Count} total targets.");
                if (merge.AmbiguousNameKeys.Count > 0)
                {
                    Debug.LogWarning($"[ExoInstruments] {merge.AmbiguousNameKeys.Count} ambiguous name matches during catalog merge: " +
                                      string.Join(" | ", merge.AmbiguousNameKeys.ToArray()));
                }
                return merge.Merged;
            }
            catch (Exception e)
            {
                Debug.LogError($"[ExoInstruments] Failed to merge background star catalog: {e.Message}");
                return exoplanetTargets;
            }
        }

        void Update()
        {
            // Apply whatever the background render Tasks finished since last
            // frame, cheap (an IsCompleted check, at most a texture upload),
            // safe to do every frame regardless of which session is active.
            PollImagingRenderTask();
            PollSkyChartRenderTask();
            PollForecastRenderTask();
            PollTransitAnalysisTask();
            PollRvAnalysisTask();
            BetterTimeWarpIntegration.PollRestore();
            if (windowVisible && SelectedInstrument.Method == DetectionMethod.SolarSystemPhotography)
            {
                solarSystemCamera.TickCapture(Time.deltaTime);

                // Stacking batch: grab the sub that just finished, then chain
                // into the next exposure until the batch is done or capture
                // conditions changed out from under it (body set, twilight
                // ended, ...). Filter and FOV are locked (their GUI controls
                // are disabled) for the duration of a batch, so every sub
                // added here is guaranteed to share the filter/FOV of the
                // first one; AddSub's FOV check is a defensive backstop,
                // not the primary guarantee.
                // PIPELINED. The next exposure is started as soon as the previous one's integration
                // ends (below), not after its reduction finishes, so the several seconds of physics
                // run inside the next two minutes of sky instead of after them. Collecting the
                // finished sub is therefore a separate step from starting the next exposure: the
                // two no longer happen in the same tick.
                if (stackBatchRemaining > 0 && solarSystemCamera.HasCapturedPhoto)
                {
                    AstroSubResult subResult = astroStack.AddSub(
                        solarSystemCamera.Filter, solarSystemCamera.GetLastCaptureGray(),
                        solarSystemCamera.FovDeg, solarSystemCamera.ExposureSeconds,
                        solarSystemCamera.GetDefectPixelIndices(),
                        solarSystemCamera.LastRegistrationX, solarSystemCamera.LastRegistrationY);
                    solarSystemCamera.ConsumeCapturedPhoto();

                    if (subResult == AstroSubResult.FilterFull)
                    {
                        stackBatchInterruptedMessage = $"Stack {FilterLabel(solarSystemCamera.Filter)} full ({AstroImageStack.MaxSubsPerFilter} subs). Compose or clear the stack before capturing more.";
                        stackBatchRemaining = 0;
                    }
                    else if (subResult == AstroSubResult.FovMismatch)
                    {
                        stackBatchInterruptedMessage = $"FOV changed since earlier {FilterLabel(solarSystemCamera.Filter)} subs; clear the stack or match the original FOV.";
                        stackBatchRemaining = 0;
                    }
                    else
                    {
                        stackBatchCollected++;
                        if (stackBatchCollected >= stackBatchRemaining)
                        {
                            stackBatchRemaining = 0;
                            stackBatchQueued = 0;
                            stackBatchCollected = 0;
                        }
                    }
                }

                // Open the shutter again the moment nothing is integrating and nothing is waiting to
                // be rendered, which is true while the previous frame is still being reduced.
                if (stackBatchRemaining > 0 && stackBatchQueued < stackBatchRemaining
                    && solarSystemCamera.CanBeginExposure)
                {
                    if (CanExposePhotography())
                    {
                        solarSystemCamera.BeginExposure(selectedPhotographyTarget);
                        stackBatchQueued++;
                    }
                    else
                    {
                        stackBatchInterruptedMessage = "Series stopped: it must be night and the body above the horizon.";
                        stackBatchRemaining = 0;
                        stackBatchQueued = 0;
                        stackBatchCollected = 0;
                    }
                }
            }

            if (session == null && rvSession == null && imagingSession == null)
            {
                if (windowVisible && Time.realtimeSinceStartup >= nextSkyChartRefreshTime)
                {
                    // Paused or time-warp-stalled: UT hasn't moved, so the whole
                    // catalog would re-transform to the exact same Alt/Az it's
                    // already showing. Skip starting a new refresh entirely.
                    double skyUt = Planetarium.GetUniversalTime();
                    if (skyUt != lastSkyChartRefreshUt)
                    {
                        StartSkyChartRefresh();
                        lastSkyChartRefreshUt = skyUt;
                    }
                    nextSkyChartRefreshTime = Time.realtimeSinceStartup + SkyChartRefreshIntervalSeconds;
                }
                if (windowVisible) MaybeStartForecastRefresh();
                return;
            }

            double ut = Planetarium.GetUniversalTime();

            if (session != null)
            {
                session.Tick(ut);
                if (session.IsRunning && windowVisible && Time.realtimeSinceStartup >= nextRawPlotRefreshTime)
                {
                    RefreshRawPlotTexture();
                    nextRawPlotRefreshTime = Time.realtimeSinceStartup + RawPlotRefreshIntervalSeconds;
                }
            }
            else if (rvSession != null)
            {
                rvSession.Tick(ut);
                if (rvSession.IsRunning && windowVisible && Time.realtimeSinceStartup >= nextRawPlotRefreshTime)
                {
                    RefreshRvRawPlotTexture();
                    RefreshRmSchedule(ut);
                    nextRawPlotRefreshTime = Time.realtimeSinceStartup + RawPlotRefreshIntervalSeconds;
                }
            }
            else
            {
                imagingSession.Tick(ut);
                if (imagingSession.IsRunning && windowVisible && Time.realtimeSinceStartup >= nextImagingRefreshTime)
                {
                    // Paused: UT hasn't moved, nothing in the frame would change; skip starting a new refresh.
                    if (ut != lastImagingRefreshUt)
                    {
                        StartImagingRefresh();
                        lastImagingRefreshUt = ut;
                    }
                    nextImagingRefreshTime = Time.realtimeSinceStartup + ImagingRefreshIntervalSeconds;
                }
            }
        }

        void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            if (button != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(button);
            }
            DebugScreenConsole.RemoveConsoleCommand(ConsoleCommand);
            InputLockManager.RemoveControlLock(InputLockId);
            ExoObservatoryBuilding.Clicked -= OnObservatoryBuildingClicked;
            solarSystemCamera.Dispose();
            ClearTextures();
            if (skyChartTexture != null) { Destroy(skyChartTexture); skyChartTexture = null; }
            if (forecastTexture != null) { Destroy(forecastTexture); forecastTexture = null; }
            if (stackedCompositeTexture != null) { Destroy(stackedCompositeTexture); stackedCompositeTexture = null; }
        }

        void AddButton()
        {
            if (button == null)
            {
                Texture2D icon = GameDatabase.Instance.GetTexture("ExoInstruments/Textures/toolbar_icon", false)
                    ?? Texture2D.whiteTexture;
                button = ApplicationLauncher.Instance.AddModApplication(
                    OnToggleOn,
                    OnToggleOff,
                    null, null, null, null,
                    ApplicationLauncher.AppScenes.SPACECENTER,
                    icon
                );
            }
        }

        void OnToggleOn() { OpenObservatoryWindow(); }
        void OnToggleOff() { CloseObservatoryWindow(); }

        /// <summary>
        /// The single entry point for opening the observatory, regardless of how
        /// it was triggered: the AppLauncher toolbar button, the debug console
        /// command above, or (later) an in-world clickable building/zone.
        /// </summary>
        void OpenObservatoryWindow()
        {
            // button.SetTrue() invokes its own registered OnToggleOn callback (this
            // method), even when called programmatically; without this guard that
            // recurses forever and crashes with a StackOverflowException, e.g. when
            // returning to the Space Center with the window already open re-fires
            // this via the AppLauncher button restoring its toggled state.
            if (windowVisible) return;
            windowVisible = true;
            if (button != null) button.SetTrue();
            // KSC_ALL alone doesn't cover camera scroll/pan/rotate; CAMERACONTROLS is a
            // separate flag, and without it the mouse wheel zooms the KSC camera right
            // through this fullscreen panel.
            InputLockManager.SetControlLock(ControlTypes.KSC_ALL | ControlTypes.CAMERACONTROLS, InputLockId);
        }

        void CloseObservatoryWindow()
        {
            if (!windowVisible) return;
            windowVisible = false;
            if (button != null) button.SetFalse();
            InputLockManager.RemoveControlLock(InputLockId);
        }

        void EnsureStyles()
        {
            if (stylesInitialized) return;
            headerLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, richText = true };
            fullscreenWindowStyle = new GUIStyle(GUI.skin.box); // solid-ish panel background, no title bar
            plotTitleStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter, richText = true };
            axisLabelRightStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleRight };
            axisLabelLeftStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleLeft };
            smallCaptionStyle = new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Italic, wordWrap = true };
            wrappedLabelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            sectionHeaderStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            stylesInitialized = true;
        }

        void OnGUI()
        {
            if (!windowVisible) return;
            GUI.matrix = Matrix4x4.identity; // guard against inheriting a leftover GUI scale from another mod/KSP's own UI this frame
            EnsureStyles();
            Rect fullscreenRect = new Rect(0, 0, Screen.width, Screen.height);

            // GUILayout.Window is built for draggable windows and does its own
            // internal Rect<->content-area remapping tuned for the "window" skin
            // style. BeginArea/EndArea has none of that; mouse coordinates stay
            // in plain screen space the whole way through, which is what we want
            // for a fixed fullscreen panel with no dragging.
            GUI.Box(fullscreenRect, GUIContent.none, fullscreenWindowStyle);
            GUILayout.BeginArea(fullscreenRect);
            DrawWindow(0);
            GUILayout.EndArea();
        }

        void DrawWindow(int windowID)
        {
            GUILayout.BeginVertical();
            DrawHeaderBar();
            GUILayout.Space(14);

            GUILayout.BeginHorizontal();
            GUILayout.Space(ColumnGap);

            GUILayout.BeginVertical(GUILayout.Width(LeftColumnWidth));
            DrawLeftColumn();
            GUILayout.EndVertical();

            GUILayout.Space(ColumnGap);

            float rightColumnWidth = Mathf.Max(560f, Screen.width - LeftColumnWidth - ColumnGap * 3f);
            GUILayout.BeginVertical(GUILayout.Width(rightColumnWidth));
            DrawRightColumn();
            GUILayout.EndVertical();

            GUILayout.Space(ColumnGap);
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            // Fullscreen fixed panel via BeginArea, nothing to drag, no window chrome.
        }


        void DrawHeaderBar()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>ExoInstruments Observatory</b>", headerLabelStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(90), GUILayout.Height(28)))
            {
                CloseObservatoryWindow();
            }
            GUILayout.EndHorizontal();
        }

        void DrawLeftColumn()
        {
            scrollPosLeft = GUILayout.BeginScrollView(scrollPosLeft, GUILayout.Height(ColumnContentHeight));

            bool anyStarSessionActive = session != null || rvSession != null || imagingSession != null;

            if (!anyStarSessionActive && !photographySessionActive)
            {
                // One shared chart. Clicking a body or a star points the telescope at it; a star
                // additionally stays selected for the detection instruments, which need a
                // catalogue entry rather than a direction.
                DrawStarSelection();
                DrawManualPointingEntry();

                if (selectedStar != null)
                {
                    GUILayout.Space(14);
                    DrawTargetInfoCard(selectedStar);
                    DrawPointingSummary();
                    GUILayout.Space(10);
                    DrawObservatorySelector();
                    DrawStartObservationButton();
                    if (SelectedInstrument.Method == DetectionMethod.SolarSystemPhotography)
                        DrawPhotographyForecastPanel();
                    else
                        DrawForecastPanel();
                }
                else if (selectedPhotographyTarget.HasTarget)
                {
                    GUILayout.Space(14);
                    DrawPhotographyTargetInfoCard(selectedPhotographyTarget);
                    GUILayout.Space(10);
                    DrawObservatorySelector();
                    DrawStartObservationButton();
                    DrawPhotographyForecastPanel();
                }
            }
            else if (photographySessionActive)
            {
                GUILayout.Label("Currently pointing at:");
                GUILayout.Space(6);
                DrawPhotographyTargetInfoCard(selectedPhotographyTarget);
                GUILayout.Space(10);
                GUILayout.Label("Stop (right panel) to pick a new target.");
            }
            else
            {
                StarTarget activeTarget = session != null ? session.Target
                    : rvSession != null ? rvSession.Target
                    : imagingSession.Target;
                GUILayout.Label("Currently observing:");
                GUILayout.Space(6);
                DrawTargetInfoCard(activeTarget);
                GUILayout.Space(10);
                GUILayout.Label("Stop the observation (right panel) to pick a new target.");
            }

            GUILayout.EndScrollView();
        }

        /// <summary>
        /// Runtime IMGUI has no native combo box, so this hand-rolls one: a button
        /// showing the current pick, which expands into a vertical list of the
        /// other options on click and collapses again on selection.
        /// </summary>
        void DrawObservatorySelector()
        {
            GUILayout.Label("Observatory:");

            // Defensive fallback: the selected instrument shouldn't ever be
            // locked (selection only happens by clicking an unlocked row below),
            // but a save loaded mid-session or a mode switch is cheap insurance
            // against getting stuck on an instrument the player can't use.
            if (!IsInstrumentUnlocked(SelectedInstrument))
            {
                int fallback = Array.FindIndex(Observatories.All, IsInstrumentUnlocked);
                SelectObservatory(fallback >= 0 ? fallback : 0);
            }

            string currentLabel = SelectedInstrument.DisplayName;
            string buttonLabel = (observatoryMenuOpen ? "▲ " : "▼ ") + currentLabel;
            if (GUILayout.Button(buttonLabel, GUILayout.Height(28)))
            {
                observatoryMenuOpen = !observatoryMenuOpen;
            }

            if (observatoryMenuOpen)
            {
                for (int i = 0; i < Observatories.All.Length; i++)
                {
                    InstrumentSpec instrument = Observatories.All[i];
                    if (!IsInstrumentUnlocked(instrument))
                    {
                        DrawLockedInstrumentRow(instrument);
                        continue;
                    }
                    bool isCurrent = i == selectedObservatoryIndex;
                    string label = isCurrent ? $"> {instrument.DisplayName}" : instrument.DisplayName;
                    if (GUILayout.Button(label, GUILayout.Height(24)))
                    {
                        SelectObservatory(i);
                        observatoryMenuOpen = false;
                    }
                }
            }

            DrawInstrumentPresentation(SelectedInstrument);

            if (CareerFogActive)
            {
                GUILayout.Label($"Telescope time: {SelectedInstrument.ScanCostFunds:N0} Funds per observation.   " +
                                 $"Detection reward: x{SelectedInstrument.ScienceRewardMultiplier:0.#}.", smallCaptionStyle);
            }

            // The feasibility hints below are about a selected catalog star,
            // meaningless, and selectedStar may well be null, whenever a
            // solar-system body is the active target instead (the unified
            // chart lets either be selected regardless of which instrument
            // happens to be chosen right now; DrawStartObservationButton is
            // what actually blocks a mismatched instrument/target pairing).
            if (SelectedInstrument.Method == DetectionMethod.SolarSystemPhotography) return;
            if (selectedStar == null) return;

            // The feasibility hints below derive from catalog truth (system
            // periods, planet contrast); on an unscanned career target they
            // would leak whether it's a known host before any data is taken.
            if (IsIdentityHidden(selectedStar))
            {
                GUILayout.Label("No prior data on this target. Feasibility can't be estimated before a first identification scan.", smallCaptionStyle);
                return;
            }

            if (SelectedInstrument.Method == DetectionMethod.RadialVelocity)
            {
                var systemPlanets = GetSystemPlanets(selectedStar);
                int rvDetectableCount = CountRvDetectable(systemPlanets);

                if (rvDetectableCount == 0)
                {
                    GUILayout.Label("No planet mass on record anywhere in this system. Expect a null result.", smallCaptionStyle);
                }
                else
                {
                    if (rvDetectableCount > 1)
                    {
                        GUILayout.Label($"{rvDetectableCount} known planets orbit this host. Their reflex signals superpose, so one campaign samples them all.", smallCaptionStyle);
                    }
                    double neededDays = RvDetector.EstimateRequiredBaselineDays(LongestRvPeriodDays(systemPlanets), SelectedInstrument.CadenceSeconds);
                    GUILayout.Label($"Estimated baseline needed: ~{ToDisplayDays(neededDays * 86400.0):F1} days at this cadence to resolve the longest catalog period in the system.", smallCaptionStyle);
                }
            }
            else if (SelectedInstrument.Method == DetectionMethod.DirectImaging)
            {
                if (!selectedStar.HasPlanet)
                {
                    GUILayout.Label("No catalogued companion here. Imaging will still characterize the star itself.", smallCaptionStyle);
                    return;
                }
                var assessment = DirectImagingSimulator.Assess(selectedStar, SelectedInstrument);
                if (!assessment.HasRequiredData)
                {
                    GUILayout.Label($"{assessment.MissingDataReason}: companion search will be inconclusive. " +
                                     (selectedStar.EffectiveTempK.HasValue ? "Stellar characterization still possible." : "No color data for characterization either."), smallCaptionStyle);
                }
                else if (!assessment.Resolvable)
                {
                    GUILayout.Label($"Separation {assessment.SeparationArcsec * 1000.0:F1} mas is inside the {assessment.DiffractionLimitArcsec * 1000.0:F1} mas diffraction limit. " +
                                     "Not resolvable at any exposure.", smallCaptionStyle);
                }
                else
                {
                    double neededSeconds = DirectImagingSimulator.RequiredExposureSeconds(assessment);
                    string exposureNote = double.IsInfinity(neededSeconds)
                        ? "below the deep contrast limit, not detectable"
                        : neededSeconds < 3600.0 * 24.0
                            ? $"~{neededSeconds / 3600.0:F1} h of integration to reach 5-sigma"
                            : $"~{ToDisplayDays(neededSeconds):F1} days of integration to reach 5-sigma";
                    GUILayout.Label($"Separation {assessment.SeparationArcsec * 1000.0:F0} mas, contrast {assessment.ContrastRatio:E1}: {exposureNote}.", smallCaptionStyle);
                }
            }
            else
            {
                int transitingCount = CountTransiting(GetSystemPlanets(selectedStar));
                if (transitingCount > 1)
                {
                    GUILayout.Label($"{transitingCount} known transiting planets orbit this host. Their transits superpose on one light curve; the analysis separates them by iterative masking.", smallCaptionStyle);
                }
            }
        }

        /// <summary>
        /// Selects an Observatories.All row. If it's a SolarSystemPhotography instrument (RC20,
        /// CDK1000, ...), also syncs SolarSystemCameraTexture's active telescope to match,
        /// re-deriving every optics/sensor-driven quantity the physics pipeline uses (aperture,
        /// focal length, FOV range, exposure/gain range, QE, full well, read/dark noise,
        /// cosmic-ray rate, astigmatism) from the newly selected instrument's real spec (see
        /// SolarSystemCameraTexture.SetActiveTelescope). A captured-but-unsaved photo and any
        /// stacked subs are tied to whichever instrument produced them (different aperture/
        /// focal-length/plate-scale invalidate them), so both are discarded on switch, exactly
        /// like SelectPhotographyBody does on a target switch. No-op (besides the index update)
        /// when the newly selected row isn't a photography instrument, or already matches the
        /// active telescope.
        /// </summary>
        void SelectObservatory(int index)
        {
            selectedObservatoryIndex = index;
            InstrumentSpec instrument = Observatories.All[index];
            if (instrument.Method != DetectionMethod.SolarSystemPhotography) return;
            if (instrument.VisualTelescope == null) return;
            if (instrument.VisualTelescope == SolarSystemCameraTexture.ActiveTelescope) return;

            solarSystemCamera.DiscardCapturedPhoto();
            ClearAstroStack();
            solarSystemCamera.SetActiveTelescope(instrument.VisualTelescope);
        }

        // Which instrument's presentation card is currently expanded; toggled by
        // clicking the card header. Starts expanded the first time an instrument
        // is selected so a new player sees the explanation at least once.
        private string presentationOpenFor;
        private bool presentationInitialized;

        /// <summary>
        /// Short presentation card for the selected instrument: a plain-language
        /// description of what the device physically is plus its key working
        /// numbers, so the player knows what they're about to point at the sky.
        /// Collapsible so veterans can fold it away.
        /// </summary>
        void DrawInstrumentPresentation(InstrumentSpec instrument)
        {
            if (!presentationInitialized)
            {
                presentationInitialized = true;
                presentationOpenFor = instrument.Name;
            }

            bool open = presentationOpenFor == instrument.Name;
            string toggleLabel = (open ? "▲ " : "▼ ") + $"About {instrument.Name}";
            if (GUILayout.Button(toggleLabel, GUILayout.Height(22)))
            {
                presentationOpenFor = open ? null : instrument.Name;
                open = !open;
            }
            if (!open) return;

            GUILayout.BeginVertical(GUI.skin.box);
            if (!string.IsNullOrEmpty(instrument.Description))
            {
                GUILayout.Label(instrument.Description, wrappedLabelStyle);
                GUILayout.Space(4);
            }

            GUILayout.Label($"Method: {DescribeMethod(instrument.Method)}", smallCaptionStyle);
            if (instrument.IsSpaceBased)
            {
                GUILayout.Label("Platform: space-based. Observes continuously, unaffected by daylight, weather or moonlight.", smallCaptionStyle);
            }
            else if (instrument.Method == DetectionMethod.SolarSystemPhotography)
            {
                GUILayout.Label($"Platform: ground-based, {instrument.ApertureMeters:0.##} m aperture. Only at night, no target altitude limit modeled yet.", smallCaptionStyle);
            }
            else
            {
                GUILayout.Label($"Platform: ground-based, {instrument.ApertureMeters:0.##} m aperture at {instrument.SiteAltitudeMeters:N0} m altitude. " +
                                 "Observes only at night with the target above the horizon.", smallCaptionStyle);
            }
            if (instrument.Method != DetectionMethod.SolarSystemPhotography)
            {
                GUILayout.Label($"Cadence: one measurement every {DescribeDuration(instrument.CadenceSeconds)}.", smallCaptionStyle);
            }
            GUILayout.Label($"Reference: {instrument.Citation}", smallCaptionStyle);
            GUILayout.EndVertical();
        }

        private static string DescribeMethod(DetectionMethod method)
        {
            switch (method)
            {
                case DetectionMethod.Transit:
                    return "transit photometry. Watches the star's brightness for the periodic dip of a planet crossing its disk.";
                case DetectionMethod.RadialVelocity:
                    return "radial velocity. Measures the star's wobble through Doppler shifts of its spectral lines.";
                case DetectionMethod.DirectImaging:
                    return "direct imaging. Blocks the starlight and integrates on the planet's own faint glow.";
                case DetectionMethod.SolarSystemPhotography:
                    return "amateur astrophotography. Points at a solar-system body and takes a real photograph, no exoplanet detection involved.";
                default:
                    return method.ToString();
            }
        }

        private static string DescribeDuration(double seconds)
        {
            if (seconds < 120.0) return $"{seconds:F0} s";
            if (seconds < 7200.0) return $"{seconds / 60.0:F0} min";
            return $"{seconds / 3600.0:F0} h";
        }

        /// <summary>
        /// All catalog planets sharing this target's host star, target included.
        /// Neither photometry nor spectroscopy can isolate one planet: the star's
        /// light curve superposes every transiting companion and its reflex
        /// motion carries every orbiting mass, so the whole system is observed
        /// together whether the player asked for it or not.
        /// </summary>
        List<StarTarget> GetSystemPlanets(StarTarget target)
        {
            if (rvSystemCacheKey == target && rvSystemCache != null) return rvSystemCache;

            var planets = new List<StarTarget> { target };
            if (!string.IsNullOrEmpty(target.HostStarName))
            {
                foreach (var star in catalog)
                {
                    if (star == target) continue;
                    if (string.Equals(star.HostStarName, target.HostStarName, StringComparison.OrdinalIgnoreCase))
                        planets.Add(star);
                }
            }
            rvSystemCacheKey = target;
            rvSystemCache = planets;
            return planets;
        }

        static int CountRvDetectable(List<StarTarget> planets)
        {
            int n = 0;
            foreach (var p in planets) if (p.IsRvDetectable) n++;
            return n;
        }

        /// <summary>
        /// System members an RV session can schedule a Rossiter-McLaughlin
        /// sequence around: a known transit ephemeris (geometry + duration) is
        /// the prerequisite, and in career, "known" means the target has been
        /// identified. Scheduling around a hidden star's catalog ephemeris would
        /// leak the fog answer, so hidden targets get an empty list (the RM
        /// physics stays in the signal either way, only the scheduling is off).
        /// </summary>
        List<StarTarget> GetRmSchedulablePlanets(StarTarget target)
        {
            var schedulable = new List<StarTarget>();
            if (IsIdentityHidden(target)) return schedulable;
            foreach (var planet in GetSystemPlanets(target))
            {
                if (!planet.IsTransiting) continue;
                if (!(planet.EstimatedTransitDurationHours > 0.0)) continue;
                schedulable.Add(planet);
            }
            return schedulable;
        }

        /// <summary>Longest RV-detectable catalog period in the system, the baseline driver, since the slowest planet is the last to close two full orbits.</summary>
        static double LongestRvPeriodDays(List<StarTarget> planets)
        {
            double longest = 0.0;
            foreach (var p in planets)
            {
                if (p.IsRvDetectable && p.PlanetPeriodDays > longest) longest = p.PlanetPeriodDays;
            }
            return longest;
        }

        void StartObservation()
        {
            // Telescope time is paid up front, per observation started; the
            // button that calls this is disabled when it isn't affordable.
            if (CareerFogActive && SelectedInstrument.ScanCostFunds > 0.0 && Funding.Instance != null)
            {
                Funding.Instance.AddFunds(-SelectedInstrument.ScanCostFunds, TransactionReasons.RnDPartPurchase);
            }

            InstrumentSpec instrument = SelectedInstrument;
            if (instrument.Method == DetectionMethod.SolarSystemPhotography)
            {
                photographySessionActive = true;
                return;
            }

            double ut = Planetarium.GetUniversalTime();
            ClearTextures();
            if (instrument.Method == DetectionMethod.Transit)
            {
                session = new ObservationSession(selectedStar, GetSystemPlanets(selectedStar), instrument, ut, BuildImagingObserverContext());
                lastTransitStages = null;
                lastTtvResult = null;
                RefreshRawPlotTexture();
            }
            else if (instrument.Method == DetectionMethod.RadialVelocity)
            {
                rvSession = new RvObservationSession(selectedStar, GetSystemPlanets(selectedStar), instrument, ut, BuildImagingObserverContext(),
                    GetRmSchedulablePlanets(selectedStar));
                lastRvStages = null;
                lastRmResult = null;
                lastRmPlanet = null;
                RefreshRvRawPlotTexture();
            }
            else
            {
                imagingRenderGeneration++;
                imagingSession = new ImagingObservationSession(selectedStar, instrument, ut, BuildImagingObserverContext());
                lastImagingResult = null;
                RefreshImagingTexture();
                RefreshImagingPredictions();
                lastImagingRefreshUt = ut;
            }
        }

        void DrawTargetInfoCard(StarTarget target)
        {
            if (IsIdentityHidden(target))
            {
                DrawHiddenTargetInfoCard(target);
                return;
            }
            if (!target.HasPlanet)
            {
                DrawDecoyInfoCard(target);
                return;
            }

            GUILayout.Label($"{target.Name}  [{target.Status}]", sectionHeaderStyle);
            if (!string.IsNullOrEmpty(target.DetectionType))
            {
                string yearSuffix = target.DiscoveryYear.HasValue ? $" ({target.DiscoveryYear})" : "";
                GUILayout.Label($"Detected via: {target.DetectionType}{yearSuffix}");
            }
            GUILayout.Label($"Magnitude: {target.ApparentMagnitude:F1}   Distance: {target.DistanceParsec:F1} pc");
            GUILayout.Label($"Stellar radius: {target.RadiusSolar:F3} R_sun   Stellar mass: {target.StellarMassSolar:F3} M_sun");
            if (target.PlanetMassJupiter.HasValue)
            {
                GUILayout.Label($"Planet mass (min): {target.PlanetMassJupiter.Value:F3} M_jup");
            }
            // Activity is knowable for an identified target (real surveys read it
            // off archival photometry / Ca II indices), and it tells the player
            // why an ultra-precise spectrograph still can't see below ~1 m/s here.
            GUILayout.Label($"Stellar activity: RV jitter ~{StellarActivity.RvJitterMps(target):F1} m/s, spot variability ~{StellarActivity.SpotAmplitudePpm(target):F0} ppm (P_rot ~{StellarActivity.RotationPeriodDays(target):F0} d)");
            DrawHabitableZoneLines(target);
        }

        /// <summary>Same card slot as DrawTargetInfoCard, for a solar-system body target instead of a catalog star: real radius, current alt/az, and the observability line.</summary>
        void DrawPhotographyTargetInfoCard(SkyTarget target)
        {
            GUILayout.Label(target.DisplayName, sectionHeaderStyle);
            if (target.IsBody)
            {
                CelestialBody body = target.Body;
                GUILayout.Label($"Radius: {body.Radius / 1000.0:N0} km" + (body.atmosphere ? "   Has an atmosphere" : ""));
            }
            else if (target.IsEquatorial)
            {
                GUILayout.Label($"RA {target.RaDeg:F4} deg   Dec {target.DecDeg:F4} deg", smallCaptionStyle);
                GUILayout.Label(SkyTarget.FormatCoordinates(target.RaDeg, target.DecDeg), smallCaptionStyle);
            }
            DrawPhotographyObservability();
        }

        /// <summary>
        /// Career, unscanned: only what pointing a telescope actually gives you,
        /// where it is and how bright it is. No name, no catalog status, no
        /// stellar parameters, and no feasibility estimates anywhere else either.
        /// </summary>
        void DrawHiddenTargetInfoCard(StarTarget target)
        {
            GUILayout.Label(GetDisplayName(target));
            GUILayout.Label($"Magnitude: {target.ApparentMagnitude:F1}");
            if (target.RaDeg.HasValue && target.DecDeg.HasValue)
            {
                GUILayout.Label($"Position: RA {target.RaDeg.Value:F3} deg   Dec {target.DecDeg.Value:F3} deg");
            }
            GUILayout.Label("Not yet surveyed. Position and brightness are all the sky gives away. " +
                             "Complete an observation and run the analysis to identify this star.", smallCaptionStyle);
        }

        /// <summary>
        /// Revealed background star: a real BSC5 entry with no catalogued planet.
        /// Distance/radius/mass lines are omitted, not zero-filled; the Bright
        /// Star Catalogue genuinely doesn't carry them.
        /// </summary>
        void DrawDecoyInfoCard(StarTarget target)
        {
            GUILayout.Label($"{target.Name}  [no catalogued planet]");
            GUILayout.Label("Background star (Yale Bright Star Catalogue)");
            GUILayout.Label($"Magnitude: {target.ApparentMagnitude:F1}");
            if (target.EffectiveTempK.HasValue)
            {
                GUILayout.Label($"Color temperature: ~{target.EffectiveTempK.Value:F0} K " +
                                 $"(class {StellarColor.SpectralClass(target.EffectiveTempK.Value)}, from B-V)");
            }
            GUILayout.Label("No catalogued companion here. Imaging will still characterize the star itself.", smallCaptionStyle);
        }

        void DrawHabitableZoneLines(StarTarget target)
        {
            if (!target.EffectiveTempK.HasValue)
            {
                GUILayout.Label("Habitable zone: unknown (no stellar Teff on record)", smallCaptionStyle);
                return;
            }

            HabitableZoneResult zone = HabitableZoneCalculator.Compute(target.EffectiveTempK.Value, target.RadiusSolar);
            if (zone == null)
            {
                GUILayout.Label($"Habitable zone: n/a (Teff {target.EffectiveTempK.Value:F0} K outside the " +
                                 $"{HabitableZoneCalculator.MinValidTeffK:F0}-{HabitableZoneCalculator.MaxValidTeffK:F0} K model range)", smallCaptionStyle);
                return;
            }

            GUILayout.Label($"Habitable zone (Kopparapu 2014): {zone.InnerConservativeAU:F2}-{zone.OuterConservativeAU:F2} AU " +
                             $"(optimistic {zone.InnerOptimisticAU:F2}-{zone.OuterOptimisticAU:F2} AU)", smallCaptionStyle);

            double aAU = target.EstimatedSemiMajorAxisAU;
            if (aAU <= 0)
            {
                GUILayout.Label("Planet orbit: unknown, so it can't be placed relative to the habitable zone.", smallCaptionStyle);
                return;
            }

            switch (HabitableZoneCalculator.Classify(zone, aAU))
            {
                case HzVerdict.ConservativeHz:
                    GUILayout.Label($"Planet at {aAU:F2} AU: inside the conservative habitable zone.", smallCaptionStyle);
                    break;
                case HzVerdict.OptimisticOnly:
                    GUILayout.Label($"Planet at {aAU:F2} AU: in the optimistic band only (Recent Venus / Early Mars limits).", smallCaptionStyle);
                    break;
                case HzVerdict.TooHot:
                    GUILayout.Label($"Planet at {aAU:F2} AU: too close, inside even the optimistic inner edge.", smallCaptionStyle);
                    break;
                default:
                    GUILayout.Label($"Planet at {aAU:F2} AU: too far, beyond even the optimistic outer edge.", smallCaptionStyle);
                    break;
            }
        }


        /// <summary>
        /// Altitude/azimuth of a celestial body in KSC's local sky right now,
        /// computed straight from world positions (no RA/Dec round-trip): local
        /// vertical from the KSC-to-Kerbin-centre vector, north/east from
        /// Kerbin's spin axis. Altitude is negative below the horizon.
        /// </summary>
        bool TryComputeBodyAltAz(CelestialBody body, out double altDeg, out double azDeg)
        {
            altDeg = 0.0; azDeg = 0.0;
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || body == null) return false;

            Vector3d obsPos = home.GetWorldSurfacePosition(
                ObservatorySite.LatitudeDeg, ObservatorySite.LongitudeDeg, 100.0);
            Vector3d up = (obsPos - home.position).normalized;
            Vector3d spinAxis = ((Vector3d)home.transform.up).normalized;
            Vector3d east = Vector3d.Cross(spinAxis, up).normalized;
            Vector3d north = Vector3d.Cross(up, east).normalized;
            Vector3d toBody = (body.position - obsPos).normalized;

            altDeg = 90.0 - Vector3d.Angle(up, toBody);
            double e = Vector3d.Dot(toBody, east);
            double n = Vector3d.Dot(toBody, north);
            azDeg = (Math.Atan2(e, n) * 180.0 / Math.PI + 360.0) % 360.0;
            return true;
        }

        /// <summary>
        /// Builds the solar-system body markers for the sky chart, on the main
        /// thread (reads KSP CelestialBody positions/radii). Each above-horizon
        /// body becomes a SkyChartPoint with IsBody set and a marker radius sized
        /// to the body's real physical radius (log-compressed), so a big planet
        /// plots as a bigger dot than a small moon, just larger than any star.
        /// Uses the same 0-deg geometric horizon as stars, matching the RC20
        /// capture gate and the body forecast.
        ///
        /// Also emits the parallel identity list used for click hit-testing.
        /// </summary>
        List<SkyChartPoint> BuildChartBodyPoints(out List<(CelestialBody, double, double)> hitList)
        {
            var points = new List<SkyChartPoint>();
            hitList = new List<(CelestialBody, double, double)>();

            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return points;

            var candidates = new List<(CelestialBody Body, double Alt, double Az, float Radius, Color Color)>();
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == home) continue;

                if (!TryComputeBodyAltAz(body, out double alt, out double az))
                    continue;

                // Must match the RC20 capture gate and ComputeBodyForecast:
                // a body is observable as soon as it is geometrically above the horizon.
                if (alt <= 0.0)
                    continue;

                candidates.Add((body, alt, az, BodyMarkerRadiusPx(body), BodyMarkerColor(body)));
            }

            DeclutterBodyPositions(candidates);

            foreach (var c in candidates)
            {
                points.Add(new SkyChartPoint
                {
                    IsBody = true,
                    AltitudeDeg = c.Alt,
                    AzimuthDeg = c.Az,
                    BodyMarkerRadiusPx = c.Radius,
                    BodyColor = c.Color,

                    // Only the selected photography target gets a ring.
                    IsSelectedTarget = c.Body == selectedPhotographyBody,
                });

                hitList.Add((c.Body, c.Alt, c.Az));
            }

            return points;
        }

        /// <summary>
        /// Nudges apart body markers whose real alt/az project to (nearly) the same screen
        /// point; a moon and its parent planet routinely do this in KSP's compressed solar
        /// system, stacking their dots on the chart and making them impossible to click apart.
        /// Detects overlapping clusters using the UNZOOMED (raw) projection, so the fix holds at
        /// any zoom level: SkyChartTexture's projection scales linearly with zoom, so a
        /// separation wide enough to click at zoom=1 only gets easier at higher zoom, never
        /// harder. Each cluster's members are then arranged evenly on a small circle around
        /// their shared position, sized so adjacent markers clear each other's click radius.
        ///
        /// Chart display and hit-testing ONLY; BuildChartBodyPoints feeds these adjusted alt/az
        /// into both the rendered SkyChartPoint and the hitList used for click hit-testing, so
        /// the dot the player sees is always the dot they can click. Every other use of a body's
        /// real position (capture aim, tracking, physics) calls TryComputeBodyAltAz or reads the
        /// CelestialBody directly and never sees this adjustment.
        /// </summary>
        void DeclutterBodyPositions(List<(CelestialBody Body, double Alt, double Az, float Radius, Color Color)> candidates)
        {
            if (candidates.Count < 2) return;

            int w = SkyChartWidth, h = SkyChartHeight;
            var rawView = SkyChartView.Default(w, h);
            var rawPos = new Vector2[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
            {
                rawPos[i] = SkyChartTexture.ProjectAltAzToScreen(candidates[i].Alt, candidates[i].Az, w, h, rawView);
            }

            // Union-find: two markers join a cluster if their discs overlap (plus a little
            // padding for comfortable clicking), transitively; so a short chain of moons all
            // near the same spot ends up as one cluster, not several partially-overlapping pairs.
            int[] parent = new int[candidates.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

            const float ClickPadding = 6f;
            for (int i = 0; i < candidates.Count; i++)
            {
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    float threshold = candidates[i].Radius + candidates[j].Radius + ClickPadding;
                    if ((rawPos[i] - rawPos[j]).sqrMagnitude < threshold * threshold)
                    {
                        Union(i, j);
                    }
                }
            }

            var clusters = new Dictionary<int, List<int>>();
            for (int i = 0; i < candidates.Count; i++)
            {
                int root = Find(i);
                if (!clusters.TryGetValue(root, out var list)) clusters[root] = list = new List<int>();
                list.Add(i);
            }

            const float MinAdjacentSpacingPx = 18f; // comfortably more than 2x the sky chart's baseline 8px hit radius
            const float MaxDeclutterRadiusPx = 40f; // caps how far a huge cluster (e.g. Jool + 5 moons) can sprawl
            foreach (var cluster in clusters.Values)
            {
                if (cluster.Count < 2) continue;
                // Deterministic order (not insertion order) so the ring's arrangement doesn't
                // visibly shuffle between chart refreshes.
                cluster.Sort((a, b) => string.CompareOrdinal(candidates[a].Body.bodyName, candidates[b].Body.bodyName));

                Vector2 centroid = Vector2.zero;
                foreach (int idx in cluster) centroid += rawPos[idx];
                centroid /= cluster.Count;

                float ringRadius = Mathf.Min(MaxDeclutterRadiusPx,
                    MinAdjacentSpacingPx / (2f * Mathf.Sin(Mathf.PI / cluster.Count)));

                for (int k = 0; k < cluster.Count; k++)
                {
                    float angle = 2f * Mathf.PI * k / cluster.Count;
                    Vector2 newRaw = centroid + ringRadius * new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                    SkyChartTexture.UnprojectRawPixel(newRaw.x, newRaw.y, w, h, out double newAlt, out double newAz);
                    newAlt = Math.Max(0.5, newAlt); // never let the nudge push a marker below the horizon line

                    int idx = cluster[k];
                    var c = candidates[idx];
                    candidates[idx] = (c.Body, newAlt, newAz, c.Radius, c.Color);
                }
            }
        }

        /// <summary>Marker radius in pixels for a body, from its real radius (log-compressed between the smallest moon and a gas giant), always bigger than any star marker so it reads as "a planet, not a star".</summary>
        static float BodyMarkerRadiusPx(CelestialBody body)
        {
            const double minR = 6_000.0;      // ~Gilly class
            const double maxR = 6_000_000.0;  // ~Jool class
            double r = Math.Max(1.0, body.Radius);
            double t = (Math.Log(r) - Math.Log(minR)) / (Math.Log(maxR) - Math.Log(minR));
            t = Math.Min(1.0, Math.Max(0.0, t));
            return Mathf.Lerp(3.0f, 15f, (float)t);
        }

        /// <summary>
        /// Real-ish body color by name (stock Kerbol system), a rough match to
        /// each body's actual albedo/surface color, so Duna reads rust-red, Jool
        /// green, Eve purple, etc., rather than every body being the same dot.
        /// Covers both the stock system and the real one (Real Solar System and any pack sharing
        /// those body names), matched by name rather than gated on which pack is installed; a
        /// name that isn't present simply never matches. Falls back to a neutral pale grey for
        /// anything in neither list (another Kopernicus pack's own bodies).
        /// </summary>
        static Color BodyMarkerColor(CelestialBody body)
        {
            switch (body.bodyName)
            {
                case "Moho": return new Color(0.62f, 0.55f, 0.50f, 1f);
                case "Eve": return new Color(0.62f, 0.35f, 0.70f, 1f);
                case "Gilly": return new Color(0.65f, 0.60f, 0.55f, 1f);
                case "Mun": return new Color(0.72f, 0.72f, 0.70f, 1f);
                case "Minmus": return new Color(0.80f, 0.90f, 0.86f, 1f);
                case "Duna": return new Color(0.85f, 0.45f, 0.30f, 1f);
                case "Ike": return new Color(0.58f, 0.56f, 0.55f, 1f);
                case "Dres": return new Color(0.68f, 0.58f, 0.45f, 1f);
                case "Jool": return new Color(0.40f, 0.75f, 0.35f, 1f);
                case "Laythe": return new Color(0.35f, 0.55f, 0.80f, 1f);
                case "Vall": return new Color(0.78f, 0.85f, 0.90f, 1f);
                case "Tylo": return new Color(0.70f, 0.68f, 0.65f, 1f);
                case "Bop": return new Color(0.60f, 0.50f, 0.40f, 1f);
                case "Pol": return new Color(0.78f, 0.68f, 0.50f, 1f);
                case "Eeloo": return new Color(0.88f, 0.90f, 0.92f, 1f);
                case "Sun": case "Kerbol": return new Color(1f, 0.92f, 0.55f, 1f);

                // Real solar system (Real Solar System and any pack using the same body names).
                // Approximate visual colours, matched to how each body actually looks to the eye
                // through a telescope rather than to enhanced-colour spacecraft imagery.
                case "Mercury": return new Color(0.66f, 0.62f, 0.58f, 1f);
                case "Venus": return new Color(0.95f, 0.90f, 0.75f, 1f);
                case "Earth": return new Color(0.36f, 0.52f, 0.72f, 1f);
                case "Moon": return new Color(0.78f, 0.76f, 0.72f, 1f);
                case "Mars": return new Color(0.80f, 0.44f, 0.28f, 1f);
                case "Phobos": case "Deimos": return new Color(0.52f, 0.47f, 0.43f, 1f);
                case "Ceres": return new Color(0.62f, 0.60f, 0.58f, 1f);
                case "Jupiter": return new Color(0.83f, 0.72f, 0.58f, 1f);
                case "Io": return new Color(0.88f, 0.80f, 0.42f, 1f);
                case "Europa": return new Color(0.86f, 0.82f, 0.75f, 1f);
                case "Ganymede": return new Color(0.68f, 0.63f, 0.57f, 1f);
                case "Callisto": return new Color(0.52f, 0.48f, 0.45f, 1f);
                case "Saturn": return new Color(0.90f, 0.82f, 0.63f, 1f);
                case "Titan": return new Color(0.83f, 0.65f, 0.35f, 1f);
                case "Enceladus": return new Color(0.93f, 0.94f, 0.95f, 1f);
                case "Mimas": case "Tethys": case "Dione": case "Rhea": case "Iapetus":
                    return new Color(0.78f, 0.76f, 0.73f, 1f);
                case "Uranus": return new Color(0.62f, 0.83f, 0.86f, 1f);
                case "Titania": case "Oberon": case "Ariel": case "Umbriel": case "Miranda":
                    return new Color(0.62f, 0.60f, 0.58f, 1f);
                case "Neptune": return new Color(0.35f, 0.48f, 0.82f, 1f);
                case "Triton": return new Color(0.80f, 0.76f, 0.74f, 1f);
                case "Pluto": return new Color(0.78f, 0.68f, 0.58f, 1f);
                case "Charon": return new Color(0.65f, 0.63f, 0.62f, 1f);

                default: return new Color(0.8f, 0.8f, 0.8f, 1f);
            }
        }

        /// <summary>
        /// Whether the RC20 can start an exposure on the currently selected
        /// body right now: night, and the body above the horizon. Shared by
        /// the Capture button's enabled state and the stacking batch driver
        /// in Update() (which needs the same check outside of GUI draw code
        /// to know whether to chain into the next sub of a batch).
        /// </summary>
        bool CanExposePhotography()
        {
            if (!selectedPhotographyTarget.HasTarget) return false;
            var conditions = ImagingObservingConditions.Evaluate(
                Planetarium.GetUniversalTime(), null, null, BuildImagingObserverContext());
            TryComputeTargetAltAz(selectedPhotographyTarget, out double targetAlt, out _);
            return conditions.IsNight && targetAlt > 0.0;
        }

        /// <summary>
        /// Timed-exposure capture for the amateur astrograph. A robotic scope,
        /// so there is NO live preview: you commit the settings (zoom, exposure,
        /// ISO, filter, focus, guiding), press Capture, and the finished frame
        /// appears once the exposure time has really elapsed.
        /// </summary>
        void DrawSolarSystemCameraView()
        {
            if (!selectedPhotographyTarget.HasTarget)
            {
                GUILayout.Label("Pick a body or a star on the sky chart at left, or enter coordinates, to point the telescope.");
                return;
            }

            GUILayout.Label($"<b>{selectedPhotographyTarget.DisplayName}</b>", plotTitleStyle);

            // Observability strip: always visible.
            DrawPhotographyObservability();

            if (!solarSystemCamera.IsAvailable)
            {
                GUILayout.Label("Amateur astrograph camera unavailable on this install (KSP's own scaled-space camera wasn't found).", smallCaptionStyle);
                return;
            }

            // The frame area: the finished photo, or a placeholder while exposing
            // / before the first capture. No live feed. Displayed at a fixed on-screen size
            // regardless of the camera's actual (possibly multi-megapixel) native resolution;
            // GUI.DrawTexture scales to the target Rect, so this never tries to lay out an
            // IMGUI window at native sensor size.
            Rect rect = GUILayoutUtility.GetRect(
                PreviewDisplaySize, PreviewDisplaySize,
                GUILayout.Width(PreviewDisplaySize), GUILayout.Height(PreviewDisplaySize));
            Color prevBg = GUI.color;
            GUI.color = Color.black;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = prevBg;
            if (solarSystemCamera.HasCapturedPhoto && solarSystemCamera.CapturedPhoto != null)
            {
                // The real sensor is non-square (4144x2822); draw at its true aspect ratio,
                // letterboxed within the fixed preview box, instead of stretching to fill it.
                GUI.DrawTexture(AspectFitRect(rect), solarSystemCamera.CapturedPhoto);
            }

            if (solarSystemCamera.IsCapturing)
            {
                var barBg = new Rect(rect.x + 20, rect.center.y - 7, rect.width - 40, 14);
                Color prev = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.15f);
                GUI.DrawTexture(barBg, Texture2D.whiteTexture);
                GUI.color = new Color(0.4f, 0.8f, 1f, 0.95f);
                GUI.DrawTexture(new Rect(barBg.x, barBg.y, barBg.width * solarSystemCamera.CaptureProgress, barBg.height), Texture2D.whiteTexture);
                GUI.color = prev;
                GUI.Label(new Rect(rect.x, rect.center.y - 34, rect.width, 20),
                    $"Exposing... {solarSystemCamera.CaptureProgress * 100f:F0}%", plotTitleStyle);
            }
            else if (solarSystemCamera.IsProcessing)
            {
                GUI.Label(new Rect(rect.x, rect.center.y - 10, rect.width, 20),
                    "Processing exposure (real sensor resolution takes a moment)...", plotTitleStyle);
            }
            else if (solarSystemCamera.LastProcessingError != null)
            {
                GUI.Label(new Rect(rect.x, rect.center.y - 10, rect.width, 40),
                    "Capture failed: " + solarSystemCamera.LastProcessingError, plotTitleStyle);
            }
            else if (!solarSystemCamera.HasCapturedPhoto)
            {
                GUI.Label(new Rect(rect.x, rect.center.y - 10, rect.width, 20),
                    "No frame yet; set up and press Capture.", plotTitleStyle);
            }

            DrawResolutionControls();
            DrawCameraControls(CanExposePhotography());
            DrawStackingControls(CanExposePhotography());
        }

        // Fixed on-screen preview size, independent of the camera's real, possibly much
        // larger, native sensor resolution (see SolarSystemCameraTexture.BinningFactor).
        private const int PreviewDisplaySize = 480;

        /// <summary>Centers a sub-rect matching the real sensor's aspect ratio (4144x2822, non-square) inside a bounding box, so the image letterboxes instead of stretching.</summary>
        static Rect AspectFitRect(Rect bounds)
        {
            float aspect = (float)SolarSystemCameraTexture.TextureWidth / SolarSystemCameraTexture.TextureHeight;
            float w = bounds.width;
            float h = w / aspect;
            if (h > bounds.height)
            {
                h = bounds.height;
                w = h * aspect;
            }
            return new Rect(bounds.x + (bounds.width - w) / 2f, bounds.y + (bounds.height - h) / 2f, w, h);
        }

        /// <summary>Real sensor binning selector (1x1 native resolution down to 4x4), the real trade-off amateur/professional acquisition software (SharpCap, NINA) exposes for exactly this resolution-vs-processing-cost problem.</summary>
        void DrawResolutionControls()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Binning:", GUILayout.Width(60));
            int current = SolarSystemCameraTexture.BinningFactor;
            foreach (int factor in new[] { 1, 2, 3, 4 })
            {
                bool selected = current == factor;
                GUI.enabled = !solarSystemCamera.IsCapturing && !solarSystemCamera.IsProcessing;
                if (GUILayout.Toggle(selected, $" {factor}x{factor}", GUILayout.Width(60)) && !selected)
                {
                    SolarSystemCameraTexture.BinningFactor = factor;
                }
                GUI.enabled = true;
            }
            int w = SolarSystemCameraTexture.TextureWidth, h = SolarSystemCameraTexture.TextureHeight;
            double captureMb = SolarSystemCameraTexture.EstimatedCaptureMemoryBytes / (1024.0 * 1024.0);
            GUILayout.Label($"({w}x{h}, {(double)w * h / 1e6:F1} Mpx, ~{captureMb:F0} MB)", smallCaptionStyle);
            GUILayout.EndHorizontal();

            // The cost is quartic in the binning factor and nothing used to say so. It matters more
            // than an ordinary performance note because the failure is not graceful: a managed
            // out-of-memory is caught and reported, but a native allocation failure at this size
            // closes the game outright, with no exception and no log entry.
            if (SolarSystemCameraTexture.EstimatedCaptureMemoryBytes > SolarSystemCameraTexture.CaptureMemoryWarningBytes)
            {
                GUILayout.Label(
                    $"WARNING: one capture at this binning needs about {captureMb:F0} MB across the heap and the "
                    + "graphics device, on top of what KSP already holds. If the machine cannot find it, the game "
                    + "closes instantly rather than reporting an error. Bin down unless you know you have the room.",
                    smallCaptionStyle);
            }

            if (solarSystemCamera.RenderTargetRefused)
            {
                GUILayout.Label(
                    $"The graphics device refused a {w}x{h} render target; captures at this binning will be garbage. "
                    + "Select a higher binning factor.", smallCaptionStyle);
            }

            // The one failure mode that looks like an ordinary under-exposure: the physics computed
            // a real electron count for the target, and the render drew none of it, so the frame
            // comes out with its sky, noise and stars but no body. Reported here because no amount
            // of looking at the picture can distinguish it from a frame that was simply too faint.
            if (solarSystemCamera.LastTargetElectrons > 0.0 && solarSystemCamera.LastRenderedLuminanceSum <= 1e-6)
            {
                GUILayout.Label(
                    $"Last capture: the physics computed {solarSystemCamera.LastTargetElectrons:E2} electrons from the "
                    + $"target, but the scene render came back empty ({w}x{h}). The target is ABSENT from that frame; "
                    + "this is a rendering failure, not an exposure problem. Try a higher binning factor.",
                    smallCaptionStyle);
            }
            else if (solarSystemCamera.LastRenderedLuminanceSum > 0.0)
            {
                GUILayout.Label(
                    $"Last capture: {solarSystemCamera.LastTargetElectrons:E2} e- from the target, rendered luminance sum "
                    + $"{solarSystemCamera.LastRenderedLuminanceSum:E2}.", smallCaptionStyle);
            }

            // Off by default: this is for attributing a bad frame, not part of normal use.
            // Every stage of the signal plane, written as it is built. Eliminating stages by
            // reasoning has a poor record on this pipeline; a dump names the stage from one
            // exposure instead.
            bool wasDumping = SolarSystemCameraTexture.StageDumpDirectory != null;
            bool wantDump = GUILayout.Toggle(wasDumping,
                " Diagnostics: dump every pipeline stage to Screenshots/ExoInstruments/stages/ (one frame-sized file per stage)");
            if (wantDump != wasDumping)
                SolarSystemCameraTexture.StageDumpDirectory = wantDump
                    ? KSPUtil.ApplicationRootPath + "Screenshots/ExoInstruments/stages"
                    : null;

            saveDiagnosticFrames = GUILayout.Toggle(saveDiagnosticFrames,
                " Diagnostics: also save the raw render on Save Photo (attributes a bad frame to the game's rendering vs. this mod's pipeline)");

            DrawDisplayStretchControls();
            DrawResolvingPowerDiagnostic();
        }

        /// <summary>
        /// Display transfer function selector. This is a VIEWER control, not an instrument one;
        /// the saved FITS and everything the stacker consumes stay linear no matter what is
        /// picked here, exactly as in a real observing tool.
        /// </summary>
        void DrawDisplayStretchControls()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Display stretch:", GUILayout.Width(110));
            var current = SolarSystemCameraTexture.Stretch;
            foreach (DisplayStretch mode in new[] { DisplayStretch.Linear, DisplayStretch.Log, DisplayStretch.Asinh })
            {
                bool selected = current == mode;
                if (GUILayout.Toggle(selected, " " + StretchLabel(mode), GUILayout.Width(76)) && !selected)
                {
                    SolarSystemCameraTexture.Stretch = mode;
                    // Restretch the frame already on screen rather than making the player re-expose.
                    solarSystemCamera.UploadDisplayTextures();
                }
            }
            bool autoScale = SolarSystemCameraTexture.AutoScaleDisplay;
            if (GUILayout.Toggle(autoScale, " Auto black/white points (zscale, as DS9 and IRAF do it)") != autoScale)
            {
                SolarSystemCameraTexture.AutoScaleDisplay = !autoScale;
                solarSystemCamera.UploadDisplayTextures();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(StretchDescription(SolarSystemCameraTexture.Stretch), smallCaptionStyle);

            if (SolarSystemCameraTexture.AutoScaleDisplay && solarSystemCamera.HasCapturedPhoto)
            {
                double black = solarSystemCamera.LastDisplayBlackPoint;
                double white = solarSystemCamera.LastDisplayWhitePoint;
                double wellFraction = white - black;
                GUILayout.Label(
                    $"   showing {black * 100.0:F3}% to {white * 100.0:F3}% of full scale"
                    + (wellFraction > 0.0 ? $", i.e. a {1.0 / wellFraction:F0}x stretch of the range the frame occupies" : ""),
                    smallCaptionStyle);
            }
        }

        static string StretchLabel(DisplayStretch mode)
        {
            switch (mode)
            {
                case DisplayStretch.Log:   return "Log";
                case DisplayStretch.Asinh: return "Asinh";
                default:                   return "Linear";
            }
        }

        static string StretchDescription(DisplayStretch mode)
        {
            switch (mode)
            {
                case DisplayStretch.Log:
                    return "Logarithmic (DS9 formulation, a=1000): strongest lift of faint detail, compresses the bright end hard. Viewer only; the FITS stays linear.";
                case DisplayStretch.Asinh:
                    return "Asinh (Lupton et al. 2004, the SDSS standard): linear near zero, logarithmic beyond, so faint surface detail lifts without crushing bright regions. Viewer only; the FITS stays linear.";
                default:
                    return "Linear: the detector's raw signal. Faithful, but a bright resolved disk hides its own few-percent surface contrast in a handful of display levels.";
            }
        }

        /// <summary>
        /// The three numbers that decide whether surface detail is resolvable at all, and which
        /// stage is the binding constraint: the real plate scale, how many pixels the target's
        /// disk actually spans at it, and how much blur the optical model is adding on top.
        /// Real acquisition/planning tools (SharpCap, NINA, ESO's ETCs) surface exactly this,
        /// and without it a soft frame is indistinguishable from a correctly-modelled one;
        /// the player can't tell "too few pixels on the disk" from "the model is over-blurring"
        /// from "the game's own texture ran out of detail", which are three different problems
        /// with three different answers.
        /// </summary>
        /// <summary>Angular size in whichever unit reads naturally at that scale, milliarcsec below 0.1", arcsec above.</summary>
        static string Arcsec(double arcsec)
            => arcsec < 0.1 ? $"{arcsec * 1000.0:F1} mas" : $"{arcsec:F2}\"";

        void DrawResolvingPowerDiagnostic()
        {
            if (!selectedPhotographyTarget.HasTarget) return;

            float plateScale = SolarSystemCameraTexture.PlateScaleArcsecPerPixel;
            if (plateScale <= 0f) return;

            // A star or a nebula has no resolvable disk, so the disk terms below drop out, but
            // everything else here (pointing, PSF, sky, extinction, line emission) is exactly what
            // those targets are photographed for. This used to return early unless the target was a
            // solar-system body, which hid every one of those readouts on the only targets they
            // were written for.
            bool hasDisk = selectedPhotographyTarget.IsBody;
            double diskArcsec = hasDisk
                ? SolarSystemCameraTexture.AngularDiameterArcsec(selectedPhotographyBody) : 0.0;
            double diskPx = diskArcsec / plateScale;
            float blurPx = solarSystemCamera.LastAppliedBlurRadiusPx;

            string scale = plateScale < 0.1f
                ? $"{plateScale * 1000f:F1} mas/px"
                : $"{plateScale:F3}\"/px";

            GUILayout.Label(hasDisk
                ? $"Plate scale {scale}  |  disk {diskArcsec:F2}\" = {diskPx:F0} px"
                : $"Plate scale {scale}  |  field {SolarSystemCameraTexture.TextureWidth * plateScale / 60.0:F1}' x "
                  + $"{SolarSystemCameraTexture.TextureHeight * plateScale / 60.0:F1}'",
                smallCaptionStyle);

            if (solarSystemCamera.HasCapturedPhoto)
            {
                // What actually limits the frame, split into the two terms that compete for it:
                // the telescope's own diffraction (which no observing condition can beat) and
                // whatever the atmosphere still contributes after adaptive optics, if any.
                double diff = solarSystemCamera.LastDiffractionFwhmArcsec;
                double atm = solarSystemCamera.LastAtmosphericFwhmArcsec;
                double delivered = Math.Sqrt(diff * diff + atm * atm);

                GUILayout.Label(
                    $"PSF: diffraction {Arcsec(diff)} + atmosphere {Arcsec(atm)} = {Arcsec(delivered)} delivered "
                    + $"({delivered / plateScale:F1} px"
                    + (diskPx > 0.0 ? $", {delivered / Math.Max(1e-9, diskArcsec) * 100.0:F1}% of disk)" : ")")
                    + $"  |  kernel {2f * blurPx + 1f:F0} px  |  saturated {solarSystemCamera.LastSaturatedFraction * 100f:F1}%",
                    smallCaptionStyle);

                // Atmospheric dispersion: the atmosphere refracts blue more than red, so a source at
                // low altitude is drawn out into a short spectrum pointing at the zenith. This is the
                // length of it across the active filter, which is the number that says whether the
                // frame's stars are points.
                double smear = solarSystemCamera.LastDispersionSmearArcsec;
                if (smear > 0.0)
                {
                    double smearPx = smear / plateScale;
                    GUILayout.Label(
                        $"Atmospheric dispersion: {smear:F3}\" across this filter at z = "
                        + $"{solarSystemCamera.LastZenithDistanceDeg:F0} deg = {smearPx:F1} px toward the zenith"
                        + (SolarSystemCameraTexture.Spec.HasAtmosphericDispersionCorrector
                            ? ", after the instrument's dispersion corrector" : ""),
                        smallCaptionStyle);
                }

                // A single per-exposure draw applied to the whole target, so it is the reason two
                // otherwise identical captures differ in brightness. It can never be negative;
                // if it ever reads below zero, the running build predates that fix.
                float sc = solarSystemCamera.LastScintillationFactor;
                GUILayout.Label(
                    $"Scintillation this frame: x{sc:F3} (sigma {solarSystemCamera.LastScintillationSigma:F2})"
                    + (sc < 0f ? "  <-- NEGATIVE: stale build, restart KSP" : ""),
                    smallCaptionStyle);

                // The three numbers that describe the sky the subject was photographed against:
                // how dark it was, how faint this exposure could reach, and what it actually
                // caught. All are quantities a real observer records with the frame.
                double skyMag = solarSystemCamera.LastSkyBrightnessVMagPerArcsec2;
                string sky = double.IsPositiveInfinity(skyMag) ? "-" : $"{skyMag:F2} mag/arcsec²";
                GUILayout.Label(
                    $"Sky {sky}  |  limiting magnitude V {solarSystemCamera.LastLimitingVMag:F1}"
                    + $"  |  {solarSystemCamera.LastStarsDrawn} catalog stars in frame",
                    smallCaptionStyle);

                // Where the target actually landed. The aim and the star field share one geometry,
                // so this should read a fraction of a pixel; anything else is a pointing fault and
                // this says which way it went.
                double offset = solarSystemCamera.LastTargetOffsetArcsec;
                double fieldArcsec = solarSystemCamera.LastFieldWidthArcsec;
                if (!double.IsNaN(offset))
                {
                    string field = fieldArcsec < 120.0
                        ? $"{fieldArcsec:F1}\""
                        : $"{fieldArcsec / 60.0:F1}'";
                    GUILayout.Label(
                        $"Pointing: target at ({solarSystemCamera.LastTargetPixelX:F0}, "
                        + $"{solarSystemCamera.LastTargetPixelY:F0}) px, {offset:F2}\" from centre, "
                        + $"field {field} wide"
                        + (solarSystemCamera.LastTargetInFrame ? "" : "  <-- OUTSIDE THE FRAME"),
                        smallCaptionStyle);
                }
                else if (fieldArcsec > 0.0)
                {
                    GUILayout.Label($"Pointing: target did not project into the field "
                                  + $"(field {fieldArcsec / 60.0:F1}' wide)", smallCaptionStyle);
                }

                // What the sky itself emits into this filter, the number a nebula's surface
                // brightness competes against, and the reason [O I] is not imaged from the ground.
                double airglow = solarSystemCamera.LastAirglowRayleighsInBand;
                if (airglow > 0.0)
                {
                    GUILayout.Label(
                        $"Airglow in this band: {airglow:F1} R "
                        + $"({solarSystemCamera.LastAirglowLineShare * 100.0:F0}% sky emission lines, "
                        + "ESO SkyCalc / Hanuschik 2003)",
                        smallCaptionStyle);
                }

                double emission = solarSystemCamera.LastEmissionRayleighs;
                if (!double.IsNaN(emission))
                {
                    double peak = solarSystemCamera.LastEmissionPeakElectrons;
                    double well = SolarSystemCameraTexture.FullWellElectrons;
                    GUILayout.Label(
                        $"Diffuse line emission in this filter: {emission:F1} R mean over the field"
                        + (solarSystemCamera.LastEmissionLines != null
                            ? $" from {solarSystemCamera.LastEmissionLines}" : "")
                        + (double.IsNaN(solarSystemCamera.LastEmissionTemperatureK) ? ""
                            : $", forbidden-line ratios at T_e = {solarSystemCamera.LastEmissionTemperatureK:F0} K"),
                        smallCaptionStyle);

                    // Which layer answered, and at what beam. This is the number that decides
                    // whether the frame can show structure at all, so it is on the frame.
                    double beam = solarSystemCamera.LastEmissionResolutionArcmin;
                    if (beam > 0.0)
                    {
                        GUILayout.Label(
                            solarSystemCamera.LastEmissionPatchName != null
                                ? $"   {solarSystemCamera.LastEmissionPatchCoverage * 100.0:F0}% of the frame from the "
                                  + $"{solarSystemCamera.LastEmissionPatchName} patch at {beam:F2}' sampling, "
                                  + "the rest from the all-sky map"
                                : $"   from the all-sky map at {beam:F2}' sampling (6' beam); no patch covers this field",
                            smallCaptionStyle);
                    }
                    if (!double.IsNaN(peak) && well > 0.0)
                    {
                        // The number that answers "why can I not see it": a nebula this bright is a
                        // fraction of a percent of full well in one sub, which no linear stretch can
                        // show. Stacking and a log or asinh stretch is not a display trick, it is
                        // the technique the exposure requires.
                        GUILayout.Label(
                            $"   brightest pixel of it: {peak:F1} e- = {100.0 * peak / well:F3}% of full well"
                            + (peak < 0.01 * well ? ", below what a linear stretch can show; use log/asinh and stack" : ""),
                            smallCaptionStyle);
                    }
                }

                if (solarSystemCamera.LastGalaxiesDrawn > 0)
                {
                    double well = SolarSystemCameraTexture.FullWellElectrons;
                    GUILayout.Label(
                        $"Galaxies in frame: {solarSystemCamera.LastGalaxiesDrawn} drawn, "
                        + $"{solarSystemCamera.LastGalaxyElectrons:E2} e- between them"
                        + (solarSystemCamera.LastGalaxiesWithModelledColour > 0
                            ? $" ({solarSystemCamera.LastGalaxiesWithModelledColour} with no catalogued colour, "
                              + "band conversion from the mean colour of their type)"
                            : ""),
                        smallCaptionStyle);
                }

                double fieldEbv = solarSystemCamera.LastFieldReddeningEBv;
                if (!double.IsNaN(fieldEbv))
                {
                    GUILayout.Label(
                        $"Galactic extinction toward this field: E(B-V) = {fieldEbv:F3}, "
                        + $"A(V) = {fieldEbv * InterstellarExtinction.MilkyWayRv:F2} mag "
                        + "(whole column, so it applies beyond the Galaxy rather than to stars in frame)",
                        smallCaptionStyle);
                }
            }

            // Saturation is the one failure here that silently destroys real detail rather than
            // just softening it, and on a large-aperture instrument it is the DEFAULT outcome on a
            // bright disk, worth calling out explicitly instead of leaving it as a percentage
            // the player has to know how to interpret.
            if (solarSystemCamera.HasCapturedPhoto && solarSystemCamera.LastSaturatedFraction > 0.01f)
            {
                GUILayout.Label(
                    "Over-exposed: saturated pixels have lost their real surface contrast. "
                    + "Shorten the exposure, drop the gain, or add ND, exactly what a real observer does on a bright target.",
                    smallCaptionStyle);
            }
        }

        /// <summary>Zoom / exposure / ISO / filter / focus / guiding controls + the Capture and Save buttons.</summary>
        void DrawCameraControls(bool canExpose)
        {
            GUILayout.BeginVertical(GUI.skin.box);

            // Zoom and filter are locked for the duration of a stacking batch
            // (see DrawStackingControls / the Update() batch driver): every
            // sub of a batch must share the same FOV and filter, or the stack
            // it feeds would be garbage.
            bool stackBatchRunning = stackBatchRemaining > 0;

            // Zoom (FOV): left = wide, right = tight. Smaller FOV = more zoom.
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Zoom (FOV {FormatFov(solarSystemCamera.FovDeg)})", GUILayout.Width(150));
            GUI.enabled = !stackBatchRunning;
            float invFov = SolarSystemCameraTexture.MaxFovDeg + SolarSystemCameraTexture.MinFovDeg - solarSystemCamera.FovDeg;
            invFov = GUILayout.HorizontalSlider(invFov, SolarSystemCameraTexture.MinFovDeg, SolarSystemCameraTexture.MaxFovDeg);
            solarSystemCamera.FovDeg = SolarSystemCameraTexture.MaxFovDeg + SolarSystemCameraTexture.MinFovDeg - invFov;
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // Exposure time. Real range spans 32us to 2000s (six decades); a linear slider
            // can't usefully address that, so drag position maps to log10(seconds), the same
            // convention real acquisition tools (SharpCap, FireCapture) use for this exact reason.
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Exposure ({FormatExposure(solarSystemCamera.ExposureSeconds)})", GUILayout.Width(150));
            float minLog = Mathf.Log10(SolarSystemCameraTexture.MinExposureSeconds);
            float maxLog = Mathf.Log10(SolarSystemCameraTexture.MaxExposureSeconds);
            float expLog = GUILayout.HorizontalSlider(Mathf.Log10(solarSystemCamera.ExposureSeconds), minLog, maxLog);
            solarSystemCamera.ExposureSeconds = Mathf.Pow(10f, expLog);
            GUILayout.EndHorizontal();

            // Gain: continuous slider. 0.7 is the RC20's real minimum gain.
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Gain ({solarSystemCamera.Gain:F2}x)", GUILayout.Width(150));
            solarSystemCamera.Gain = GUILayout.HorizontalSlider(
                solarSystemCamera.Gain,
                SolarSystemCameraTexture.MinGain, SolarSystemCameraTexture.MaxGain);
            GUILayout.EndHorizontal();

            // Cooler setpoint. Only offered where the observer really has the control: the ZWO
            // camera's thermoelectric cooler is a dial, while FORS2's and SPHERE's cryogenic
            // detectors sit at a fixed temperature nobody at a VLT is invited to change.
            //
            // This is a real physical control, not a readout. Dark current follows it through the
            // depletion-generation law (Core.DarkCurrentModel), hot pixels follow with it because
            // they are dark current, and CCD-TEMP in the exported header reports what was used.
            GUILayout.BeginHorizontal();
            if (SolarSystemCameraTexture.HasAdjustableCooler)
            {
                double setpoint = SolarSystemCameraTexture.DetectorTemperatureCelsius;
                double reference = SolarSystemCameraTexture.ActiveTelescope.DetectorTemperatureCelsius;
                double darkNow = Core.DarkCurrentModel.ElectronsPerSecond(
                    SolarSystemCameraTexture.ActiveTelescope.DarkCurrentElectronsPerSecond, reference, setpoint);

                GUILayout.Label($"Cooler ({setpoint:F0} C)", GUILayout.Width(150));
                float newSetpoint = GUILayout.HorizontalSlider(
                    (float)setpoint,
                    (float)SolarSystemCameraTexture.CoolerMinimumTemperatureCelsius,
                    (float)SolarSystemCameraTexture.CoolerMaximumTemperatureCelsius);
                if (Mathf.Abs(newSetpoint - (float)setpoint) > 0.01f)
                    SolarSystemCameraTexture.DetectorTemperatureCelsius = newSetpoint;
                GUILayout.Label($"{darkNow:G3} e-/px/s", GUILayout.Width(110));
            }
            else
            {
                double fixedTemp = SolarSystemCameraTexture.ActiveTelescope.DetectorTemperatureCelsius;
                GUILayout.Label("Cooler", GUILayout.Width(150));
                GUILayout.Label(
                    double.IsNaN(fixedTemp)
                        ? "not modelled for this instrument, no published operating temperature"
                        : $"fixed at {fixedTemp:F0} C, cryogenic detector, not an observer control",
                    smallCaptionStyle);
            }
            GUILayout.EndHorizontal();

            // Filter wheel, only the active telescope's real filters (VisualTelescopeSpec.
            // AvailableFilters). Most instruments have all five; one that genuinely lacks a
            // filter (e.g. SPHERE/ZIMPOL has no real blue filter) simply doesn't offer it here,
            // rather than faking a channel that doesn't exist on the real hardware.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Filter", GUILayout.Width(150));
            GUI.enabled = !stackBatchRunning;
            foreach (CameraFilter f in SolarSystemCameraTexture.ActiveTelescope.AvailableFilters)
            {
                bool sel = solarSystemCamera.Filter == f;
                if (GUILayout.Toggle(sel, " " + FilterLabel(f), GUILayout.Width(58)) && !sel)
                    solarSystemCamera.Filter = f;
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // ND filter: real optical-density stops for targets too bright for exposure/gain
            // alone; Kerbin's compressed-scale system puts nearby moons in that regime.
            GUILayout.BeginHorizontal();
            GUILayout.Label("ND filter", GUILayout.Width(150));
            GUI.enabled = !stackBatchRunning;
            foreach (NdFilterStop stop in (NdFilterStop[])Enum.GetValues(typeof(NdFilterStop)))
            {
                bool sel = solarSystemCamera.NdFilter == stop;
                if (GUILayout.Toggle(sel, " " + NdFilterLabel(stop), GUILayout.Width(58)) && !sel)
                    solarSystemCamera.NdFilter = stop;
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // Focus: autofocus toggle + manual slider.
            GUILayout.BeginHorizontal();
            solarSystemCamera.Autofocus = GUILayout.Toggle(solarSystemCamera.Autofocus, " Autofocus", GUILayout.Width(120));
            GUI.enabled = !solarSystemCamera.Autofocus;
            GUILayout.Label("Focus", GUILayout.Width(45));
            solarSystemCamera.FocusOffset = GUILayout.HorizontalSlider(solarSystemCamera.FocusOffset, -1f, 1f);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // Autoguiding. Forced on and locked for an instrument with no real bare/unguided
            // mode (VisualTelescopeSpec.AlwaysAutoguided, e.g. the VLT); SetActiveTelescope
            // already flips the property itself; this just stops the player from toggling it
            // back off and reintroducing drift trailing a real 8.2m research telescope never has.
            bool autoguidingLocked = SolarSystemCameraTexture.AutoguidingForced;
            GUILayout.BeginHorizontal();
            GUI.enabled = !autoguidingLocked;
            solarSystemCamera.Autoguiding = GUILayout.Toggle(solarSystemCamera.Autoguiding,
                autoguidingLocked
                    ? " Autoguiding (always on; this instrument has no unguided mode)"
                    : " Autoguiding (tracks the sky; off = the target drifts between shots unless re-centered)");
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            // Manual re-center: only meaningful without autoguiding; with it
            // on, every capture already re-centers automatically.
            GUI.enabled = !solarSystemCamera.Autoguiding;
            if (GUILayout.Button("Update telescope target", GUILayout.Height(22), GUILayout.Width(180)))
                solarSystemCamera.UpdateAim(selectedPhotographyTarget);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (solarSystemCamera.IsCapturing)
            {
                if (GUILayout.Button("Cancel exposure", GUILayout.Height(28), GUILayout.Width(180)))
                    solarSystemCamera.CancelExposure();
            }
            else if (solarSystemCamera.IsProcessing)
            {
                GUI.enabled = false;
                GUILayout.Button("Processing...", GUILayout.Height(28), GUILayout.Width(180));
                GUI.enabled = true;
            }
            else
            {
                GUI.enabled = canExpose;
                if (GUILayout.Button($"Capture ({FormatExposure(solarSystemCamera.ExposureSeconds)})", GUILayout.Height(28), GUILayout.Width(180)))
                    solarSystemCamera.BeginExposure(selectedPhotographyTarget);
                GUI.enabled = true;
            }
            GUI.enabled = solarSystemCamera.HasCapturedPhoto;
            if (GUILayout.Button("Save Photo (.png + .fits)", GUILayout.Height(28), GUILayout.Width(180)))
                SaveSolarSystemPhoto();
            GUI.enabled = true;
            if (GUILayout.Button("Stop", GUILayout.Height(28), GUILayout.Width(90)))
            {
                photographySessionActive = false;
                solarSystemCamera.CancelExposure();
                stackBatchRemaining = 0; // stop a batch mid-flight too, but keep the subs already captured
            }
            GUILayout.EndHorizontal();

            // Calibration frames. Deliberately NOT gated on canExpose: the shutter stays closed, so
            // there is nothing to be night for and nothing to be above the horizon. That is the
            // point of them; a real observer takes darks with the dome shut, in daylight, whenever
            // the detector is at the temperature the lights were taken at.
            GUILayout.BeginHorizontal();
            GUI.enabled = !solarSystemCamera.IsCapturing && !solarSystemCamera.IsProcessing;
            if (GUILayout.Button("Save bias frame", GUILayout.Height(24), GUILayout.Width(180)))
                SaveCalibrationFrame(SolarSystemCameraTexture.CalibrationFrameType.Bias);
            if (GUILayout.Button($"Save dark ({FormatExposure(solarSystemCamera.ExposureSeconds)})", GUILayout.Height(24), GUILayout.Width(180)))
                SaveCalibrationFrame(SolarSystemCameraTexture.CalibrationFrameType.Dark);
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label(
                "A dark is only valid for lights of the same exposure, gain, binning and cooler setpoint.",
                smallCaptionStyle);
            if (!string.IsNullOrEmpty(lastCalibrationFrameMessage))
                GUILayout.Label(lastCalibrationFrameMessage, smallCaptionStyle);

            if (!canExpose)
            {
                GUILayout.Label("Can't expose right now: it must be night and the body above the horizon.", smallCaptionStyle);
            }
            GUILayout.EndVertical();
        }

        /// <summary>Status line under the calibration buttons: where the last frame went, or why it didn't.</summary>
        string lastCalibrationFrameMessage;

        /// <summary>
        /// Writes a shutter-closed calibration frame to disk as FITS.
        ///
        /// It goes through the same detector chain a science frame does (see
        /// SolarSystemCameraTexture.CaptureCalibrationFrameAdu), so subtracting it from a light of
        /// matching exposure, gain, binning and temperature removes exactly what it should: the
        /// pedestal, the dark current, and the hot pixels that are made of dark current.
        ///
        /// No WCS and no zero point: the shutter was shut, so the frame points nowhere and measures
        /// no flux. Writing either would describe a frame this is not, the same standard the
        /// stacked composite is already held to.
        /// </summary>
        void SaveCalibrationFrame(SolarSystemCameraTexture.CalibrationFrameType type)
        {
            float exposureUsed;
            double biasLevel;
            float[] adu = solarSystemCamera.CaptureCalibrationFrameAdu(
                type, solarSystemCamera.ExposureSeconds, out exposureUsed, out biasLevel);

            if (adu == null)
            {
                lastCalibrationFrameMessage = "Calibration frame unavailable: the camera has not been built yet.";
                return;
            }

            string dir = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "Screenshots");
            System.IO.Directory.CreateDirectory(dir);
            string stamp = $"{DateTime.Now:yyyyMMdd_HHmmss}";
            bool isDark = type == SolarSystemCameraTexture.CalibrationFrameType.Dark;
            string kind = isDark ? "dark" : "bias";

            var fitsInfo = new FitsWriter.FitsHeaderInfo
            {
                ExposureSeconds = exposureUsed,
                PixelSizeMicrons = SolarSystemCameraTexture.PixelSizeMicrons,
                FullWellElectrons = SolarSystemCameraTexture.FullWellElectrons,
                AdcBits = SolarSystemCameraTexture.ActiveTelescope.AdcBits,
                ElectronsPerAdu = solarSystemCamera.LastCalibrationElectronsPerAdu,
                SaturationAdu = solarSystemCamera.LastCalibrationSaturationElectrons
                                / Math.Max(1e-9, solarSystemCamera.LastCalibrationElectronsPerAdu),
                FocalLengthMm = SolarSystemCameraTexture.FocalLengthMm,
                Gain = solarSystemCamera.Gain,
                IsCalibratedAdu = true,   // genuinely raw converter counts, which is the whole point
                FilterName = FilterLabel(solarSystemCamera.Filter),
                ObjectName = kind.ToUpperInvariant(),
                UtcTimestamp = DateTime.UtcNow,
                ImageType = isDark ? "Dark Frame" : "Bias Frame",
            };
            FillCommonFitsMetadata(ref fitsInfo);

            // Overwrite the light-frame values FillCommonFitsMetadata copied from the last science
            // capture: this frame has its own seed and its own conversion, and no zero point at all.
            fitsInfo.RandomSeed = solarSystemCamera.LastCalibrationSeed;
            fitsInfo.BiasLevelAdu = biasLevel;
            fitsInfo.PhotometricZeroPoint = double.NaN;
            fitsInfo.DarkCurrentElectronsPerSecond = solarSystemCamera.LastCalibrationDarkPerSecond;
            fitsInfo.Airmass = double.NaN;
            fitsInfo.SeeingFwhmArcsec = double.NaN;
            fitsInfo.SkyBrightnessVMagPerArcsec2 = double.NaN;
            fitsInfo.GalacticReddeningEBv = double.NaN;
            fitsInfo.LineSurfaceBrightnessRayleighs = double.NaN;

            string path = System.IO.Path.Combine(dir, $"ExoInstruments_{kind}_{stamp}.fits");
            FitsWriter.WriteGrayscale(path, adu,
                SolarSystemCameraTexture.TextureWidth, SolarSystemCameraTexture.TextureHeight, fitsInfo);

            lastCalibrationFrameMessage = isDark
                ? $"Dark frame saved ({FormatExposure(exposureUsed)}, {SolarSystemCameraTexture.DetectorTemperatureCelsius:F0} C): {System.IO.Path.GetFileName(path)}"
                : $"Bias frame saved (0 s, {SolarSystemCameraTexture.DetectorTemperatureCelsius:F0} C): {System.IO.Path.GetFileName(path)}";
        }

        /// <summary>
        /// Real astrophotography stacking workflow: capture N subs on the
        /// current filter in one batch (see the Update() batch driver), stack
        /// each filter's subs (optionally aligned by brightness centroid),
        /// then compose a colour image through the instrument's own fitted
        /// colour transform, or a named narrowband palette; see
        /// Visualization/ColourComposite. Separate from the single-shot
        /// Capture/Save above, which is unaffected.
        /// </summary>
        void DrawStackingControls(bool canExpose)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<b>Stacking</b>", plotTitleStyle);

            bool batchRunning = stackBatchRemaining > 0;

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Subs per batch ({stackBatchSize})", GUILayout.Width(150));
            GUI.enabled = !batchRunning;
            stackBatchSize = Mathf.RoundToInt(GUILayout.HorizontalSlider(stackBatchSize, 1, 20));
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = !batchRunning && canExpose && !solarSystemCamera.IsCapturing && !solarSystemCamera.IsProcessing;
            if (GUILayout.Button($"Capture series ({FilterLabel(solarSystemCamera.Filter)}, {stackBatchSize})", GUILayout.Height(26), GUILayout.Width(220)))
            {
                stackBatchInterruptedMessage = null;
                stackBatchRemaining = stackBatchSize;
                stackBatchCollected = 0;
                stackBatchQueued = 1;
                solarSystemCamera.BeginExposure(selectedPhotographyTarget);
            }
            GUI.enabled = true;
            if (batchRunning && GUILayout.Button("Cancel series", GUILayout.Height(26), GUILayout.Width(120)))
            {
                stackBatchRemaining = 0;
                stackBatchQueued = 0;
                stackBatchCollected = 0;
            }
            GUILayout.EndHorizontal();

            if (batchRunning)
            {
                GUILayout.Label($"Capturing series... {stackBatchCollected}/{stackBatchSize}"
                              + (solarSystemCamera.IsProcessing && solarSystemCamera.IsCapturing
                                  ? "  (reducing the previous frame while this one integrates)" : ""),
                                smallCaptionStyle);
            }
            else if (!string.IsNullOrEmpty(stackBatchInterruptedMessage))
            {
                GUILayout.Label(stackBatchInterruptedMessage, smallCaptionStyle);
            }

            // Every channel the instrument's own wheel can shoot (so the narrowband lines, OIII
            // and SII, are listed on the same footing as L R G B Ha), plus any filter that
            // already holds subs from a telescope swapped out mid-session, which would otherwise
            // vanish from the readout while still sitting in the stack.
            var subsReadout = new System.Text.StringBuilder("Stacked subs: ");
            List<CameraFilter> readoutFilters = StackReadoutFilters();
            for (int i = 0; i < readoutFilters.Count; i++)
            {
                CameraFilter f = readoutFilters[i];
                if (i > 0) subsReadout.Append(" | ");
                subsReadout.Append($"{FilterLabel(f)} {astroStack.SubCount(f)} ({astroStack.TotalExposureSeconds(f):F1}s)");
            }
            GUILayout.Label(subsReadout.ToString(), smallCaptionStyle);

            GUILayout.BeginHorizontal();
            stackAlignSubs = GUILayout.Toggle(stackAlignSubs, " Align subs (brightness centroid)", GUILayout.Width(220));
            stackLuckyImaging = GUILayout.Toggle(stackLuckyImaging, " Lucky imaging (keep sharpest 30%)", GUILayout.Width(240));
            GUILayout.EndHorizontal();

            // Replaces the old "Ha blend strength" slider, which was an artist's knob with no
            // physical meaning: it added an arbitrary fraction of the H-alpha frame to the red
            // channel. Colour now comes from the instrument's own fitted transform (true colour) or
            // from a named palette convention; see Visualization/ColourComposite.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Composite:", GUILayout.Width(80));
            foreach (ColourCompositeMode m in new[] { ColourCompositeMode.TrueColour,
                                                     ColourCompositeMode.NarrowbandHoo,
                                                     ColourCompositeMode.NarrowbandSho })
            {
                bool selected = compositeMode == m;
                if (GUILayout.Toggle(selected, " " + CompositeLabel(m), GUILayout.Width(126)) && !selected)
                {
                    compositeMode = m;
                    if (stackedCompositeTexture != null) ComposeAstroStack();
                }
            }
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(compositeReport))
                GUILayout.Label(compositeReport, smallCaptionStyle);

            // How the subs were registered, and what it cost at the frame edge. Both matter: a
            // centroid registration on an extended object is meaningless, and a shifted stack has a
            // border fewer subs reached.
            if (stackAlignSubs && astroStack.HasAnySubs && stackedCompositeTexture != null)
            {
                GUILayout.Label(
                    (astroStack.LastAlignmentUsedPointing
                        ? $"Registered on the recorded pointing, worst shift {astroStack.LastAlignmentShiftPx:F1} px"
                        : "Registered on the brightness centroid (no pointing recorded), unreliable on an extended object")
                    + $"; every sub covers {astroStack.LastFullCoverageFraction * 100.0:F1}% of the frame",
                    smallCaptionStyle);
            }

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUI.enabled = astroStack.HasAnySubs;
            if (GUILayout.Button("Compose colour", GUILayout.Height(26), GUILayout.Width(150)))
                ComposeAstroStack();
            GUI.enabled = astroStack.HasAnySubs;
            if (GUILayout.Button("Export", GUILayout.Height(26), GUILayout.Width(150)))
                ExportStack();
            GUI.enabled = astroStack.HasAnySubs;
            if (GUILayout.Button("Clear stack", GUILayout.Height(26), GUILayout.Width(110)))
                ClearAstroStack();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // What "Export" writes. Three products, because they serve three different jobs: a
            // finished picture, one calibrated frame per band for an external stacker, and the
            // individual subs for a package that wants to do the registration itself.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Export:", GUILayout.Width(80));
            foreach (StackExportMode m in new[] { StackExportMode.Composite,
                                                 StackExportMode.PerFilter,
                                                 StackExportMode.EverySub })
            {
                bool selected = exportMode == m;
                if (GUILayout.Toggle(selected, " " + ExportLabel(m), GUILayout.Width(160)) && !selected)
                    exportMode = m;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(ExportDescription(exportMode), smallCaptionStyle);

            if (!string.IsNullOrEmpty(stackExportReport))
                GUILayout.Label(stackExportReport, smallCaptionStyle);

            if (!string.IsNullOrEmpty(stackComposeError))
            {
                GUILayout.Label(stackComposeError, smallCaptionStyle);
            }

            if (stackedCompositeTexture != null)
            {
                GUILayout.Space(6);
                GUILayout.Label("Composite preview:", smallCaptionStyle);
                Rect compositeRect = GUILayoutUtility.GetRect(
                    PreviewDisplaySize, PreviewDisplaySize,
                    GUILayout.Width(PreviewDisplaySize), GUILayout.Height(PreviewDisplaySize));
                GUI.DrawTexture(AspectFitRect(compositeRect), stackedCompositeTexture);
            }

            GUILayout.EndVertical();
        }

        static string CompositeLabel(ColourCompositeMode m)
        {
            switch (m)
            {
                case ColourCompositeMode.NarrowbandHoo: return "HOO";
                case ColourCompositeMode.NarrowbandSho: return "SHO (Hubble)";
                default: return "True colour";
            }
        }

        /// <summary>Runs the colour composite and refreshes (or builds) stackedCompositeTexture from the result.</summary>
        void ComposeAstroStack()
        {
            Color[] pixels = astroStack.Compose(compositeMode, stackAlignSubs, stackLuckyImaging,
                                                SolarSystemCameraTexture.Spec, out stackComposeError);
            compositeReport = stackComposeError;
            if (pixels == null) return;

            // Keep the full-precision composite for FITS export; stackedCompositeTexture
            // below is only for on-screen preview and round-trips through an 8-bit RGB24
            // Texture2D, which would otherwise crush the real sub-1/255 noise floor to nothing.
            lastComposedPixels = pixels;

            int w = SolarSystemCameraTexture.TextureWidth, h = SolarSystemCameraTexture.TextureHeight;
            if (stackedCompositeTexture == null || stackedCompositeTexture.width != w || stackedCompositeTexture.height != h)
            {
                if (stackedCompositeTexture != null) UnityEngine.Object.Destroy(stackedCompositeTexture);
                stackedCompositeTexture = new Texture2D(w, h, TextureFormat.RGB24, false);
            }
            stackedCompositeTexture.SetPixels(pixels);
            stackedCompositeTexture.Apply();
        }

        /// <summary>Writes the composed LRGB stack to KSP's screenshot folder as a PNG and a real 16-bit FITS file, same scheme as SaveSolarSystemPhoto.</summary>
        /// <summary>
        /// Writes the stack out in whichever of the three products the export selector asks for, into
        /// one timestamped folder per session.
        ///
        /// A folder rather than a pile of files in Screenshots: the per-sub product alone is forty
        /// files for a two-filter series, and the point of exporting is to hand a coherent dataset to
        /// something else. The name carries the target and the instrument so two sessions on the same
        /// object from different telescopes do not merge.
        /// </summary>
        void ExportStack()
        {
            if (!astroStack.HasAnySubs) { stackExportReport = "Nothing to export: capture a series first."; return; }

            string stamp = $"{DateTime.Now:yyyyMMdd_HHmmss}";
            string dir = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "Screenshots", "ExoInstruments",
                $"{TargetFileName()}_{SafeName(SolarSystemCameraTexture.ActiveTelescope.CameraName)}_{stamp}");
            System.IO.Directory.CreateDirectory(dir);

            int files;
            switch (exportMode)
            {
                case StackExportMode.PerFilter: files = ExportPerFilter(dir); break;
                case StackExportMode.EverySub: files = ExportEverySub(dir); break;
                default: files = ExportComposite(dir, stamp); break;
            }

            stackExportReport = files > 0
                ? $"Wrote {files} file{(files == 1 ? "" : "s")} to Screenshots/ExoInstruments/{System.IO.Path.GetFileName(dir)}"
                : "Nothing was written: see the log.";
            Debug.Log($"[ExoInstruments] Exported {files} files to {dir}");
        }

        static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
            return new string(chars);
        }

        static string ExportLabel(StackExportMode m)
        {
            switch (m)
            {
                case StackExportMode.PerFilter: return "One per filter";
                case StackExportMode.EverySub: return "Every sub";
                default: return "Composite";
            }
        }

        string ExportDescription(StackExportMode m)
        {
            switch (m)
            {
                case StackExportMode.PerFilter:
                {
                    int n = 0;
                    foreach (CameraFilter f in astroStack.FiltersWithSubs) n++;
                    return $"One stacked FITS and PNG per band ({n} band{(n == 1 ? "" : "s")} held), "
                         + "registered and averaged, background subtracted. What an external stacker "
                         + "or PixInsight wants as its channels.";
                }
                case StackExportMode.EverySub:
                {
                    int n = 0;
                    foreach (CameraFilter f in astroStack.FiltersWithSubs) n += astroStack.SubCount(f);
                    return $"Every individual sub: {n} FITS and {n} PNG, cosmetically corrected but "
                         + "NOT registered, averaged or background subtracted. For a package that "
                         + "does its own registration.";
                }
                default:
                    return "The colour composite as shown: one PNG and one FITS of its luminance.";
            }
        }

        int ExportComposite(string dir, string stamp)
        {
            if (stackedCompositeTexture == null || lastComposedPixels == null)
            {
                stackExportReport = "Compose the colour image first.";
                return 0;
            }
            byte[] pngData = stackedCompositeTexture.EncodeToPNG();
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, $"{TargetFileName()}_{CompositeLabel(compositeMode).Replace(" ", "")}.png"), pngData);

            var fitsInfo = new FitsWriter.FitsHeaderInfo
            {
                ExposureSeconds = astroStack.TotalExposureSeconds(solarSystemCamera.Filter),
                PixelSizeMicrons = SolarSystemCameraTexture.PixelSizeMicrons,
                FullWellElectrons = SolarSystemCameraTexture.FullWellElectrons,
                AdcBits = SolarSystemCameraTexture.ActiveTelescope.AdcBits,
                ElectronsPerAdu = solarSystemCamera.LastElectronsPerAdu,
                SaturationAdu = solarSystemCamera.LastSaturationElectrons / System.Math.Max(1e-9, solarSystemCamera.LastElectronsPerAdu),
                FocalLengthMm = SolarSystemCameraTexture.FocalLengthMm,
                Gain = solarSystemCamera.Gain,
                IsCalibratedAdu = false, // stacked composite, see FitsHeaderInfo.IsCalibratedAdu
                FilterName = "LRGB",
                ObjectName = selectedPhotographyTarget.DisplayName,
                UtcTimestamp = DateTime.UtcNow,
                ImageType = "Light Frame",
            };
            FillCommonFitsMetadata(ref fitsInfo);

            // Deliberately NO world coordinate system on the composite. Every sub was registered
            // on the target body's own centroid (see AstroImageStack), and the body moves against
            // the stars between subs, so no single pointing describes the stack: the planet is
            // aligned and the field around it is not. Writing the last sub's WCS here would give a
            // plate solve or a catalogue cross-match a pointing that is wrong by however far the
            // body travelled, silently. Same standard as the omitted EGAIN above; a processed
            // product does not get keywords that describe a raw one.
            FitsWriter.WriteGrayscale(
                System.IO.Path.Combine(dir, $"{TargetFileName()}_{CompositeLabel(compositeMode).Replace(" ", "")}.fits"),
                ToAduScale(lastComposedPixels), stackedCompositeTexture.width, stackedCompositeTexture.height, fitsInfo);
            return 2;
        }

        /// <summary>One stacked frame per band: the composite's own channels, before they were combined.</summary>
        int ExportPerFilter(string dir)
        {
            int written = 0;
            foreach (CameraFilter filter in astroStack.FiltersWithSubs)
            {
                float[] stacked = astroStack.StackedFrame(filter, stackAlignSubs, stackLuckyImaging);
                if (stacked == null) continue;
                written += WriteFrame(dir, stacked, FilterLabel(filter) + "_stack", filter,
                                     astroStack.TotalExposureSeconds(filter),
                                     astroStack.SubCount(filter), calibrated: false);
            }
            return written;
        }

        /// <summary>Every sub, as it came off the converter. Numbered so an external stacker sees them in order.</summary>
        int ExportEverySub(string dir)
        {
            int written = 0;
            foreach (CameraFilter filter in astroStack.FiltersWithSubs)
            {
                int count = astroStack.SubCount(filter);
                for (int i = 0; i < count; i++)
                {
                    float[] sub = astroStack.SubFrame(filter, i);
                    if (sub == null) continue;
                    written += WriteFrame(dir, sub, $"{FilterLabel(filter)}_sub{i + 1:D3}", filter,
                                          astroStack.SubExposureSeconds(filter, i), 1, calibrated: true);
                }
            }
            return written;
        }

        /// <summary>
        /// Writes one monochrome frame as a FITS and a PNG beside it.
        ///
        /// The FITS carries the frame's OWN band, not the camera's currently selected one, and its
        /// own integration time rather than the stack's total; a header that describes a different
        /// exposure than the pixels is worse than no header. NSTACK says how many subs went in, so a
        /// reader can tell a single sub from an average of ten.
        /// </summary>
        int WriteFrame(string dir, float[] gray, string suffix, CameraFilter filter,
                       float exposureSeconds, int subCount, bool calibrated)
        {
            int w = SolarSystemCameraTexture.TextureWidth, h = SolarSystemCameraTexture.TextureHeight;
            if (gray.Length != w * h) return 0;

            var fitsInfo = new FitsWriter.FitsHeaderInfo
            {
                ExposureSeconds = exposureSeconds,
                PixelSizeMicrons = SolarSystemCameraTexture.PixelSizeMicrons,
                FullWellElectrons = SolarSystemCameraTexture.FullWellElectrons,
                AdcBits = SolarSystemCameraTexture.ActiveTelescope.AdcBits,
                ElectronsPerAdu = solarSystemCamera.LastElectronsPerAdu,
                SaturationAdu = solarSystemCamera.LastSaturationElectrons / System.Math.Max(1e-9, solarSystemCamera.LastElectronsPerAdu),
                FocalLengthMm = SolarSystemCameraTexture.FocalLengthMm,
                Gain = solarSystemCamera.Gain,
                IsCalibratedAdu = calibrated,
                FilterName = FilterLabel(filter),
                ObjectName = selectedPhotographyTarget.DisplayName,
                UtcTimestamp = DateTime.UtcNow,
                ImageType = "Light Frame",
            };
            FillCommonFitsMetadata(ref fitsInfo);
            fitsInfo.FilterCentralWavelengthNm = SolarSystemCameraTexture.CentralWavelengthNmOf(filter);
            fitsInfo.FilterBandwidthNm = SolarSystemCameraTexture.BandwidthNmOf(filter);
            fitsInfo.StackedSubs = subCount;

            string baseName = System.IO.Path.Combine(dir, $"{TargetFileName()}_{suffix}");
            FitsWriter.WriteGrayscale(baseName + ".fits", ToAduScaleGray(gray), w, h, fitsInfo);

            byte[] rgb = SolarSystemCameraTexture.RenderMonoPreviewRgb24(gray, w, h);
            int count = 1;
            if (rgb != null)
            {
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.LoadRawTextureData(rgb);
                tex.Apply();
                System.IO.File.WriteAllBytes(baseName + ".png", tex.EncodeToPNG());
                Destroy(tex);
                count = 2;
            }
            return count;
        }

        /// <summary>Fractions of full scale to ADU, for a monochrome plane rather than a Color[].</summary>
        static float[] ToAduScaleGray(float[] gray)
        {
            if (gray == null) return null;
            int max = SolarSystemCameraTexture.AdcMaxCount;
            var outp = new float[gray.Length];
            for (int i = 0; i < gray.Length; i++) outp[i] = Mathf.Clamp01(gray[i]) * max;
            return outp;
        }

        /// <summary>
        /// Fills the header fields that describe the instrument, the site, and the conditions the
        /// exposure was taken through, everything common to a raw sub and a stacked composite.
        ///
        /// Kept in one place because these are the fields a reduction pipeline actually keys off,
        /// and having two save paths fill them independently is how they drift apart. What differs
        /// between the two paths (the WCS, the frame type, the calibration flag) is set by each
        /// caller instead.
        /// </summary>
        void FillCommonFitsMetadata(ref FitsWriter.FitsHeaderInfo info)
        {
            VisualTelescopeSpec spec = SolarSystemCameraTexture.ActiveTelescope;

            info.TelescopeName = spec.Name;
            info.InstrumentName = spec.CameraName;
            info.ObservatoryName = spec.SiteName;
            info.ApertureMeters = spec.ApertureMeters;
            info.BinningFactor = SolarSystemCameraTexture.BinningFactor;
            info.ReadNoiseElectrons = spec.ReadNoiseElectrons;
            // The rate the capture ACTUALLY used, at the detector's actual temperature, rather than
            // the catalogue's reference figure; the two are the same today and would silently
            // disagree the moment a cooler setpoint exists (see Core.DarkCurrentModel).
            info.DarkCurrentElectronsPerSecond = solarSystemCamera.LastDarkCurrentElectronsPerSecond > 0.0
                ? solarSystemCamera.LastDarkCurrentElectronsPerSecond
                : spec.DarkCurrentElectronsPerSecond;
            info.DetectorTemperatureCelsius = SolarSystemCameraTexture.DetectorTemperatureCelsius;

            info.PhotometricZeroPoint = solarSystemCamera.LastPhotometricZeroPoint;
            info.BiasLevelAdu = solarSystemCamera.LastBiasLevelAdu;
            info.RandomSeed = solarSystemCamera.LastCaptureSeed;
            info.SoftwareVersion = "ExoInstruments "
                + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

            info.SiteLatitudeDeg = ObservatorySite.LatitudeDeg;
            info.SiteLongitudeDeg = ObservatorySite.LongitudeDeg;
            info.SiteElevationMeters = spec.SiteAltitudeMeters;

            info.Airmass = solarSystemCamera.LastAirmass;
            info.SeeingFwhmArcsec = solarSystemCamera.LastAtmosphericFwhmArcsec;
            info.DiffractionFwhmArcsec = solarSystemCamera.LastDiffractionFwhmArcsec;
            info.SkyBrightnessVMagPerArcsec2 = solarSystemCamera.LastSkyBrightnessVMagPerArcsec2;
            info.GalacticReddeningEBv = solarSystemCamera.LastFieldReddeningEBv;
            info.DustMapSource = SolarSystemCameraTexture.DustMap != null
                ? SolarSystemCameraTexture.DustMap.Source : null;
            info.LineSurfaceBrightnessRayleighs = solarSystemCamera.LastEmissionRayleighs;
            info.EmissionLineName = SolarSystemCameraTexture.EmissionMap != null
                ? SolarSystemCameraTexture.EmissionMap.LineName : null;
            info.EmissionMapSource = SolarSystemCameraTexture.EmissionMap != null
                ? SolarSystemCameraTexture.EmissionMap.Source : null;

            info.OpticalThroughput = spec.OpticsTransmission;
            info.EffectiveWidthAngstrom = solarSystemCamera.LastEffectiveWidthAngstrom;
        }

        /// <summary>Spreads a processed [0,1] composite over the converter's range, purely so it survives a 16-bit container. Not a calibration; see FitsHeaderInfo.IsCalibratedAdu.</summary>
        static float[] ToAduScale(Color[] pixels)
        {
            if (pixels == null) return null;
            int max = SolarSystemCameraTexture.AdcMaxCount;
            var outp = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) outp[i] = Mathf.Clamp01(pixels[i].r) * max;
            return outp;
        }

        static string FilterLabel(CameraFilter f)
        {
            switch (f)
            {
                case CameraFilter.Luminance: return "L";
                case CameraFilter.Red: return "R";
                case CameraFilter.Green: return "G";
                case CameraFilter.Blue: return "B";
                case CameraFilter.HAlpha: return "Ha";
                case CameraFilter.OIII:   return "OIII";
                case CameraFilter.SII:    return "SII";
                case CameraFilter.NII:    return "NII";
                case CameraFilter.OII:    return "OII";
                case CameraFilter.OI:     return "OI";
                default: return f.ToString();
            }
        }

        /// <summary>Channels the stacked-subs readout lists, in enum order: the active instrument's own wheel, plus any filter that still holds subs (a telescope swapped mid-session).</summary>
        List<CameraFilter> StackReadoutFilters()
        {
            var listed = new List<CameraFilter>(SolarSystemCameraTexture.ActiveTelescope.AvailableFilters);
            foreach (CameraFilter f in (CameraFilter[])Enum.GetValues(typeof(CameraFilter)))
                if (!listed.Contains(f) && astroStack.SubCount(f) > 0) listed.Add(f);
            listed.Sort();
            return listed;
        }

        static string NdFilterLabel(NdFilterStop stop)
        {
            switch (stop)
            {
                case NdFilterStop.Nd8: return "ND8";
                case NdFilterStop.Nd64: return "ND64";
                case NdFilterStop.Nd1000: return "ND1000";
                case NdFilterStop.Nd6300: return "OD3.8";
                case NdFilterStop.Nd100000: return "Solar";
                default: return "None";
            }
        }

        static string FormatExposure(float seconds)
        {
            if (seconds < 0.001f) return $"{seconds * 1_000_000f:F0} us";
            if (seconds < 1f) return $"{seconds * 1000f:F1} ms";
            return $"{seconds:F2} s";
        }

        /// <summary>
        /// Real acquisition-software convention (SharpCap, NINA): FOV switches to arcminutes
        /// below 1 degree, since every telescope in VisualTelescopeCatalog has a sub-1-degree
        /// range and a flat "X.X deg" label would round every instrument's real MinFovDeg/
        /// MaxFovDeg range down to the same one or two digits, hiding genuine differences
        /// between telescopes (and between zoom settings on the same one) behind the rounding.
        /// Drops to arcseconds below 1 arcminute for SPHERE/ZIMPOL's real ~3.6" field, which
        /// would otherwise round to an unreadable "0.06'".
        /// </summary>
        static string FormatFov(float deg)
        {
            float arcmin = deg * 60f;
            if (arcmin < 1f) return $"{arcmin * 60f:F2}\"";
            if (deg < 1f) return $"{arcmin:F2}'";
            return $"{deg:F2} deg";
        }

        /// <summary>Always-visible observability line for the selected body: night gate + the body's current altitude.</summary>
        void DrawPhotographyObservability()
        {
            var conditions = ImagingObservingConditions.Evaluate(
                Planetarium.GetUniversalTime(), null, null, BuildImagingObserverContext());
            TryComputeTargetAltAz(selectedPhotographyTarget, out double alt, out double az);
            string name = selectedPhotographyTarget.DisplayName;

            string sky = conditions.IsNight
                ? $"Night (Sun {conditions.SunAltitudeDeg:F0} deg)."
                : $"Daytime, dome closed (Sun {conditions.SunAltitudeDeg:F0} deg, reopens below {ImagingObservingConditions.TwilightSunAltitudeDeg:F0} deg).";
            string targetLine = alt > 0.0
                ? $"{name} is up: altitude {alt:F0} deg, azimuth {az:F0} deg."
                : $"{name} is below the horizon ({alt:F0} deg); warp/wait for it to rise.";
            GUILayout.Label(sky + "  " + targetLine, smallCaptionStyle);
        }

        /// <summary>Writes the finished captured photo to KSP's screenshot folder as a PNG (quick preview) and a real 16-bit FITS file (the same format a real RC20+camera would actually produce).</summary>
        void SaveSolarSystemPhoto()
        {
            Texture2D frame = solarSystemCamera.CapturedPhoto;
            if (frame == null) return;
            string dir = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "Screenshots");
            System.IO.Directory.CreateDirectory(dir);
            string stamp = $"{DateTime.Now:yyyyMMdd_HHmmss}";

            byte[] pngData = frame.EncodeToPNG();
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, $"ExoInstruments_{TargetFileName()}_{stamp}.png"), pngData);

            if (saveDiagnosticFrames)
            {
                Texture2D raw = solarSystemCamera.RawRenderFrame;
                if (raw != null)
                {
                    System.IO.File.WriteAllBytes(
                        System.IO.Path.Combine(dir, $"ExoInstruments_{TargetFileName()}_{stamp}_RAWRENDER.png"),
                        raw.EncodeToPNG());
                }
            }

            // Companion dump of the untouched Unity render (see RawRenderFrame): the finished PNG
            // alone can't tell you whether softness came from the game's own scaled-space
            // rendering of the body or from this mod's optical model. Saving both side by side
            // makes that attribution a one-glance comparison instead of a guess.
            var fitsInfo = new FitsWriter.FitsHeaderInfo
            {
                ExposureSeconds = solarSystemCamera.ExposureSeconds,
                PixelSizeMicrons = SolarSystemCameraTexture.PixelSizeMicrons,
                FullWellElectrons = SolarSystemCameraTexture.FullWellElectrons,
                AdcBits = SolarSystemCameraTexture.ActiveTelescope.AdcBits,
                ElectronsPerAdu = solarSystemCamera.LastElectronsPerAdu,
                SaturationAdu = solarSystemCamera.LastSaturationElectrons / System.Math.Max(1e-9, solarSystemCamera.LastElectronsPerAdu),
                FocalLengthMm = SolarSystemCameraTexture.FocalLengthMm,
                Gain = solarSystemCamera.Gain,
                IsCalibratedAdu = true, // single raw frame straight off the converter
                FilterName = FilterLabel(solarSystemCamera.Filter),
                ObjectName = selectedPhotographyTarget.DisplayName,
                UtcTimestamp = DateTime.UtcNow,
                ImageType = "Light Frame",
                // A raw sub is one pointing at one instant, so it can carry a real WCS, measured
                // from the same projection that placed the stars in it (see Core.FitsWcs).
                Wcs = solarSystemCamera.LastWcs,
                TrailedByDrift = solarSystemCamera.LastFrameTrailed,
                FilterCentralWavelengthNm = solarSystemCamera.ActiveFilterCentralWavelengthNm,
                FilterBandwidthNm = solarSystemCamera.ActiveFilterBandwidthNm,
            };
            FillCommonFitsMetadata(ref fitsInfo);
            FitsWriter.WriteGrayscale(
                System.IO.Path.Combine(dir, $"ExoInstruments_{TargetFileName()}_{stamp}.fits"),
                solarSystemCamera.GetLastCaptureAdu(), SolarSystemCameraTexture.TextureWidth, SolarSystemCameraTexture.TextureHeight, fitsInfo);
        }

        void DrawStarSelection()
        {
            // The site is what decides which half of the sky is reachable at all, so it belongs
            // next to the target list rather than buried in a diagnostic. On stock this reads as
            // the equatorial KSC; on a pack that relocates the space centre it reads as wherever
            // the observatory actually stands, and the chart above it changes accordingly.
            GUILayout.Label($"Observing from {ObservatorySite.Describe()}", smallCaptionStyle);
            GUILayout.Label($"Select target star: ({catalog.Count} loaded)");
            GUILayout.Label("Filter by name (matches are highlighted and clickable on the sky chart):");

            string newFilter = GUILayout.TextField(searchFilter, GUILayout.Height(28));
            if (newFilter != searchFilter)
            {
                searchFilter = newFilter;
                RefreshSkyChartHighlights();
            }

            GUILayout.Space(6);
            Rect chartRect = GUILayoutUtility.GetRect(SkyChartWidth, SkyChartHeight,
                GUILayout.Width(SkyChartWidth), GUILayout.Height(SkyChartHeight));
            if (skyChartTexture != null)
            {
                GUI.DrawTexture(chartRect, skyChartTexture);
            }
            UpdateSkyChartHover(chartRect);
            HandleSkyChartInteraction(chartRect);

            GUILayout.Label(hoveredSkyChartStar != null
                ? $"Hovering: {GetDisplayName(hoveredSkyChartStar)}"
                : hoveredDeepSky.HasValue
                    ? $"Hovering: {hoveredDeepSky.Value.DisplayName}, {DescribeDeepSky(hoveredDeepSky.Value)}"
                    : "Hovering: (none)");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Scroll to zoom, drag to pan. Zenith at center, horizon/rings at 0/20/40/60 deg.");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Recenter", GUILayout.Width(90)))
            {
                skyChartZoom = 1f;
                skyChartPan = new Vector2(SkyChartWidth / 2f, SkyChartHeight / 2f);
                RenderSkyChartTexture();
            }
            GUILayout.EndHorizontal();

            DrawUnmappedMatches();
        }

        /// <summary>
        /// Catalog entries with no RaDeg/DecDeg never appear on the sky chart;
        /// this is the only way to still select them, shown only while filtering
        /// so it doesn't bloat the view for the common case.
        /// </summary>
        void DrawUnmappedMatches()
        {
            if (string.IsNullOrEmpty(searchFilter)) return;

            List<StarTarget> unmapped = null;
            foreach (var star in catalog)
            {
                if (star.RaDeg.HasValue && star.DecDeg.HasValue) continue;
                // Career: an unscanned target with no coordinates is unreachable by
                // construction; there's no position to point the telescope at and
                // no name the player could search for. Skip rather than leak.
                if (IsIdentityHidden(star)) continue;
                if (star.Name.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (unmapped == null) unmapped = new List<StarTarget>();
                unmapped.Add(star);
            }
            if (unmapped == null) return;

            GUILayout.Space(6);
            GUILayout.Label("No sky coordinates (not on the chart above):");
            int shown = 0;
            foreach (var star in unmapped)
            {
                bool isSelected = (selectedStar == star);
                string label = isSelected ? $"> {star.Name} [{star.Status}] <" : $"{star.Name} [{star.Status}]";
                if (GUILayout.Button(label))
                {
                    selectedStar = star;
                }
                shown++;
                if (shown >= MaxShownInList)
                {
                    GUILayout.Label($"... showing first {MaxShownInList} matches, refine your search.");
                    break;
                }
            }
        }

        /// <summary>
        /// Scroll wheel zooms toward the cursor; left-drag pans; a left click that
        /// barely moves (under SkyChartDragClickThreshold) selects a star instead.
        /// Distinguishing click vs. drag needs MouseDown/Drag/Up across frames,
        /// which is why this carries state (skyChartDragging etc.) rather than
        /// being a single stateless check like the old click-only handler.
        /// </summary>
        /// <summary>
        /// Recomputes which star the cursor is hovering, every OnGUI pass. Cheap:
        /// reuses HitTest (an O(n) loop over already-projected points), no
        /// texture rebuild; unlike a click, this can safely run every frame.
        /// </summary>
        void UpdateSkyChartHover(Rect chartRect)
        {
            Vector2 mouse = Event.current.mousePosition;
            if (!chartRect.Contains(mouse))
            {
                hoveredSkyChartStar = null;
                hoveredDeepSky = null;
                return;
            }

            int localX = (int)(mouse.x - chartRect.x);
            int localY = (int)(chartRect.height - (mouse.y - chartRect.y));
            var view = new SkyChartView { Zoom = skyChartZoom, Pan = skyChartPan };
            hoveredSkyChartStar = SkyChartTexture.HitTest(cachedSkyChartPoints, SkyChartWidth, SkyChartHeight, view, localX, localY);
            hoveredDeepSky = SkyChartTexture.HitTestDeepSky(cachedSkyChartPoints, SkyChartWidth, SkyChartHeight,
                                                            view, localX, localY, out DeepSkyObject nebula)
                ? (DeepSkyObject?)nebula : null;
        }

        void HandleSkyChartInteraction(Rect chartRect)
        {
            Event e = Event.current;
            bool overChart = chartRect.Contains(e.mousePosition);

            if (e.type == EventType.ScrollWheel && overChart)
            {
                HandleSkyChartZoom(chartRect, e);
                e.Use();
                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0 && overChart)
            {
                // Bodies are checked first and resolved immediately (no
                // drag-vs-click distinction needed; their markers are big,
                // deliberate targets): a hit selects the body as the
                // photography target straight away and never starts a pan drag.
                if (TryHitBodyMarker(chartRect, e.mousePosition, out CelestialBody hitBody))
                {
                    SelectPhotographyBody(hitBody);
                    e.Use();
                    return;
                }

                skyChartDragging = true;
                skyChartDragStartMouse = e.mousePosition;
                skyChartDragStartPan = skyChartPan;
                skyChartDragDistance = 0f;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && skyChartDragging && e.button == 0)
            {
                Vector2 deltaImgui = e.mousePosition - skyChartDragStartMouse;
                skyChartDragDistance = deltaImgui.magnitude;

                // IMGUI y grows downward; the chart's raw pixel space grows upward
                // (row 0 = bottom, see SkyChartTexture); flip the y component.
                Vector2 textureDelta = new Vector2(deltaImgui.x, -deltaImgui.y);
                skyChartPan = skyChartDragStartPan - textureDelta / skyChartZoom;
                ClampSkyChartPan();
                RenderSkyChartTexture();
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && skyChartDragging)
            {
                skyChartDragging = false;
                if (skyChartDragDistance < SkyChartDragClickThreshold)
                {
                    if (hoveredSkyChartStar != null)
                    {
                        selectedStar = hoveredSkyChartStar;
                        SelectPhotographyStar(hoveredSkyChartStar);
                    }
                    else if (hoveredDeepSky.HasValue)
                    {
                        SelectPhotographyTarget(
                            SkyTarget.FromEquatorial(hoveredDeepSky.Value.RaDeg, hoveredDeepSky.Value.DecDeg,
                                                     hoveredDeepSky.Value.DisplayName),
                            clearStarSelection: true);
                    }
                    else
                    {
                        // Empty sky. A dark cloud or an uncatalogued patch has no marker, so
                        // clicking where it is has to work or those targets are unreachable
                        // without typing coordinates.
                        PointAtChartPosition(chartRect, e.mousePosition);
                    }
                }
                e.Use();
            }
        }

        /// <summary>Hit-tests a screen point against cachedChartBodies (built alongside the star points) using the same projection the chart was baked with.</summary>
        bool TryHitBodyMarker(Rect chartRect, Vector2 screenPos, out CelestialBody hit)
        {
            hit = null;
            var view = new SkyChartView { Zoom = skyChartZoom, Pan = skyChartPan };
            float bestDistSq = 16f * 16f; // generous click tolerance
            foreach (var (body, alt, az) in cachedChartBodies)
            {
                Vector2 proj = SkyChartTexture.ProjectAltAzToScreen(alt, az, SkyChartWidth, SkyChartHeight, view);
                float sx = chartRect.x + proj.x;
                float sy = chartRect.y + (SkyChartHeight - proj.y); // texture y-up -> IMGUI y-down
                float dsq = (sx - screenPos.x) * (sx - screenPos.x) + (sy - screenPos.y) * (sy - screenPos.y);
                if (dsq <= bestDistSq) { bestDistSq = dsq; hit = body; }
            }
            return hit != null;
        }

        /// <summary>Selects a body as the photography target (clearing any star selection), resets any in-progress capture for the old target, and updates the chart's selection ring immediately.</summary>
        void SelectPhotographyBody(CelestialBody body)
        {
            SelectPhotographyTarget(SkyTarget.FromBody(body), clearStarSelection: true);
        }

        /// <summary>
        /// Points the telescope. clearStarSelection is false when the target came FROM a star the
        /// player picked on the chart, so the detection tabs keep showing that star.
        /// </summary>
        void SelectPhotographyTarget(SkyTarget target, bool clearStarSelection)
        {
            if (target == selectedPhotographyTarget) return;
            selectedPhotographyTarget = target;
            if (clearStarSelection) selectedStar = null;
            manualTargetError = null;
            solarSystemCamera.DiscardCapturedPhoto();
            photographySessionActive = false;
            ClearAstroStack();
            UpdateBodySelectionRingAndRerender();
            ApplyTelescopeTracking();
        }

        /// <summary>
        /// Drives the observatory model. The rig aims in altitude and azimuth, so a fixed target is
        /// handed the same pair the camera aims with rather than a body to look up.
        /// </summary>
        void ApplyTelescopeTracking()
        {
            ExoObservatoryTelescopeTracker.TrackedBody = selectedPhotographyTarget.Body;
            if (selectedPhotographyTarget.IsEquatorial
                && TryComputeTargetAltAz(selectedPhotographyTarget, out double alt, out double az))
            {
                ExoObservatoryTelescopeTracker.TrackedAltDeg = alt;
                ExoObservatoryTelescopeTracker.TrackedAzDeg = az;
            }
            else
            {
                ExoObservatoryTelescopeTracker.TrackedAltDeg = null;
                ExoObservatoryTelescopeTracker.TrackedAzDeg = null;
            }
        }

        /// <summary>Altitude and azimuth of whatever the telescope is pointed at.</summary>
        bool TryComputeTargetAltAz(SkyTarget target, out double altDeg, out double azDeg)
        {
            altDeg = 0.0; azDeg = 0.0;
            if (target.IsBody) return TryComputeBodyAltAz(target.Body, out altDeg, out azDeg);
            if (!target.IsEquatorial) return false;

            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return false;
            double meridianRaDeg = SkyCoordinates.ComputeLocalMeridianRaDeg(
                Planetarium.GetUniversalTime(), home.rotationPeriod, home.initialRotation,
                ObservatorySite.LongitudeDeg);
            var horizontal = SkyCoordinates.EquatorialToHorizontal(
                target.RaDeg, target.DecDeg, meridianRaDeg, ObservatorySite.LatitudeDeg);
            altDeg = horizontal.AltitudeDeg;
            azDeg = horizontal.AzimuthDeg;
            return true;
        }

        /// <summary>
        /// Points the telescope at whatever patch of sky a chart click landed on, by running the
        /// chart's own projection backwards and then the horizontal-to-equatorial transform.
        /// </summary>
        void PointAtChartPosition(Rect chartRect, Vector2 screenPos)
        {
            float localX = screenPos.x - chartRect.x;
            float localY = SkyChartHeight - (screenPos.y - chartRect.y);   // IMGUI y-down -> texture y-up
            var view = new SkyChartView { Zoom = skyChartZoom, Pan = skyChartPan };

            if (!SkyChartTexture.TryScreenToAltAz(localX, localY, SkyChartWidth, SkyChartHeight,
                                                  view, out double altDeg, out double azDeg))
                return;

            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return;

            double meridianRaDeg = SkyCoordinates.ComputeLocalMeridianRaDeg(
                Planetarium.GetUniversalTime(), home.rotationPeriod, home.initialRotation,
                ObservatorySite.LongitudeDeg);
            SkyCoordinates.HorizontalToEquatorial(altDeg, azDeg, meridianRaDeg,
                                                  ObservatorySite.LatitudeDeg,
                                                  out double raDeg, out double decDeg);

            SelectPhotographyTarget(SkyTarget.FromEquatorial(raDeg, decDeg, null),
                                    clearStarSelection: true);
        }

        /// <summary>Turns a chart star into a pointing target. Requires coordinates, which is what the chart placed it by anyway.</summary>
        void SelectPhotographyStar(StarTarget star)
        {
            if (star == null || !star.RaDeg.HasValue || !star.DecDeg.HasValue) return;
            SelectPhotographyTarget(
                SkyTarget.FromEquatorial(star.RaDeg.Value, star.DecDeg.Value, GetDisplayName(star)),
                clearStarSelection: false);
        }

        /// <summary>Drops all stacked subs and the composite preview; the stack is specific to one target, it must not survive a target switch.</summary>
        void ClearAstroStack()
        {
            astroStack.ClearAll();
            stackBatchRemaining = 0;
            stackComposeError = null;
            stackBatchInterruptedMessage = null;
            if (stackedCompositeTexture != null)
            {
                Destroy(stackedCompositeTexture);
                stackedCompositeTexture = null;
            }
            lastComposedPixels = null;
        }

        /// <summary>Refreshes IsSelectedTarget on the cached body points to match selectedPhotographyBody and re-rasters; so the ring appears/moves the instant a body is (de)selected, without waiting for the next full catalog refresh.</summary>
        void UpdateBodySelectionRingAndRerender()
        {
            double selAlt = double.NaN, selAz = double.NaN;
            foreach (var (body, alt, az) in cachedChartBodies)
            {
                if (body == selectedPhotographyBody) { selAlt = alt; selAz = az; break; }
            }

            SkyTarget pointing = selectedPhotographyTarget;
            for (int i = 0; i < cachedSkyChartPoints.Count; i++)
            {
                var p = cachedSkyChartPoints[i];
                bool shouldBeSelected = p.IsBody
                    ? !double.IsNaN(selAlt)
                      && Math.Abs(selAlt - p.AltitudeDeg) < 1e-6 && Math.Abs(selAz - p.AzimuthDeg) < 1e-6
                    : IsPhotographyTarget(p.Target, pointing);
                if (p.IsSelectedTarget != shouldBeSelected)
                {
                    p.IsSelectedTarget = shouldBeSelected;
                    cachedSkyChartPoints[i] = p;
                }
            }
            RenderSkyChartTexture();
        }

        /// <summary>
        /// Hand-entered coordinates. Everything worth photographing that is not a planet and not in
        /// the loaded catalogue (a nebula, a galaxy, a dark cloud) is reachable only this way.
        /// </summary>
        void DrawManualPointingEntry()
        {
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Point at RA/Dec", GUILayout.Width(100));
            manualRaText = GUILayout.TextField(manualRaText ?? "", GUILayout.Width(110));
            manualDecText = GUILayout.TextField(manualDecText ?? "", GUILayout.Width(110));
            if (GUILayout.Button("Go", GUILayout.Width(40)))
            {
                if (SkyTarget.TryParse(manualRaText, manualDecText, out double ra, out double dec))
                {
                    SelectPhotographyTarget(SkyTarget.FromEquatorial(ra, dec, null), clearStarSelection: true);
                }
                else
                {
                    manualTargetError = "Could not read those coordinates.";
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(manualTargetError
                ?? "Sexagesimal (05 35 17.3 / -05 23 28) or decimal degrees (83.822 / -5.391). RA in hours when sexagesimal.",
                smallCaptionStyle);
        }

        /// <summary>One line stating where the telescope is actually pointed, shown under a star's own card.</summary>
        void DrawPointingSummary()
        {
            if (!selectedPhotographyTarget.HasTarget)
            {
                GUILayout.Label("Telescope not pointed at anything; click this star on the chart to point at it.",
                                smallCaptionStyle);
                return;
            }
            TryComputeTargetAltAz(selectedPhotographyTarget, out double alt, out double az);
            GUILayout.Label($"Telescope pointed at {selectedPhotographyTarget.DisplayName}"
                          + $"  (altitude {alt:F0} deg, azimuth {az:F0} deg)", smallCaptionStyle);
        }

        /// <summary>Target name reduced to something a filesystem accepts.</summary>
        string TargetFileName()
        {
            string name = selectedPhotographyTarget.DisplayName;
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '+' ? c : '_');
            return sb.ToString();
        }

        /// <summary>Whether the telescope is pointed at this catalogue star, matched on coordinates rather than identity so a reloaded catalogue still rings the right marker.</summary>
        static bool IsPhotographyTarget(StarTarget star, SkyTarget pointing)
        {
            if (star == null || !pointing.IsEquatorial) return false;
            if (!star.RaDeg.HasValue || !star.DecDeg.HasValue) return false;
            return Math.Abs(star.RaDeg.Value - pointing.RaDeg) < 1e-6
                && Math.Abs(star.DecDeg.Value - pointing.DecDeg) < 1e-6;
        }

        void HandleSkyChartZoom(Rect chartRect, Event e)
        {
            float localX = e.mousePosition.x - chartRect.x;
            float localY = chartRect.height - (e.mousePosition.y - chartRect.y);

            // The raw-space point currently under the cursor, before zoom changes,
            // solving pan afterwards so this same point stays under the cursor.
            Vector2 rawUnderCursor = new Vector2(
                (localX - SkyChartWidth / 2f) / skyChartZoom + skyChartPan.x,
                (localY - SkyChartHeight / 2f) / skyChartZoom + skyChartPan.y);

            // Scroll up (delta.y < 0) zooms in.
            float zoomFactor = 1f - e.delta.y * SkyChartZoomSensitivity;
            float newZoom = Mathf.Clamp(skyChartZoom * zoomFactor, SkyChartMinZoom, SkyChartMaxZoom);

            skyChartPan.x = rawUnderCursor.x - (localX - SkyChartWidth / 2f) / newZoom;
            skyChartPan.y = rawUnderCursor.y - (localY - SkyChartHeight / 2f) / newZoom;
            skyChartZoom = newZoom;

            ClampSkyChartPan();
            RenderSkyChartTexture();
        }

        /// <summary>Keeps the view center within the horizon radius so panning can't lose the sky off-screen entirely.</summary>
        void ClampSkyChartPan()
        {
            Vector2 center = new Vector2(SkyChartWidth / 2f, SkyChartHeight / 2f);
            float rMax = SkyChartTexture.ComputeRMax(SkyChartWidth, SkyChartHeight);
            Vector2 offset = skyChartPan - center;
            if (offset.magnitude > rMax)
            {
                skyChartPan = center + offset.normalized * rMax;
            }
        }

        void DrawRightColumn()
        {
            scrollPosRight = GUILayout.BeginScrollView(scrollPosRight, GUILayout.Height(ColumnContentHeight));

            if (photographySessionActive)
            {
                DrawSolarSystemCameraView();
                GUILayout.EndScrollView();
                return;
            }

            if (session == null && rvSession == null && imagingSession == null)
            {
                GUILayout.Label(selectedPhotographyTarget.HasTarget
                    ? "Click \"Start Observation\" on the left when ready."
                    : selectedStar == null
                    ? "Select a target on the left to begin."
                    : "Click \"Start Observation\" on the left when ready.");
                GUILayout.EndScrollView();
                return;
            }

            if (session != null)
            {
                DrawTransitObservation();
            }
            else if (rvSession != null)
            {
                DrawRvObservation();
            }
            else
            {
                DrawImagingObservation();
            }

            GUILayout.EndScrollView();
        }

        /// <summary>
        /// Converts a duration in real seconds to whatever "day" KSP itself is
        /// currently displaying, 6h Kerbin days by default (GameSettings.KERBIN_TIME),
        /// or 24h Earth days if the player enabled that setting. Session/detector math
        /// stays in real seconds/days throughout (astronomically correct, matches the
        /// catalog's real orbital periods); this only affects operational labels the
        /// player reads alongside KSP's own "T-" warp countdown and mission clock, so
        /// the numbers agree instead of appearing to disagree by exactly 4x.
        /// </summary>
        private static double ToDisplayDays(double seconds)
        {
            double kspDaySeconds = GameSettings.KERBIN_TIME ? 21600.0 : 86400.0;
            return seconds / kspDaySeconds;
        }

        // --- Career fog of war ------------------------------------------------
        // In career mode a star's catalog identity is hidden until the player
        // completes a scan of it (observation + detection analysis). Sandbox and
        // science-sandbox games are untouched: full catalog info on click, as
        // always. Gate is KSP's own game mode, no bespoke setting.

        /// <summary>Outcome of the most recent completed scan, shown in the scan report.</summary>
        private float lastScanScienceAwarded;
        private bool lastScanWasFirstForStar;
        private int lastScanJackpotPlanetCount;
        private bool lastScanCharacterized;

        private static bool CareerFogActive =>
            HighLogic.CurrentGame != null && HighLogic.CurrentGame.Mode == Game.Modes.CAREER;

        /// <summary>
        /// True when the star's identity must be withheld from the player. If the
        /// scenario state is unavailable in career (shouldn't happen; it's added
        /// to all games), fail toward hiding rather than leaking.
        /// </summary>
        private static bool IsIdentityHidden(StarTarget star)
        {
            if (!CareerFogActive) return false;
            ExoInstrumentsScenario scenario = ExoInstrumentsScenario.Instance;
            return scenario == null || !scenario.IsScanned(star.CatalogKey);
        }

        /// <summary>
        /// What the player is allowed to call this star right now: its real
        /// designation once revealed, else a positional provisional designation
        /// ("Unscanned J2257+2046"), the naming a real survey would use for a
        /// source it hasn't identified.
        /// </summary>
        private static string GetDisplayName(StarTarget star)
        {
            if (!IsIdentityHidden(star)) return star.Name;
            return star.RaDeg.HasValue && star.DecDeg.HasValue
                ? "Unscanned " + StarNames.ProvisionalDesignation(star.RaDeg.Value, star.DecDeg.Value)
                : "Unscanned target";
        }

        /// <summary>
        /// Career bookkeeping when a detection analysis completes: reveals the
        /// star's identity whatever the outcome (a null result still charts the
        /// sky) and awards Science, once per star for the scan itself, plus a
        /// one-time bonus per host for a confirmed real detection. The detection
        /// bonus is gated on the catalog truth as well as the analysis verdict so
        /// a statistical false positive on a decoy's noise can't be farmed.
        ///
        /// The detection bonus is scaled by the observing instrument's explicit
        /// ScienceRewardMultiplier (bigger telescope, bigger payoff; see
        /// InstrumentSpec) and, when realPlanetsDetectedCount is more than 1 (an
        /// RV campaign resolving several catalog planets at once), by the jackpot
        /// bonus, the single biggest payout the survey loop offers, by design.
        ///
        /// stellarCharacterization additionally claims the one-time
        /// characterization award (direct imaging of a star with a measurable
        /// temperature), flat, tracked separately from the scan reveal so a
        /// star identified earlier by transit/RV still pays out the first time
        /// it's actually imaged.
        /// </summary>
        private void RegisterScanCompleted(StarTarget target, InstrumentSpec instrument, bool confirmedRealDetection,
            int realPlanetsDetectedCount = 1, bool stellarCharacterization = false)
        {
            lastScanScienceAwarded = 0f;
            lastScanWasFirstForStar = false;
            lastScanJackpotPlanetCount = 0;
            lastScanCharacterized = false;
            if (!CareerFogActive) return;

            ExoInstrumentsScenario scenario = ExoInstrumentsScenario.Instance;
            if (scenario == null)
            {
                Debug.LogWarning("[ExoInstruments] Career scan completed but no scenario instance is loaded, reveal not persisted.");
                return;
            }

            float award = 0f;
            if (scenario.MarkScanned(target.CatalogKey))
            {
                lastScanWasFirstForStar = true;
                award += ScienceRewards.ScienceRewardFirstScan;
            }
            if (stellarCharacterization && scenario.MarkCharacterized(target.CatalogKey))
            {
                lastScanCharacterized = true;
                award += ScienceRewards.ScienceRewardStellarCharacterization;
            }
            if (confirmedRealDetection && scenario.MarkDetectionRewarded(target.CatalogKey))
            {
                int planetCount = Math.Max(1, realPlanetsDetectedCount);
                float detectionAward = ScienceRewards.ScienceRewardRealDetection
                                       * (float)instrument.ScienceRewardMultiplier
                                       * planetCount;
                if (planetCount > 1)
                {
                    double jackpotFactor = 1.0 + (planetCount - 1) * ScienceRewards.JackpotMultiplierPerExtraPlanet;
                    detectionAward *= (float)jackpotFactor;
                    lastScanJackpotPlanetCount = planetCount;
                }
                award += detectionAward;
            }

            if (award > 0f)
            {
                scenario.AddEarnedScience(award);
                if (ResearchAndDevelopment.Instance != null)
                {
                    ResearchAndDevelopment.Instance.AddScience(award, TransactionReasons.ScienceTransmission);
                }
            }
            lastScanScienceAwarded = award;
        }

        /// <summary>Career lines at the top of a scan report: the reveal and what it paid.</summary>
        private void DrawCareerScanOutcome(StarTarget target)
        {
            if (!CareerFogActive) return;
            if (lastScanWasFirstForStar)
            {
                GUILayout.Label($"Target identified: {target.HostStarName ?? target.Name}");
            }
            if (lastScanCharacterized)
            {
                GUILayout.Label($"Stellar characterization recorded (+{ScienceRewards.ScienceRewardStellarCharacterization:F0} Science).");
            }
            if (lastScanJackpotPlanetCount > 1)
            {
                GUILayout.Label($"JACKPOT: {lastScanJackpotPlanetCount} confirmed planets in one campaign!");
            }
            GUILayout.Label(lastScanScienceAwarded > 0f
                ? $"+{lastScanScienceAwarded:F0} Science"
                : "Already surveyed. No new Science.");
            GUILayout.Space(6);
        }

        // --- Career progression: instrument unlock economy --------------------
        // Sandbox/science-sandbox: every instrument is available from the start
        // and scans are free, same as before this feature existed, gated on the
        // same CareerFogActive check as the star fog above, no separate setting.
        // Unlocking happens exclusively through the locked rows inside the
        // observatory selector: a separate bottom-of-column "program" table
        // duplicated the same rows and was removed as redundant.

        /// <summary>
        /// Start Observation with the career telescope-time cost attached:
        /// shows the price on the button, refuses when the program can't afford
        /// it. Telescope time is what stops scans from being free spam.
        /// </summary>
        void DrawStartObservationButton()
        {
            // The visual instruments point anywhere on the sky, so the only mismatch left is a
            // detection instrument handed a solar-system body: a transit or radial-velocity run
            // needs a catalogue star, and a planet is not one.
            bool methodIsPhotography = SelectedInstrument.Method == DetectionMethod.SolarSystemPhotography;
            if (selectedPhotographyTarget.IsBody && !methodIsPhotography)
            {
                GUILayout.Label($"Observation impossible: {SelectedInstrument.DisplayName} can't observe solar-system bodies. " +
                                 "Switch to the amateur astrograph (RC20) in the observatory selector above.", smallCaptionStyle);
                return;
            }
            if (methodIsPhotography && !selectedPhotographyTarget.HasTarget)
            {
                GUILayout.Label("Observation impossible: nothing is pointed at. " +
                                 "Click a target on the sky chart, or enter coordinates.", smallCaptionStyle);
                return;
            }

            double scanCost = CareerFogActive ? SelectedInstrument.ScanCostFunds : 0.0;
            if (scanCost <= 0.0)
            {
                if (GUILayout.Button("Start Observation", GUILayout.Height(32)))
                {
                    StartObservation();
                }
                return;
            }

            double funds = Funding.Instance != null ? Funding.Instance.Funds : 0.0;
            bool affordable = funds >= scanCost;
            GUI.enabled = affordable;
            if (GUILayout.Button($"Start Observation  (-{scanCost:N0} Funds telescope time)", GUILayout.Height(32)))
            {
                StartObservation();
            }
            GUI.enabled = true;
            if (!affordable)
            {
                GUILayout.Label($"Insufficient Funds for {SelectedInstrument.Name} telescope time ({funds:N0} available).", smallCaptionStyle);
            }
        }

        private static bool IsInstrumentUnlocked(InstrumentSpec instrument)
        {
            if (!CareerFogActive) return true;
            if (instrument.UnlockedByDefault) return true;
            ExoInstrumentsScenario scenario = ExoInstrumentsScenario.Instance;
            return scenario != null && scenario.IsInstrumentUnlocked(instrument.Name);
        }

        /// <summary>
        /// A locked instrument's row in the observatory dropdown: name, price,
        /// the Science track record required, and an Unlock button gated on
        /// actually being able to afford both. No selection possible until unlocked.
        /// </summary>
        void DrawLockedInstrumentRow(InstrumentSpec instrument)
        {
            ExoInstrumentsScenario scenario = ExoInstrumentsScenario.Instance;
            double earnedScience = scenario?.TotalScienceEarned ?? 0.0;
            double funds = Funding.Instance != null ? Funding.Instance.Funds : 0.0;

            bool scienceMet = earnedScience >= instrument.UnlockScienceThreshold;
            bool fundsMet = funds >= instrument.UnlockCostFunds;

            string requirement;
            if (scienceMet && fundsMet)
            {
                requirement = "ready to unlock";
            }
            else
            {
                var missing = new List<string>(2);
                if (!scienceMet) missing.Add($"{instrument.UnlockScienceThreshold - earnedScience:N0} more Science");
                if (!fundsMet) missing.Add($"{instrument.UnlockCostFunds - funds:N0} more Funds");
                requirement = "needs " + string.Join(" and ", missing);
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Locked: {instrument.DisplayName} ({instrument.UnlockCostFunds:N0} Funds, {requirement})");
            GUILayout.FlexibleSpace();
            GUI.enabled = scienceMet && fundsMet && scenario != null;
            if (GUILayout.Button("Unlock", GUILayout.Width(70), GUILayout.Height(22)))
            {
                Funding.Instance.AddFunds(-instrument.UnlockCostFunds, TransactionReasons.RnDPartPurchase);
                scenario.MarkInstrumentUnlocked(instrument.Name);
                int idx = Array.IndexOf(Observatories.All, instrument);
                if (idx >= 0) SelectObservatory(idx);
                observatoryMenuOpen = false;
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        /// <summary>Catalog-status report line, decoy-aware, a background star has no planet status to report.</summary>
        private static string CatalogStatusLine(StarTarget target)
        {
            if (!target.HasPlanet) return "Catalog status: no catalogued planet on this star";
            string statusNote = target.Status == PlanetStatus.Retracted
                ? " (literature has since retracted this claim)"
                : "";
            return $"Catalog status: {target.Status}{statusNote}";
        }

        void DrawTransitObservation()
        {
            double elapsedDisplayDays = ToDisplayDays(session.LastSampleUt - session.StartUt);
            int transitingCount = CountTransiting(session.SystemPlanets);

            GUILayout.Label($"Observing: {GetDisplayName(session.Target)}");
            // "N transiting planets" is catalog truth, withheld until identified.
            if (transitingCount > 1 && !IsIdentityHidden(session.Target))
            {
                GUILayout.Label($"Host system: {transitingCount} known transiting planets superpose on this light curve.");
            }
            GUILayout.Label($"Observatory: {session.Instrument.Name}");
            GUILayout.Label($"Exposure: {session.Instrument.CadenceSeconds:F0}s");
            GUILayout.Label($"Elapsed: {elapsedDisplayDays:F2} days");
            GUILayout.Label($"Points collected: {session.Samples.Count}");
            if (session.IsRunning) DrawObservingConditionsLine(session.Instrument, session.CurrentConditions);

            DrawPlot(rawPlotTexture, rawPlotRange, 0.0, elapsedDisplayDays, "time since start", GetDisplayName(session.Target));

            if (session.IsRunning)
            {
                GUILayout.Label("Engage time warp to accelerate data collection.");
                if (GUILayout.Button("Stop Observation"))
                {
                    session.Stop();
                    RefreshRawPlotTexture();
                }
                return;
            }

            if (lastTransitStages == null)
            {
                GUILayout.Space(10);

                bool hasEnoughData = session.Samples.Count >= TransitDetector.MinSampleCount;
                if (!hasEnoughData)
                {
                    GUILayout.Label($"Need at least {TransitDetector.MinSampleCount} points to analyze " +
                                     $"(currently {session.Samples.Count}). Keep warping.");
                }

                GUI.enabled = hasEnoughData && transitAnalysisTask == null;
                if (GUILayout.Button(transitAnalysisTask != null ? "Analyzing..." : "Run Detection Analysis"))
                {
                    StartTransitAnalysis();
                }
                GUI.enabled = true;
                if (transitAnalysisTask != null)
                {
                    float elapsed = Time.realtimeSinceStartup - transitAnalysisStartRealtime;
                    GUILayout.Label($"Searching {transitAnalysisSampleCount} points for transits, still running ({elapsed:F0}s elapsed). " +
                                     "A large dataset can legitimately take several minutes; the game itself isn't frozen.", smallCaptionStyle);
                }

                if (GUILayout.Button("New Observation"))
                {
                    ResetSession();
                }
                return;
            }

            for (int i = 0; i < lastTransitStages.Count; i++)
            {
                DetectionResult stageResult = lastTransitStages[i].Result;
                if (stageResult.InsufficientData || i >= transitPhaseFoldedTextures.Count) continue;
                // The final, below-threshold stage keeps its folded plot too;
                // seeing residual points fold into nothing is the visual proof the
                // masking search bottomed out, same idiom as the RV stages. That
                // stage isn't a confirmed planet, so its title must say so instead
                // of reading as "signal N" like the genuine detections above it;
                // otherwise a below-threshold noise fold looks like a second planet.
                string plotTitle;
                if (!stageResult.Detected)
                {
                    plotTitle = "Residual search (no further signal)";
                }
                else
                {
                    plotTitle = lastTransitStages.Count > 1
                        ? $"{GetDisplayName(session.Target)}: signal {i + 1}" + (i > 0 ? " (masked residual search)" : "")
                        : GetDisplayName(session.Target);
                }
                DrawPlot(transitPhaseFoldedTextures[i], transitPhaseFoldedRanges[i], 0.0, stageResult.BestPeriodDays, "phase", plotTitle);
            }

            DrawTransitScanReport(lastTransitStages);
            DrawTtvSection();
            GUILayout.Space(10);
            if (GUILayout.Button("New Observation"))
            {
                ResetSession();
            }
        }

        static int CountTransiting(List<StarTarget> planets)
        {
            int n = 0;
            foreach (var p in planets) if (p.IsTransiting) n++;
            return n;
        }

        /// <summary>Bundles everything the background transit-analysis Task computes, applied to fields together once it lands.</summary>
        private struct TransitAnalysisPayload
        {
            public List<TransitDetectionStage> Stages;
            public TransitTimingVariations.TtvAnalysisResult Ttv;
        }

        // In flight while the transit "Run Detection Analysis" button's work
        // (iterative multi-planet box search over the whole light curve, plus
        // the O-C timing search) runs off the main thread; both were
        // previously synchronous and, on a long baseline with many samples,
        // took long enough to read as a multi-second game freeze. Neither
        // TransitDetector.DetectMultiple nor TransitTimingVariations.Analyze
        // touches any UnityEngine.Object API, so both are safe here; only the
        // texture refresh and the career-reward bookkeeping after landing
        // have to happen on the main thread.
        private Task<TransitAnalysisPayload> transitAnalysisTask;
        // Captured at task start so a stale result (the player hit "New
        // Observation" and started a fresh session while this one was still
        // computing) gets discarded instead of overwriting the new session.
        private ObservationSession transitAnalysisSession;
        // Real time and sample count captured at task start, purely to give
        // the "Analyzing..." label something concrete to say; a box search
        // over thousands of samples can legitimately run for minutes, and a
        // static caption with no elapsed time reads exactly like a freeze.
        private float transitAnalysisStartRealtime;
        private int transitAnalysisSampleCount;

        void StartTransitAnalysis()
        {
            if (transitAnalysisTask != null || session == null) return;
            var samples = session.Samples;
            transitAnalysisStartRealtime = Time.realtimeSinceStartup;
            transitAnalysisSampleCount = samples.Count;
            transitAnalysisSession = session;

            transitAnalysisTask = Task.Run(() =>
            {
                var stages = TransitDetector.DetectMultiple(samples);
                TransitTimingVariations.TtvAnalysisResult ttv = null;
                if (!stages[0].Result.InsufficientData && stages[0].Result.Detected)
                {
                    ttv = TransitTimingVariations.Analyze(samples, stages[0].Result);
                }
                return new TransitAnalysisPayload { Stages = stages, Ttv = ttv };
            });
        }

        /// <summary>
        /// Applies a completed background transit analysis: textures and the
        /// career bookkeeping, including the multi-planet jackpot (confirmed
        /// count capped by how many catalog planets actually transit, same
        /// anti-farming reasoning as the RV path) and the one-time TTV award.
        /// The only part of the transit-analysis pipeline allowed to touch a
        /// Texture2D or a KSP API.
        /// </summary>
        void PollTransitAnalysisTask()
        {
            if (transitAnalysisTask == null || !transitAnalysisTask.IsCompleted) return;
            if (transitAnalysisTask.IsFaulted)
            {
                Debug.LogError("[ExoInstruments] Transit analysis task failed: " + transitAnalysisTask.Exception);
                transitAnalysisTask = null;
                return;
            }
            var payload = transitAnalysisTask.Result;
            transitAnalysisTask = null;
            if (session != transitAnalysisSession) return; // stale: a new session started while this one was in flight
            transitAnalysisSession = null;

            lastTransitStages = payload.Stages;
            if (lastTransitStages[0].Result.InsufficientData) return;

            RefreshTransitPhaseFoldedTextures();

            lastTtvResult = payload.Ttv;
            RefreshTtvPlotTexture();

            int detectedStageCount = 0;
            foreach (var stage in lastTransitStages)
            {
                if (stage.Result.Detected) detectedStageCount++;
            }
            int realTransiting = CountTransiting(session.SystemPlanets);
            int confirmed = Math.Min(detectedStageCount, realTransiting);
            RegisterScanCompleted(session.Target, session.Instrument, confirmed > 0, confirmed);
            RegisterTtvOutcome();
        }

        /// <summary>
        /// One-time TTV Science: requires the measured O-C sinusoid to clear the
        /// threshold AND the catalog to actually carry a perturbing companion for
        /// some transiting member of this system; an O-C wobble fit onto pure
        /// noise can't be farmed, same truth-gating as the detection bonus.
        /// </summary>
        private float lastTtvScienceAwarded;

        void RegisterTtvOutcome()
        {
            lastTtvScienceAwarded = 0f;
            if (!CareerFogActive) return;
            if (lastTtvResult == null || !lastTtvResult.Detected) return;

            bool truthHasTtv = false;
            foreach (var planet in session.SystemPlanets)
            {
                if (!planet.IsTransiting) continue;
                if (TransitTimingVariations.ComputeSignal(planet, session.SystemPlanets).IsSignificant)
                {
                    truthHasTtv = true;
                    break;
                }
            }
            if (!truthHasTtv) return;

            ExoInstrumentsScenario scenario = ExoInstrumentsScenario.Instance;
            if (scenario == null || !scenario.MarkTtvRewarded(session.Target.CatalogKey)) return;

            lastTtvScienceAwarded = ScienceRewards.ScienceRewardTtvDetection;
            scenario.AddEarnedScience(lastTtvScienceAwarded);
            if (ResearchAndDevelopment.Instance != null)
            {
                ResearchAndDevelopment.Instance.AddScience(lastTtvScienceAwarded, TransactionReasons.ScienceTransmission);
            }
        }

        /// <summary>
        /// Transit-timing section of the scan report: the O-C series (each
        /// measured mid-transit against the linear ephemeris) and the sinusoid
        /// search verdict. A detected TTV is dynamical evidence of a companion
        /// tugging the transiter, the way planets that never transit have been
        /// discovered, and the way TRAPPIST-1's planets were weighed.
        /// </summary>
        void DrawTtvSection()
        {
            if (lastTtvResult == null) return;

            GUILayout.Space(10);
            GUILayout.Label("Transit timing (O-C)", sectionHeaderStyle);

            if (lastTtvResult.EpochCount < TransitTimingVariations.MinMeasuredEpochs)
            {
                GUILayout.Label($"Only {lastTtvResult.EpochCount} individual transit mid-times measurable " +
                                 $"(need {TransitTimingVariations.MinMeasuredEpochs}+ for a timing analysis). " +
                                 "A longer baseline with more covered transits would enable one.", smallCaptionStyle);
                return;
            }

            DrawRvPlot(ttvPlotTexture, ttvPlotRange, 0.0,
                ToDisplayDays(lastTtvResult.Measurements[lastTtvResult.EpochCount - 1].ExpectedCenterUt
                              - lastTtvResult.Measurements[0].ExpectedCenterUt),
                "time since first measured transit", "Transit timing O-C", "O-C [minutes]");

            GUILayout.Label($"Measured mid-times: {lastTtvResult.EpochCount}   O-C scatter: {lastTtvResult.RmsSeconds / 60.0:F1} min");
            if (lastTtvResult.Detected)
            {
                GUILayout.Label($"TTV DETECTED: amplitude {lastTtvResult.BestAmplitudeSeconds / 60.0:F1} min, " +
                                 $"super-period {ToDisplayDays(lastTtvResult.BestSuperPeriodDays * 86400.0):F0} days, SNR {lastTtvResult.Snr:F1}.");
                GUILayout.Label("The transits are not periodic: something with mass is tugging this planet. " +
                                 "That is a gravitational companion detection, whether or not the perturber ever transits.", smallCaptionStyle);
                if (lastTtvScienceAwarded > 0f)
                {
                    GUILayout.Label($"+{lastTtvScienceAwarded:F0} Science (transit timing variations)");
                }
            }
            else
            {
                GUILayout.Label($"No periodic timing signal above SNR {TransitTimingVariations.DetectionSnrThreshold:F0} " +
                                 $"(best candidate SNR {lastTtvResult.Snr:F1}). Mid-times are consistent with a linear ephemeris.", smallCaptionStyle);
            }
        }

        /// <summary>Fixed real-time top-up used by the "Warp +N days" button, a physically meaningful week of extra epochs, independent of display convention.</summary>
        private const double RvTopUpRealDays = 7.0;

        void DrawRvObservation()
        {
            double elapsedDisplayDays = ToDisplayDays(rvSession.LastSampleUt - rvSession.StartUt);
            int rvDetectableCount = CountRvDetectable(rvSession.SystemPlanets);

            GUILayout.Label($"Observing: {GetDisplayName(rvSession.Target)}");
            // "N known planets" is catalog truth, withheld until the target is identified.
            if (rvDetectableCount > 1 && !IsIdentityHidden(rvSession.Target))
            {
                GUILayout.Label($"Host system: {rvDetectableCount} known planets contribute to the measured reflex velocity.");
            }
            GUILayout.Label($"Observatory: {rvSession.Instrument.Name}");
            GUILayout.Label($"Epoch cadence: {rvSession.Instrument.CadenceSeconds / 3600.0:F1}h");
            GUILayout.Label($"Elapsed: {elapsedDisplayDays:F2} days");
            GUILayout.Label($"Points collected: {rvSession.Samples.Count}");
            if (rvSession.IsRunning) DrawObservingConditionsLine(rvSession.Instrument, rvSession.CurrentConditions);

            DrawRvPlot(rvRawPlotTexture, rvRawPlotRange, 0.0, elapsedDisplayDays, "time since start", GetDisplayName(rvSession.Target));

            if (rvSession.IsRunning)
            {
                double ut = Planetarium.GetUniversalTime();

                if (IsIdentityHidden(rvSession.Target))
                {
                    // The suggested baseline is computed from the catalog period,
                    // exactly the information a blind survey doesn't have. The
                    // player samples as long as they judge useful, like a real
                    // blind RV campaign.
                    GUILayout.Label("Unidentified target: no catalog period to plan a baseline against. Sample as long as you judge useful.");
                    if (GUILayout.Button($"Warp +{ToDisplayDays(RvTopUpRealDays * 86400.0):F0} days"))
                    {
                        BetterTimeWarpIntegration.WarpTo(ut + RvTopUpRealDays * 86400.0);
                    }
                }
                else
                {
                    double neededBaselineDays = RvDetector.EstimateRequiredBaselineDays(LongestRvPeriodDays(rvSession.SystemPlanets), rvSession.Instrument.CadenceSeconds);
                    double baselineTargetUt = rvSession.StartUt + neededBaselineDays * 86400.0;
                    double daysRemainingDisplay = ToDisplayDays(Math.Max(0.0, baselineTargetUt - ut));

                    GUILayout.Label($"Suggested baseline: {ToDisplayDays(neededBaselineDays * 86400.0):F1} days to resolve the catalog period at this cadence.");

                    GUILayout.BeginHorizontal();
                    GUI.enabled = baselineTargetUt > ut;
                    if (GUILayout.Button(baselineTargetUt > ut ? $"Warp to suggested baseline ({daysRemainingDisplay:F1}d left)" : "Suggested baseline reached"))
                    {
                        BetterTimeWarpIntegration.WarpTo(baselineTargetUt);
                    }
                    GUI.enabled = true;
                    if (GUILayout.Button($"Warp +{ToDisplayDays(RvTopUpRealDays * 86400.0):F0} days"))
                    {
                        BetterTimeWarpIntegration.WarpTo(ut + RvTopUpRealDays * 86400.0);
                    }
                    GUILayout.EndHorizontal();
                }

                DrawRmSchedulingLine(ut);

                if (GUILayout.Button("Stop Observation"))
                {
                    rvSession.Stop();
                    RefreshRvRawPlotTexture();
                }
                return;
            }

            if (lastRvStages == null)
            {
                GUILayout.Space(10);

                bool hasEnoughData = rvSession.Samples.Count >= RvDetector.MinSampleCount;
                if (!hasEnoughData)
                {
                    GUILayout.Label($"Need at least {RvDetector.MinSampleCount} points to analyze " +
                                     $"(currently {rvSession.Samples.Count}). Keep warping.");
                }

                GUI.enabled = hasEnoughData && rvAnalysisTask == null;
                if (GUILayout.Button(rvAnalysisTask != null ? "Analyzing..." : "Run Detection Analysis"))
                {
                    StartRvAnalysis();
                }
                GUI.enabled = true;
                if (rvAnalysisTask != null)
                {
                    float elapsed = Time.realtimeSinceStartup - rvAnalysisStartRealtime;
                    GUILayout.Label($"Searching {rvAnalysisSampleCount} points for periodic signals, still running ({elapsed:F0}s elapsed). " +
                                     "A large dataset can legitimately take several minutes; the game itself isn't frozen.", smallCaptionStyle);
                }

                if (GUILayout.Button("New Observation"))
                {
                    ResetSession();
                }
                return;
            }

            for (int i = 0; i < lastRvStages.Count; i++)
            {
                RvDetectionResult stageResult = lastRvStages[i].Result;
                if (stageResult.InsufficientData || i >= rvPhaseFoldedTextures.Count) continue;
                // The final, below-threshold stage still gets its folded plot: seeing
                // pure-noise residuals fold into nothing is the visual proof that the
                // prewhitening bottomed out, same information a real survey would show.
                string plotTitle;
                if (!stageResult.Detected)
                {
                    plotTitle = "Residual search (no further signal)";
                }
                else
                {
                    string hostLabel = IsIdentityHidden(rvSession.Target)
                        ? GetDisplayName(rvSession.Target)
                        : rvSession.Target.HostStarName ?? rvSession.Target.Name;
                    plotTitle = lastRvStages.Count > 1
                        ? $"{hostLabel}: signal {i + 1}" + (i > 0 ? " (residuals)" : "")
                        : GetDisplayName(rvSession.Target);
                }
                DrawRvPlot(rvPhaseFoldedTextures[i], rvPhaseFoldedRanges[i], 0.0, stageResult.BestPeriodDays, "phase", plotTitle);
            }

            DrawRvScanReport(lastRvStages);
            DrawRmSection();
            GUILayout.Space(10);
            if (GUILayout.Button("New Observation"))
            {
                ResetSession();
            }
        }

        // --- Rossiter-McLaughlin: scheduling and analysis ----------------------

        /// <summary>Next fully observable transit window among the session's schedulable planets, cached, recomputed on the 1s plot-refresh throttle (the search re-runs the conditions evaluator across upcoming transits).</summary>
        private double rmNextTransitUt = double.NaN;
        private StarTarget rmNextTransitPlanet;
        private float lastRmScienceAwarded;

        void RefreshRmSchedule(double ut)
        {
            rmNextTransitUt = double.NaN;
            rmNextTransitPlanet = null;
            if (rvSession == null || rvSession.TransitBurstPlanets.Count == 0) return;

            ImagingObserverContext observer = BuildImagingObserverContext();
            double best = double.PositiveInfinity;
            foreach (var planet in rvSession.TransitBurstPlanets)
            {
                double centerUt = RossiterMcLaughlin.NextObservableTransitCenterUt(planet, ut, observer);
                if (!double.IsNaN(centerUt) && centerUt < best)
                {
                    best = centerUt;
                    rmNextTransitPlanet = planet;
                }
            }
            rmNextTransitUt = best;
        }

        /// <summary>
        /// Live RM status while the RV session runs: the high-cadence sequence in
        /// progress, or the next observable transit window with a warp shortcut.
        /// Only shown when scheduling is possible at all (identified target with
        /// a transiting companion; see GetRmSchedulablePlanets).
        /// </summary>
        void DrawRmSchedulingLine(double ut)
        {
            if (rvSession.TransitBurstPlanets.Count == 0) return;

            if (rvSession.InTransitBurst)
            {
                GUILayout.Label("TRANSIT SEQUENCE in progress: sampling the transit window at " +
                                 $"{RvObservationSession.RmBurstCadenceSeconds / 60.0:F0}-min cadence for the Rossiter-McLaughlin anomaly.");
                return;
            }
            if (double.IsNaN(rmNextTransitUt)) return;
            if (double.IsPositiveInfinity(rmNextTransitUt))
            {
                GUILayout.Label("No fully observable transit window found in the transits ahead. " +
                                 "Rossiter-McLaughlin scheduling is off the table from this site for now.", smallCaptionStyle);
                return;
            }

            double halfWindowSeconds = (rmNextTransitPlanet.EstimatedTransitDurationHours ?? 3.0) * 3600.0;
            // Land one burst epoch before the window opens so ingress is covered.
            double warpTargetUt = rmNextTransitUt - halfWindowSeconds - RvObservationSession.RmBurstCadenceSeconds;
            GUILayout.Label($"Next observable transit of {rmNextTransitPlanet.Name} in {(rmNextTransitUt - ut) / 3600.0:F1} h. " +
                             "epochs through its window are taken at high cadence (Rossiter-McLaughlin sequence).", smallCaptionStyle);
            if (warpTargetUt > ut)
            {
                if (GUILayout.Button($"Warp to transit window ({(warpTargetUt - ut) / 3600.0:F1} h)"))
                {
                    BetterTimeWarpIntegration.WarpTo(warpTargetUt);
                }
            }
        }

        /// <summary>Bundles everything the background RV-analysis Task computes, applied to fields together once it lands.</summary>
        private struct RvAnalysisPayload
        {
            public List<RvDetectionStage> Stages;
            public RossiterMcLaughlin.RmFitResult Rm;
            public StarTarget RmPlanet;
        }

        // In flight while the RV "Run Detection Analysis" button's work (the
        // Lomb-Scargle-style prewhitening search, up to RvDetector.MaxPlanetsPerSearch
        // passes over the whole series, plus the Rossiter-McLaughlin fit on the
        // residuals) runs off the main thread, same freeze this caused as the
        // transit path, same fix. Career-fog and scheduling ("is this target
        // identified, which planets can we fit RM for") depend on
        // ExoInstrumentsScenario.Instance and must be read on the main thread
        // before the task starts; RvDetector.DetectMultiple and
        // RossiterMcLaughlin.Fit themselves touch no UnityEngine.Object API.
        private Task<RvAnalysisPayload> rvAnalysisTask;
        private RvObservationSession rvAnalysisSession;
        private float rvAnalysisStartRealtime;
        private int rvAnalysisSampleCount;

        void StartRvAnalysis()
        {
            if (rvAnalysisTask != null || rvSession == null) return;
            var samples = rvSession.Samples;
            var target = rvSession.Target;
            List<StarTarget> rmSchedulable = IsIdentityHidden(target) ? new List<StarTarget>() : GetRmSchedulablePlanets(target);
            rvAnalysisSession = rvSession;
            rvAnalysisStartRealtime = Time.realtimeSinceStartup;
            rvAnalysisSampleCount = samples.Count;

            rvAnalysisTask = Task.Run(() =>
            {
                var stages = RvDetector.DetectMultiple(samples);
                RossiterMcLaughlin.RmFitResult rm = null;
                StarTarget rmPlanet = null;

                if (!stages[0].Result.InsufficientData)
                {
                    var residuals = stages[stages.Count - 1].SearchedSamples;
                    if (residuals != null)
                    {
                        foreach (var planet in rmSchedulable)
                        {
                            var fit = RossiterMcLaughlin.Fit(residuals, planet);
                            if (rm == null || fit.Snr > rm.Snr)
                            {
                                rm = fit;
                                rmPlanet = planet;
                            }
                        }
                    }
                }
                return new RvAnalysisPayload { Stages = stages, Rm = rm, RmPlanet = rmPlanet };
            });
        }

        /// <summary>
        /// Applies a completed background RV analysis: the career bookkeeping
        /// (the multi-planet jackpot, same anti-farming cap as the transit path)
        /// and the Rossiter-McLaughlin outcome. The only part of the RV-analysis
        /// pipeline allowed to touch a KSP API.
        /// </summary>
        void PollRvAnalysisTask()
        {
            if (rvAnalysisTask == null || !rvAnalysisTask.IsCompleted) return;
            if (rvAnalysisTask.IsFaulted)
            {
                Debug.LogError("[ExoInstruments] RV analysis task failed: " + rvAnalysisTask.Exception);
                rvAnalysisTask = null;
                return;
            }
            var payload = rvAnalysisTask.Result;
            rvAnalysisTask = null;
            if (rvSession != rvAnalysisSession) return; // stale: a new session started while this one was in flight
            rvAnalysisSession = null;

            lastRvStages = payload.Stages;
            RefreshRvPhaseFoldedTextures();
            if (lastRvStages[0].Result.InsufficientData) return;

            // One campaign scans the whole system, so the reveal and the
            // (single, per-host) detection bonus are host-level. Count of "real"
            // planets confirmed is capped at how many catalog planets are
            // actually RV-detectable in this system, never higher, even if
            // prewhitening reports more Detected stages. This matters because a
            // single eccentric orbit's un-subtracted harmonics (P/2, P/3) can
            // masquerade as extra "detected" stages (see
            // RvDetectionResult.LikelyHarmonicOfPeriodDays and the known
            // BLS-harmonic-alias flaw); without the cap, a one-planet eccentric
            // system could farm jackpot Science off its own residual harmonics.
            int detectedStageCount = 0;
            foreach (var stage in lastRvStages)
            {
                if (stage.Result.Detected) detectedStageCount++;
            }
            int realDetectable = CountRvDetectable(rvSession.SystemPlanets);
            int realPlanetsConfirmed = Math.Min(detectedStageCount, realDetectable);
            RegisterScanCompleted(rvSession.Target, rvSession.Instrument, realPlanetsConfirmed > 0, realPlanetsConfirmed);

            lastRmResult = payload.Rm;
            lastRmPlanet = payload.RmPlanet;
            RegisterRmOutcome();
        }

        /// <summary>
        /// One-time Rossiter-McLaughlin Science: requires a confirmed anomaly
        /// fit (see RvAnalysisPayload.Rm, computed in the background task) on a
        /// target that was already identified when the analysis started.
        /// </summary>
        void RegisterRmOutcome()
        {
            lastRmScienceAwarded = 0f;
            if (lastRmResult == null || !lastRmResult.Detected) return;
            if (!CareerFogActive) return;
            ExoInstrumentsScenario scenario = ExoInstrumentsScenario.Instance;
            if (scenario == null || !scenario.MarkRmRewarded(rvSession.Target.CatalogKey)) return;

            lastRmScienceAwarded = ScienceRewards.ScienceRewardRossiterMcLaughlin;
            scenario.AddEarnedScience(lastRmScienceAwarded);
            if (ResearchAndDevelopment.Instance != null)
            {
                ResearchAndDevelopment.Instance.AddScience(lastRmScienceAwarded, TransactionReasons.ScienceTransmission);
            }
        }

        void DrawRmSection()
        {
            if (lastRmResult == null || lastRmPlanet == null) return;

            GUILayout.Space(10);
            GUILayout.Label("Rossiter-McLaughlin (spin-orbit geometry)", sectionHeaderStyle);

            if (lastRmResult.InsufficientData)
            {
                GUILayout.Label($"Only {lastRmResult.InTransitEpochs} epochs landed inside {lastRmPlanet.Name}'s transit " +
                                 $"(need {RossiterMcLaughlin.MinInTransitEpochs}+). Warp to a transit window during the campaign " +
                                 "so the high-cadence sequence can sample the anomaly.", smallCaptionStyle);
                return;
            }

            GUILayout.Label($"Planet: {lastRmPlanet.Name}   In-transit epochs: {lastRmResult.InTransitEpochs}");
            GUILayout.Label($"Anomaly amplitude: {lastRmResult.AnomalyAmplitudeMps:F1} m/s   SNR: {lastRmResult.Snr:F1}");
            if (lastRmResult.Detected)
            {
                GUILayout.Label($"Projected spin-orbit angle: lambda = {lastRmResult.MeasuredLambdaDeg:F0} +/- {lastRmResult.LambdaUncertaintyDeg:F0} deg   " +
                                 $"v sin(i) = {lastRmResult.MeasuredVsiniMps:F0} m/s");
                GUILayout.Label(Math.Abs(lastRmResult.MeasuredLambdaDeg) < 30.0
                    ? "The orbit is prograde and roughly aligned with the stellar spin, consistent with quiet disk migration."
                    : "The orbit is significantly misaligned with the stellar spin: fossil evidence of a violent dynamical history.",
                    smallCaptionStyle);
                if (lastRmScienceAwarded > 0f)
                {
                    GUILayout.Label($"+{lastRmScienceAwarded:F0} Science (Rossiter-McLaughlin measurement)");
                }
            }
            else
            {
                GUILayout.Label($"Anomaly below SNR {RossiterMcLaughlin.DetectionSnrThreshold:F0}. More transit sequences " +
                                 "(or a slower-rotating star's smaller anomaly is simply beyond this precision).", smallCaptionStyle);
            }
        }

        void DrawImagingObservation()
        {
            DirectImagingAssessment assessment = imagingSession.Assessment;
            double effectiveSeconds = imagingSession.EffectiveExposureSeconds;
            double currentSnr = DirectImagingSimulator.ComputeSnr(assessment, effectiveSeconds);

            GUILayout.Label($"Observing: {GetDisplayName(imagingSession.Target)}");
            GUILayout.Label($"Observatory: {imagingSession.Instrument.Name}");
            GUILayout.Label($"On-sky integration: {effectiveSeconds / 3600.0:F1} h (zenith-equivalent)   " +
                             $"Elapsed: {ToDisplayDays(imagingSession.ElapsedSeconds):F2} days");
            GUILayout.Label(DescribeImagingConditions(imagingSession.CurrentConditions));
            // Live SNR is computed from catalog truth (companion contrast); on an
            // unidentified career target it would answer "is there a planet here?"
            // for free. The frame itself stays visible: that IS the observation.
            if (assessment.HasRequiredData && assessment.Resolvable && assessment.SignalPresent
                && !IsIdentityHidden(imagingSession.Target))
            {
                GUILayout.Label($"Live SNR: {currentSnr:F1}  (detection at {DirectImagingSimulator.DetectionSnrThreshold:F0})");
            }

            DrawImagingFrame(assessment);

            if (imagingSession.IsRunning)
            {
                double ut = Planetarium.GetUniversalTime();

                // Downtime shortcut: when the dome is closed or the target is too
                // low, offer a jump to the next moment integration actually accrues.
                if (!imagingSession.CurrentConditions.Observable)
                {
                    if (double.IsInfinity(imagingNextWindowUt))
                    {
                        GUILayout.Label("This target is never observable from KSC: it either stays below the " +
                                         $"{ImagingObservingConditions.MinTelescopeAltitudeDeg:F0} deg telescope limit or never shares the sky with darkness.");
                    }
                    else if (!double.IsNaN(imagingNextWindowUt) && imagingNextWindowUt > ut)
                    {
                        if (GUILayout.Button($"Warp to next observing window ({(imagingNextWindowUt - ut) / 3600.0:F1} h)"))
                        {
                            BetterTimeWarpIntegration.WarpTo(imagingNextWindowUt);
                        }
                    }
                }

                if (IsIdentityHidden(imagingSession.Target))
                {
                    // Required-exposure predictions come from catalog truth
                    // (contrast, separation, or their absence), withheld on an
                    // unidentified target. Blind imaging means deciding yourself
                    // when the frame has gone deep enough.
                    GUILayout.Label("Unidentified target: required integration can't be predicted without prior data. " +
                                     "Integrate until you're convinced either way.");
                    if (GUILayout.Button("Warp +6 h"))
                    {
                        BetterTimeWarpIntegration.WarpTo(ut + 6.0 * 3600.0);
                    }
                }
                else
                {
                    double neededSeconds = DirectImagingSimulator.RequiredExposureSeconds(assessment);

                    if (double.IsInfinity(neededSeconds))
                    {
                        GUILayout.Label(assessment.Resolvable
                            ? "No detectable signal at any exposure. Stop whenever you're convinced."
                            : "Target is inside the diffraction limit. No exposure can resolve it.");
                    }
                    else
                    {
                        // Detection UT comes from forward-simulating the actual
                        // nights and airmass ahead, not needed/elapsed arithmetic.
                        bool reached = effectiveSeconds >= neededSeconds;
                        GUILayout.BeginHorizontal();
                        if (double.IsInfinity(imagingDetectionUt))
                        {
                            GUILayout.Label($"5-sigma needs {neededSeconds / 3600.0:F0} h on-sky. Not reachable in any sane campaign from this site.");
                        }
                        else
                        {
                            GUI.enabled = !reached && !double.IsNaN(imagingDetectionUt);
                            if (GUILayout.Button(reached
                                    ? "5-sigma exposure reached"
                                    : $"Warp to 5-sigma exposure ({Math.Max(0.0, imagingDetectionUt - ut) / 3600.0:F1} h away)"))
                            {
                                BetterTimeWarpIntegration.WarpTo(imagingDetectionUt);
                            }
                            GUI.enabled = true;
                        }
                        if (GUILayout.Button("Warp +6 h"))
                        {
                            BetterTimeWarpIntegration.WarpTo(ut + 6.0 * 3600.0);
                        }
                        GUILayout.EndHorizontal();
                    }
                }

                if (GUILayout.Button("Stop Observation"))
                {
                    imagingSession.Stop();
                    RefreshImagingTexture();
                }
                return;
            }

            if (lastImagingResult == null)
            {
                GUILayout.Space(10);
                if (GUILayout.Button("Run Detection Analysis"))
                {
                    lastImagingResult = DirectImagingSimulator.Analyze(assessment, effectiveSeconds);
                    // Detected already implies a real, resolvable catalog companion
                    // (ComputeSnr returns 0 otherwise), no extra truth gate needed.
                    // Imaging additionally characterizes the star itself whenever a
                    // temperature is measurable; that's the payoff for pointing
                    // the ELT at a star with no companion to find.
                    RegisterScanCompleted(imagingSession.Target, imagingSession.Instrument, lastImagingResult.Detected,
                        stellarCharacterization: imagingSession.Target.EffectiveTempK.HasValue);
                }
                if (GUILayout.Button("New Observation"))
                {
                    ResetSession();
                }
                return;
            }

            DrawImagingScanReport(lastImagingResult);
            GUILayout.Space(10);
            if (GUILayout.Button("New Observation"))
            {
                ResetSession();
            }
        }

        /// <summary>Square image frame + scale caption; the star sits off-center by the per-target pointing offset.</summary>
        void DrawImagingFrame(DirectImagingAssessment assessment)
        {
            GUILayout.Space(6);
            GUILayout.Label(GetDisplayName(imagingSession.Target), plotTitleStyle);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            Rect frameRect = GUILayoutUtility.GetRect(ImagingTextureSize, ImagingTextureSize,
                GUILayout.Width(ImagingTextureSize), GUILayout.Height(ImagingTextureSize));
            if (imagingTexture != null)
            {
                GUI.DrawTexture(frameRect, imagingTexture);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (imagingSession.EffectiveExposureSeconds < DirectImagingTexture.MinStarlightExposureSeconds)
            {
                GUILayout.Label("Detector readout only: shutter closed, no photons collected yet. " +
                                 "The frame builds up once night-time integration begins.", smallCaptionStyle);
            }
            else
            {
                GUILayout.Label($"H-band (1.6 um), log-stretched. FOV: {imagingFovArcsec * 1000.0:F0} mas. " +
                                 "Dashed ring: diffraction limit. Spikes: 6 (pupil spider). " +
                                 "Color: blackbody temperature tint.", smallCaptionStyle);
            }
        }

        /// <summary>One-line live status of the observing gate: why integration is or isn't accruing right now.</summary>
        string DescribeImagingConditions(ImagingConditionsSnapshot c)
        {
            if (!c.IsNight)
            {
                return $"Dome closed for daytime (Sun at {c.SunAltitudeDeg:F0} deg; science resumes below " +
                       $"{ImagingObservingConditions.TwilightSunAltitudeDeg:F0} deg twilight).";
            }
            if (!c.TargetUp)
            {
                return $"Night, but target at {c.TargetAltitudeDeg:F0} deg altitude, below the " +
                       $"{ImagingObservingConditions.MinTelescopeAltitudeDeg:F0} deg telescope limit. Waiting for it to rise.";
            }
            string coordNote = c.HasTargetCoordinates
                ? $"target at {c.TargetAltitudeDeg:F0} deg altitude"
                : $"no sky coordinates on record, assuming {ImagingObservingConditions.FallbackAltitudeDeg:F0} deg altitude";
            return $"Integrating: {coordNote}, airmass {c.Airmass:F2}, efficiency {c.Efficiency * 100.0:F0}% of zenith.";
        }

        /// <summary>
        /// Builds the pure-C# observer description the imaging session needs:
        /// KSC's site, the home body's spin, and its orbit (which places the Sun).
        /// </summary>
        ImagingObserverContext BuildImagingObserverContext()
        {
            var ctx = new ImagingObserverContext
            {
                LatitudeDeg = ObservatorySite.LatitudeDeg,
                LongitudeDeg = ObservatorySite.LongitudeDeg,
            };
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null) return ctx; // no rotation, no sun orbit: permanent-night fallback
            ctx.BodyRotationPeriodSeconds = home.rotationPeriod;
            ctx.BodyInitialRotationDeg = home.initialRotation;
            if (home.orbit != null && home.orbit.period > 0)
            {
                ctx.HasSunOrbit = true;
                ctx.SunOrbitPeriodSeconds = home.orbit.period;
                ctx.SunMeanAnomalyAtEpochRad = home.orbit.meanAnomalyAtEpoch;
                ctx.SunOrbitEpochUt = home.orbit.epoch;
                ctx.SunLanPlusArgPeDeg = home.orbit.LAN + home.orbit.argumentOfPeriapsis;
            }

            // Every natural satellite of the home body (Mün and Minmus, stock)
            // becomes an occultation and sky-pollution source, read generically
            // so planet packs with different moons work unchanged.
            if (home.orbitingBodies != null && home.orbitingBodies.Count > 0)
            {
                var moons = new List<MoonContext>();
                foreach (CelestialBody moon in home.orbitingBodies)
                {
                    if (moon.orbit == null || moon.orbit.period <= 0) continue;
                    moons.Add(new MoonContext
                    {
                        Name = moon.bodyName,
                        OrbitPeriodSeconds = moon.orbit.period,
                        MeanAnomalyAtEpochRad = moon.orbit.meanAnomalyAtEpoch,
                        OrbitEpochUt = moon.orbit.epoch,
                        LanPlusArgPeDeg = moon.orbit.LAN + moon.orbit.argumentOfPeriapsis,
                        SemiMajorAxisMeters = moon.orbit.semiMajorAxis,
                        BodyRadiusMeters = moon.Radius,
                        Albedo = moon.albedo,
                    });
                }
                ctx.Moons = moons.ToArray();
            }
            return ctx;
        }

        /// <summary>
        /// One status line for the running transit/RV session: is the telescope
        /// actually collecting right now, and if not, why. Space-based
        /// instruments report their continuous coverage; that IS the feature
        /// the player paid for.
        /// </summary>
        void DrawObservingConditionsLine(InstrumentSpec instrument, ImagingConditionsSnapshot cond)
        {
            if (instrument.IsSpaceBased)
            {
                GUILayout.Label("Space-based: observing continuously, no day/night interruption.");
            }
            else if (cond.Observable)
            {
                GUILayout.Label($"On sky. Airmass {cond.Airmass:F2}{DescribeMoonNote(instrument, cond)}");
            }
            else if (!cond.IsNight)
            {
                GUILayout.Label("Daylight. Dome closed, waiting for night. Warp ahead.");
            }
            else if (cond.OccultedByMoon)
            {
                GUILayout.Label($"Target occulted by the {cond.OccultingMoonName}. Nothing gets through a moon; waiting for it to move on.");
            }
            else
            {
                GUILayout.Label($"Target below the {ImagingObservingConditions.MinTelescopeAltitudeDeg:F0} deg telescope limit. Waiting for it to rise.");
            }
        }

        /// <summary>
        /// Moonlight annotation for the on-sky status line. Only photometry pays
        /// the sky-noise tax (see MoonlightPollution), so only transit
        /// instruments get the warning, but a bright moon near anyone's target
        /// is still worth a mention at high levels.
        /// </summary>
        static string DescribeMoonNote(InstrumentSpec instrument, ImagingConditionsSnapshot cond)
        {
            if (cond.MoonSkyFactor <= 0.0 || cond.DominantMoon.Name == null) return "";
            bool paysNoise = instrument.Method == DetectionMethod.Transit && !instrument.IsSpaceBased;
            if (paysNoise && cond.MoonSkyFactor >= 0.2)
            {
                return $". {cond.DominantMoon.Name} up ({cond.DominantMoon.IlluminatedFraction * 100.0:F0}% lit, " +
                       $"{cond.DominantMoon.SeparationFromTargetDeg:F0} deg away): moonlit sky raising the noise floor";
            }
            if (cond.MoonSkyFactor >= 1.0)
            {
                return $". {cond.DominantMoon.Name} bright nearby (no effect on this method)";
            }
            return "";
        }

        /// <summary>
        /// Recomputes the forward-simulated campaign markers: next observing
        /// window (when currently blocked) and the UT at which effective exposure
        /// reaches 5-sigma (identified targets only; for hidden ones this would
        /// leak catalog truth). Throttled to the texture refresh cadence because
        /// each call re-integrates the nights ahead.
        /// </summary>
        void RefreshImagingPredictions()
        {
            imagingDetectionUt = double.NaN;
            imagingNextWindowUt = double.NaN;
            if (imagingSession == null || !imagingSession.IsRunning) return;

            if (!imagingSession.CurrentConditions.Observable)
            {
                imagingNextWindowUt = imagingSession.PredictNextObservableUt();
            }

            if (!IsIdentityHidden(imagingSession.Target))
            {
                double needed = DirectImagingSimulator.RequiredExposureSeconds(imagingSession.Assessment);
                imagingDetectionUt = double.IsInfinity(needed)
                    ? double.PositiveInfinity
                    : imagingSession.PredictUtForEffectiveExposure(needed, ImagingPredictionMaxWallSeconds);
            }
        }

        void DrawImagingScanReport(DirectImagingResult result)
        {
            DirectImagingAssessment a = result.Assessment;
            StarTarget target = imagingSession.Target;

            GUILayout.Space(10);
            GUILayout.Label("Scan Report", sectionHeaderStyle);
            DrawCareerScanOutcome(target);

            GUILayout.Label(CatalogStatusLine(target));
            GUILayout.Space(6);

            // A resolved AO image characterizes the star whether or not any
            // companion turns up; this section is the scan's guaranteed yield.
            GUILayout.Label("Stellar characterization", sectionHeaderStyle);
            if (target.EffectiveTempK.HasValue)
            {
                string tempSourceNote = target.EffectiveTempDerivedFromColor
                    ? "photometric estimate from the star's B-V color (Ballesteros 2012)"
                    : "catalog spectroscopy";
                GUILayout.Label($"Effective temperature: {target.EffectiveTempK.Value:F0} K, " +
                                 $"spectral class {StellarColor.SpectralClass(target.EffectiveTempK.Value)} ({tempSourceNote})");
            }
            else
            {
                GUILayout.Label("No temperature measurable: no catalog Teff and no color index on record for this star.");
            }
            GUILayout.Label($"Apparent magnitude: {target.ApparentMagnitude:F1}");
            GUILayout.Space(6);

            GUILayout.Label("Companion search", sectionHeaderStyle);
            if (!target.HasPlanet)
            {
                GUILayout.Label("No companion in the catalog for this star. The frame shows the stellar PSF and residual speckle only.");
                return;
            }
            if (!a.HasRequiredData)
            {
                GUILayout.Label($"Search inconclusive: {a.MissingDataReason}.");
                return;
            }

            GUILayout.Label($"Angular separation: {a.SeparationArcsec * 1000.0:F1} mas   Diffraction limit: {a.DiffractionLimitArcsec * 1000.0:F1} mas");
            if (!a.Resolvable)
            {
                GUILayout.Label("Not resolvable: the planet sits inside the PSF core. No exposure can separate it from the star.");
                return;
            }

            string tempSource = a.PlanetTempFromCatalog ? "catalog" : $"assumed equilibrium (A={DirectImagingSimulator.AssumedBondAlbedo:F1})";
            GUILayout.Label($"Planet temperature used: {a.PlanetTempKUsed:F0} K ({tempSource})");
            GUILayout.Label($"Contrast at 1.6 um: {a.ContrastRatio:E2}");
            GUILayout.Label($"5-sigma floor at this separation (1 h): {a.SpeckleFloor5Sigma1Hr:E2}");
            GUILayout.Label($"On-sky integration: {result.ExposureSeconds / 3600.0:F1} h (zenith-equivalent, airmass-weighted)   SNR: {result.Snr:F1}");
            GUILayout.Space(6);

            if (result.Detected)
            {
                GUILayout.Label($"DETECTED at {result.Snr:F1} sigma. Companion visible at {a.SeparationArcsec * 1000.0:F0} mas.");
            }
            else if (!a.SignalPresent)
            {
                GUILayout.Label("No companion detected, consistent with the retracted/absent-planet status above.");
            }
            else
            {
                GUILayout.Label($"Not detected (SNR {result.Snr:F1} < {DirectImagingSimulator.DetectionSnrThreshold:F0}). " +
                                 "More integration would help if the contrast sits above the deep floor.");
            }
        }

        /// <summary>
        /// Light curve plot with a title, numeric y-axis ticks (min/mid/max flux)
        /// and x-axis endpoints, mirrors DrawRvPlot's layout and idiom.
        /// </summary>
        void DrawPlot(Texture2D tex, LightCurvePlotRange range, double minXDays, double maxXDays, string xAxisLabel, string title)
        {
            const float yAxisGutter = 58f;
            const float xAxisRow = 16f;

            GUILayout.Space(6);
            GUILayout.Label(title, plotTitleStyle);

            Rect blockRect = GUILayoutUtility.GetRect(PlotWidth + yAxisGutter, PlotHeight + xAxisRow);
            Rect plotRect = new Rect(blockRect.x + yAxisGutter, blockRect.y, PlotWidth, PlotHeight);

            if (tex != null)
            {
                GUI.DrawTexture(plotRect, tex);
            }

            double midFlux = (range.MinFlux + range.MaxFlux) / 2.0;
            GUI.Label(new Rect(blockRect.x, plotRect.y - 7, yAxisGutter - 6, 16), $"{range.MaxFlux:F5}", axisLabelRightStyle);
            GUI.Label(new Rect(blockRect.x, plotRect.y + PlotHeight / 2f - 8, yAxisGutter - 6, 16), $"{midFlux:F5}", axisLabelRightStyle);
            GUI.Label(new Rect(blockRect.x, plotRect.y + PlotHeight - 9, yAxisGutter - 6, 16), $"{range.MinFlux:F5}", axisLabelRightStyle);

            GUI.Label(new Rect(plotRect.x - 2, plotRect.y + PlotHeight + 1, 70, 16), $"{minXDays:F1}", axisLabelLeftStyle);
            GUI.Label(new Rect(plotRect.x + PlotWidth - 68, plotRect.y + PlotHeight + 1, 70, 16), $"{maxXDays:F1}", axisLabelRightStyle);

            GUILayout.Space(xAxisRow);
            GUILayout.Label($"y: normalized flux (F = 1.0 baseline)   x: {xAxisLabel} [days]   error bars: 1-sigma photometric precision/exposure");
        }

        /// <summary>
        /// RV plot with a title, numeric y-axis ticks (min/mid/max velocity) and
        /// x-axis endpoints, drawn as GUI.Label overlays positioned relative to a
        /// single reserved Rect, the same "GetRect once, position absolutely
        /// afterwards" idiom already used for the sky chart above.
        /// </summary>
        void DrawRvPlot(Texture2D tex, RvPlotRange range, double minXDays, double maxXDays, string xAxisLabel, string title,
            string yAxisLabel = "radial velocity [m/s]")
        {
            const float yAxisGutter = 58f;
            const float xAxisRow = 16f;

            GUILayout.Space(6);
            GUILayout.Label(title, plotTitleStyle);

            Rect blockRect = GUILayoutUtility.GetRect(PlotWidth + yAxisGutter, PlotHeight + xAxisRow);
            Rect plotRect = new Rect(blockRect.x + yAxisGutter, blockRect.y, PlotWidth, PlotHeight);

            if (tex != null)
            {
                GUI.DrawTexture(plotRect, tex);
            }

            double midV = (range.MinVelocityMps + range.MaxVelocityMps) / 2.0;
            GUI.Label(new Rect(blockRect.x, plotRect.y - 7, yAxisGutter - 6, 16), $"{range.MaxVelocityMps:F1}", axisLabelRightStyle);
            GUI.Label(new Rect(blockRect.x, plotRect.y + PlotHeight / 2f - 8, yAxisGutter - 6, 16), $"{midV:F1}", axisLabelRightStyle);
            GUI.Label(new Rect(blockRect.x, plotRect.y + PlotHeight - 9, yAxisGutter - 6, 16), $"{range.MinVelocityMps:F1}", axisLabelRightStyle);

            GUI.Label(new Rect(plotRect.x - 2, plotRect.y + PlotHeight + 1, 70, 16), $"{minXDays:F1}", axisLabelLeftStyle);
            GUI.Label(new Rect(plotRect.x + PlotWidth - 68, plotRect.y + PlotHeight + 1, 70, 16), $"{maxXDays:F1}", axisLabelRightStyle);

            GUILayout.Space(xAxisRow);
            GUILayout.Label($"y: {yAxisLabel}   x: {xAxisLabel} [days]   error bars: 1-sigma per point");
        }

        void DrawRvScanReport(List<RvDetectionStage> stages)
        {
            GUILayout.Space(10);
            GUILayout.Label("Scan Report", sectionHeaderStyle);

            RvDetectionResult first = stages[0].Result;
            if (first.InsufficientData)
            {
                GUILayout.Label($"Not enough data ({first.SampleCount} points, need {RvDetector.MinSampleCount}+).");
                return;
            }

            DrawCareerScanOutcome(rvSession.Target);
            GUILayout.Label(CatalogStatusLine(rvSession.Target));
            GUILayout.Label("Review the phase-folded plots and the stats below.");
            GUILayout.Space(6);

            GUILayout.Label($"Baseline: {first.BaselineDays:F2} days   Points: {first.SampleCount}");
            GUILayout.Label($"RV precision (measured): {first.RvPrecisionMps:F2} m/s/epoch");
            double expectedRvPrecision = rvSession.Instrument.EstimatePrecision(rvSession.Target.ApparentMagnitude);
            GUILayout.Label($"RV precision ({rvSession.Instrument.Name}, expected): {expectedRvPrecision:F2} m/s/epoch");
            GUILayout.Space(6);

            for (int i = 0; i < stages.Count; i++)
            {
                RvDetectionResult r = stages[i].Result;

                if (!r.Detected)
                {
                    // The prewhitening's terminal stage: what the residual search
                    // bottomed out at, so a marginal near-miss is visible to the player.
                    GUILayout.Label($"No further signal above SNR {RvDetector.DefaultSnrThreshold:F0} " +
                                     $"(best residual candidate: P = {r.BestPeriodDays:F2} d, SNR {r.Snr:F1}).");
                    break;
                }

                int orbitsCovered = r.BestPeriodDays > 0 ? (int)(r.BaselineDays / r.BestPeriodDays) : 0;
                GUILayout.Label($"Signal {i + 1}:  P = {r.BestPeriodDays:F3} d   " +
                                 $"K = {r.BestSemiAmplitudeMps:F2} +/- {r.SemiAmplitudeUncertaintyMps:F2} m/s   " +
                                 $"SNR = {r.Snr:F1}   (~{orbitsCovered} orbits in baseline)");

                if (r.BestSemiAmplitudeMps > 0 && rvSession.Target.StellarMassSolar > 0 && r.BestPeriodDays > 0)
                {
                    // Eccentricity 0 here, not the catalog value: a prewhitened sinusoid
                    // carries no eccentricity information of its own, and the catalog's e
                    // belongs to the selected planet, not necessarily to this signal.
                    double impliedMassJupiterSini = ImpliedMinimumMassJupiter(
                        r.BestSemiAmplitudeMps, r.BestPeriodDays, rvSession.Target.StellarMassSolar, 0.0);
                    GUILayout.Label($"    Implied Mp*sin(i): {impliedMassJupiterSini:F3} M_jup");
                }

                if (r.LikelyHarmonicOfPeriodDays.HasValue)
                {
                    GUILayout.Label($"    Caution: near-integer period ratio with the {r.LikelyHarmonicOfPeriodDays.Value:F2} d signal. " +
                                     "Possibly a harmonic of that (eccentric) orbit rather than a distinct planet.", smallCaptionStyle);
                }
                GUILayout.Space(4);
            }
        }

        /// <summary>
        /// Inverts the K = 28.4329*(Mp*sini/Mjup)*(Mtot/Msun)^(-2/3)*(P/yr)^(-1/3)*(1-e^2)^(-1/2)
        /// mass function for Mp*sin(i), approximating Mtot ~ Ms (planet mass is always
        /// a small fraction of stellar mass across the range this method can detect).
        /// </summary>
        private static double ImpliedMinimumMassJupiter(double semiAmplitudeMps, double periodDays, double stellarMassSolar, double eccentricity)
        {
            double periodYears = periodDays / 365.25;
            double eccFactor = Math.Sqrt(Math.Max(0.0, 1.0 - eccentricity * eccentricity));
            return semiAmplitudeMps * Math.Pow(stellarMassSolar, 2.0 / 3.0) * Math.Pow(periodYears, 1.0 / 3.0) * eccFactor / RvSemiAmplitudeConstantMps;
        }

        void DrawTransitScanReport(List<TransitDetectionStage> stages)
        {
            GUILayout.Space(10);
            GUILayout.Label("Scan Report", sectionHeaderStyle);

            DetectionResult first = stages[0].Result;
            if (first.InsufficientData)
            {
                GUILayout.Label($"Not enough data ({first.SampleCount} points, need {TransitDetector.MinSampleCount}+).");
                return;
            }

            DrawCareerScanOutcome(session.Target);
            GUILayout.Label(CatalogStatusLine(session.Target));
            GUILayout.Label("Review the phase-folded plots and the stats below.");
            GUILayout.Space(6);

            GUILayout.Label($"Baseline: {first.BaselineDays:F2} days   Points: {first.SampleCount}");
            GUILayout.Label($"Photometric precision (measured): {first.PhotometricPrecisionPpm:F0} ppm/point");
            double expectedPhotometricPrecision = session.Instrument.EstimatePrecision(session.Target.ApparentMagnitude);
            GUILayout.Label($"Photometric precision ({session.Instrument.Name}, expected): {expectedPhotometricPrecision:F0} ppm/point");
            GUILayout.Space(6);

            for (int i = 0; i < stages.Count; i++)
            {
                DetectionResult r = stages[i].Result;

                if (!r.Detected)
                {
                    // The masking search's terminal stage: what the residual
                    // search bottomed out at, so a marginal near-miss is visible.
                    GUILayout.Label($"No further transit above SNR {TransitDetector.DefaultSnrThreshold:F0} " +
                                     $"(best residual candidate: P = {r.BestPeriodDays:F2} d, SNR {r.Snr:F1}).");
                    break;
                }

                int transitsCovered = r.BestPeriodDays > 0 ? (int)(r.BaselineDays / r.BestPeriodDays) : 0;
                GUILayout.Label($"Signal {i + 1}:  P = {r.BestPeriodDays:F3} d   " +
                                 $"depth = {r.BestDepthPpm:F0} +/- {r.DepthUncertaintyPpm:F0} ppm   " +
                                 $"duration = {r.BestDurationHours:F2} h   SNR = {r.Snr:F1}   (~{transitsCovered} transits in baseline)");

                if (r.BestDepthPpm > 0 && session.Target.RadiusSolar > 0)
                {
                    double radiusRatio = Math.Sqrt(r.BestDepthPpm / 1_000_000.0);
                    double planetRadiusEarth = radiusRatio * session.Target.RadiusSolar * SolarRadiusToEarthRadii;
                    GUILayout.Label($"    Implied Rp/Rs: {radiusRatio:F4}   Implied planet radius: {planetRadiusEarth:F2} R_earth");
                }

                // Same alias caution the RV report carries: in-transit masking
                // with a slightly-off period leaves residual dips that re-detect
                // at P/2, 2P etc. A near-integer ratio with an earlier, stronger
                // signal is a flag for the player, not proof either way.
                double? harmonicOf = FindTransitHarmonicParent(stages, i);
                if (harmonicOf.HasValue)
                {
                    GUILayout.Label($"    Caution: near-integer period ratio with the {harmonicOf.Value:F2} d signal. " +
                                     "Possibly a masking residual of that transit rather than a distinct planet.", smallCaptionStyle);
                }
                GUILayout.Space(4);
            }
        }

        /// <summary>Period of an earlier detected stage this one sits at a near-integer ratio of (within 5%, ratios 1:1 to 3:1), else null, mirrors RvDetector.FindHarmonicParentPeriodDays.</summary>
        static double? FindTransitHarmonicParent(List<TransitDetectionStage> stages, int stageIndex)
        {
            double periodDays = stages[stageIndex].Result.BestPeriodDays;
            if (periodDays <= 0) return null;
            for (int i = 0; i < stageIndex; i++)
            {
                DetectionResult prior = stages[i].Result;
                if (!prior.Detected || prior.BestPeriodDays <= 0) continue;
                double ratio = Math.Max(prior.BestPeriodDays, periodDays) / Math.Min(prior.BestPeriodDays, periodDays);
                double nearestInteger = Math.Round(ratio);
                if (nearestInteger >= 1.0 && nearestInteger <= 3.0 && Math.Abs(ratio - nearestInteger) < 0.05 * nearestInteger)
                    return prior.BestPeriodDays;
            }
            return null;
        }

        void RefreshRawPlotTexture()
        {
            if (session == null) return;
            if (rawPlotTexture != null) Destroy(rawPlotTexture);
            rawPlotTexture = LightCurveTexture.RenderRawLightCurve(session.Samples, PlotWidth, PlotHeight, out rawPlotRange);
        }

        /// <summary>
        /// One folded plot per transit-search stage, each rendered from the
        /// masked series that stage actually searched; folding the full data on
        /// a weaker second period would just show the first planet's deeper
        /// transit. Mirrors RefreshRvPhaseFoldedTextures.
        /// </summary>
        void RefreshTransitPhaseFoldedTextures()
        {
            ClearTransitPhaseFoldedTextures();
            if (session == null || lastTransitStages == null) return;

            foreach (var stage in lastTransitStages)
            {
                if (stage.Result.InsufficientData || stage.SearchedSamples == null) break;
                LightCurvePlotRange range;
                var tex = LightCurveTexture.RenderPhaseFoldedCurve(stage.SearchedSamples, stage.Result.BestPeriodDays, PlotWidth, PlotHeight, out range);
                transitPhaseFoldedTextures.Add(tex);
                transitPhaseFoldedRanges.Add(range);
            }
        }

        void ClearTransitPhaseFoldedTextures()
        {
            foreach (var tex in transitPhaseFoldedTextures)
            {
                if (tex != null) Destroy(tex);
            }
            transitPhaseFoldedTextures.Clear();
            transitPhaseFoldedRanges.Clear();
        }

        /// <summary>
        /// O-C series rendered through the RV scatter-plot pipeline: each
        /// measured mid-transit becomes one point (y = O-C in minutes, error bar
        /// = its timing uncertainty). Same plot, different physical quantity;
        /// the GUI relabels the axis.
        /// </summary>
        void RefreshTtvPlotTexture()
        {
            if (ttvPlotTexture != null) { Destroy(ttvPlotTexture); ttvPlotTexture = null; }
            if (lastTtvResult == null || lastTtvResult.EpochCount < TransitTimingVariations.MinMeasuredEpochs) return;

            var pseudoSamples = new List<RvSample>(lastTtvResult.EpochCount);
            foreach (var m in lastTtvResult.Measurements)
            {
                pseudoSamples.Add(new RvSample(m.ExpectedCenterUt, m.OMinusCSeconds / 60.0, m.UncertaintySeconds / 60.0));
            }
            ttvPlotTexture = RvCurveTexture.RenderRawRvCurve(pseudoSamples, PlotWidth, PlotHeight, out ttvPlotRange);
        }

        void RefreshRvRawPlotTexture()
        {
            if (rvSession == null) return;
            if (rvRawPlotTexture != null) Destroy(rvRawPlotTexture);
            rvRawPlotTexture = RvCurveTexture.RenderRawRvCurve(rvSession.Samples, PlotWidth, PlotHeight, out rvRawPlotRange);
        }

        /// <summary>
        /// One folded plot per prewhitening stage, each rendered from the residual
        /// series that stage actually searched; folding the raw data on a weak
        /// second period would just show the first planet's much larger signal.
        /// </summary>
        void RefreshRvPhaseFoldedTextures()
        {
            ClearRvPhaseFoldedTextures();
            if (rvSession == null || lastRvStages == null) return;

            foreach (var stage in lastRvStages)
            {
                if (stage.Result.InsufficientData || stage.SearchedSamples == null) break;
                RvPlotRange range;
                var tex = RvCurveTexture.RenderPhaseFoldedRvCurve(stage.SearchedSamples, stage.Result.BestPeriodDays, PlotWidth, PlotHeight, out range);
                rvPhaseFoldedTextures.Add(tex);
                rvPhaseFoldedRanges.Add(range);
            }
        }

        void ClearRvPhaseFoldedTextures()
        {
            foreach (var tex in rvPhaseFoldedTextures)
            {
                if (tex != null) Destroy(tex);
            }
            rvPhaseFoldedTextures.Clear();
            rvPhaseFoldedRanges.Clear();
        }

        /// <summary>One-off synchronous refresh, for the rare calls that aren't on the periodic timer (session start/stop); a single hitch on a user-triggered action is imperceptible, unlike a repeating one.</summary>
        void RefreshImagingTexture()
        {
            if (imagingSession == null) return;
            var result = DirectImagingTexture.ComputePixels(
                imagingSession.Target, imagingSession.Assessment, imagingSession.EffectiveExposureSeconds, ImagingTextureSize);
            imagingTexture = DirectImagingTexture.ApplyToTexture(result.Pixels, ImagingTextureSize, imagingTexture);
            imagingFovArcsec = result.FovArcsec;
        }

        /// <summary>
        /// Kicks off the frame raster + forward-simulated predictions on a
        /// background Task: at 400x400 with several transcendental calls per
        /// pixel, plus predictions that can step through days of upcoming nights,
        /// this is the single most expensive thing the mod does per refresh,
        /// expensive enough that running it synchronously on the main thread
        /// stalls the frame that's rendering the game (visible as a periodic
        /// hitch/flash). Neither DirectImagingTexture.ComputePixels nor
        /// ImagingObservationSession's Predict* methods touch any UnityEngine.Object
        /// API, so they're safe to run off the main thread; only the final
        /// Texture2D upload (ApplyImagingRenderResult) has to happen on it.
        /// </summary>
        void StartImagingRefresh()
        {
            if (imagingSession == null || imagingRenderTask != null) return;
            var session = imagingSession;
            var star = session.Target;
            var assessment = session.Assessment;
            double effectiveSeconds = session.EffectiveExposureSeconds;
            bool identified = !IsIdentityHidden(star);
            bool currentlyObservable = session.CurrentConditions.Observable;
            int generation = imagingRenderGeneration;

            imagingRenderTask = Task.Run(() =>
            {
                var pixelResult = DirectImagingTexture.ComputePixels(star, assessment, effectiveSeconds, ImagingTextureSize);

                double detectionUt = double.NaN;
                if (identified)
                {
                    double needed = DirectImagingSimulator.RequiredExposureSeconds(assessment);
                    detectionUt = double.IsInfinity(needed)
                        ? double.PositiveInfinity
                        : session.PredictUtForEffectiveExposure(needed, ImagingPredictionMaxWallSeconds);
                }

                double nextWindowUt = currentlyObservable ? double.NaN : session.PredictNextObservableUt();

                return new ImagingRenderResult
                {
                    Pixels = pixelResult.Pixels,
                    FovArcsec = pixelResult.FovArcsec,
                    DetectionUt = detectionUt,
                    NextWindowUt = nextWindowUt,
                    Generation = generation
                };
            });
        }

        /// <summary>Applies a completed background render (see StartImagingRefresh), the only part of the pipeline that's allowed to touch the Texture2D.</summary>
        void PollImagingRenderTask()
        {
            if (imagingRenderTask == null || !imagingRenderTask.IsCompleted) return;
            if (imagingRenderTask.IsFaulted)
            {
                Debug.LogError("[ExoInstruments] Imaging render task failed: " + imagingRenderTask.Exception);
                imagingRenderTask = null;
                return;
            }
            var result = imagingRenderTask.Result;
            imagingRenderTask = null;
            if (result.Generation != imagingRenderGeneration) return; // stale: a new session started while this one was in flight
            imagingTexture = DirectImagingTexture.ApplyToTexture(result.Pixels, ImagingTextureSize, imagingTexture);
            imagingFovArcsec = result.FovArcsec;
            imagingDetectionUt = result.DetectionUt;
            imagingNextWindowUt = result.NextWindowUt;
        }

        /// <summary>
        /// Kicks off the full catalog re-transform (RA/Dec -> Alt/Az for every
        /// loaded target, thousands once background stars are merged in) plus the
        /// 640x640 raster on a background Task, for the same reason as
        /// StartImagingRefresh; SkyCoordinates' math and SkyChartTexture.ComputePixels
        /// touch no UnityEngine.Object API, so both are safe off the main thread.
        /// </summary>
        void StartSkyChartRefresh()
        {
            if (catalog == null || skyChartRenderTask != null) return;

            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null)
            {
                Debug.LogWarning("[ExoInstruments] Home body not available yet; skipping sky chart refresh.");
                return;
            }

            double ut = Planetarium.GetUniversalTime();
            double localMeridianRaDeg = SkyCoordinates.ComputeLocalMeridianRaDeg(
                ut, home.rotationPeriod, home.initialRotation, ObservatorySite.LongitudeDeg);
            var catalogSnapshot = catalog;
            string filterSnapshot = searchFilter;
            var pointingSnapshot = selectedPhotographyTarget;
            var view = new SkyChartView { Zoom = skyChartZoom, Pan = skyChartPan };

            // Solar-system bodies must be sampled on the MAIN thread (they read
            // KSP CelestialBody positions/transforms). We compute their
            // alt/az/marker here into plain data, then hand it to the background
            // task purely as SkyChartPoints (no KSP types cross the boundary).
            var bodyPoints = BuildChartBodyPoints(out var bodyHitList);
            cachedChartBodies = bodyHitList;

            skyChartRenderTask = Task.Run(() =>
            {
                var points = new List<SkyChartPoint>();
                foreach (var star in catalogSnapshot)
                {
                    var horizontal = SkyCoordinates.TryComputeHorizontal(star, localMeridianRaDeg, ObservatorySite.LatitudeDeg);
                    if (!horizontal.HasValue) continue;
                    if (!horizontal.Value.IsAboveHorizon(0.0)) continue;

                    points.Add(new SkyChartPoint
                    {
                        Target = star,
                        AltitudeDeg = horizontal.Value.AltitudeDeg,
                        AzimuthDeg = horizontal.Value.AzimuthDeg,
                        IsHighlighted = MatchesFilter(star, filterSnapshot),
                        IsSelectedTarget = IsPhotographyTarget(star, pointingSnapshot)
                    });
                }
                points.AddRange(bodyPoints);
                points.AddRange(BuildDeepSkyPoints(localMeridianRaDeg, pointingSnapshot));
                var pixels = SkyChartTexture.ComputePixels(points, SkyChartWidth, SkyChartHeight, view, !string.IsNullOrEmpty(filterSnapshot));
                return (points, pixels);
            });
        }

        /// <summary>What the object is and how much sky it covers, the two things that decide which instrument can frame it.</summary>
        static string DescribeDeepSky(DeepSkyObject obj)
        {
            string kind;
            switch (obj.Kind)
            {
                case DeepSkyKind.HiiRegion: kind = "H II region"; break;
                case DeepSkyKind.SupernovaRemnant: kind = "supernova remnant"; break;
                case DeepSkyKind.PlanetaryNebula: kind = "planetary nebula"; break;
                case DeepSkyKind.Galaxy: kind = "galaxy"; break;
                case DeepSkyKind.DarkNebula: kind = "dark nebula, seen in silhouette"; break;
                default: kind = "reflection nebula"; break;
            }
            string size = obj.MajorArcmin >= 60.0
                ? $"{obj.MajorArcmin / 60.0:F1} x {obj.MinorArcmin / 60.0:F1} deg"
                : obj.MajorArcmin >= 1.0
                    ? $"{obj.MajorArcmin:F0} x {obj.MinorArcmin:F0} arcmin"
                    : $"{obj.MajorArcmin * 60.0:F0} x {obj.MinorArcmin * 60.0:F0} arcsec";
            string note = "";
            var map = SolarSystemCameraTexture.EmissionMap;
            if (obj.Kind == DeepSkyKind.DarkNebula)
            {
                // An emission map holds what is emitted, so a silhouette is not in it at all. Say
                // so on the chart rather than let the player spend an exposure finding out.
                note = ", absorption, not emission: no installed map can show it";
            }
            else if (obj.EmitsLines && map != null && map.IsLoaded)
            {
                // The number that decides whether the installed data can render this object as a
                // shape. The composite's beam is 6', coarser than its 3.4' sampling.
                double beams = obj.BeamsAcross(EmissionMapBeamArcmin);
                note = $", {beams:F0} beams across the installed map"
                     + (beams < 1.5 ? ", so it cannot resolve it at all"
                        : beams < 4.0 ? ", so expect a smudge"
                        : beams < 12.0 ? ", so expect an outline and no detail" : "");
            }
            return kind + ", " + size + note;
        }

        /// <summary>Beam of the Finkbeiner (2003) H-alpha composite, arcminutes FWHM. Coarser than its 3.4' HEALPix sampling, which is what actually limits what a frame can show.</summary>
        private const double EmissionMapBeamArcmin = 6.0;

        /// <summary>
        /// The bright nebulae, placed on the chart the same way a star is. Pure computation on
        /// plain data, so it runs on the chart's background task.
        ///
        /// Not gated by the search filter: these are never "highlighted", which is what keeps them
        /// out of the star hit test. They have their own, HitTestDeepSky.
        /// </summary>
        static List<SkyChartPoint> BuildDeepSkyPoints(double localMeridianRaDeg, SkyTarget pointing)
        {
            var points = new List<SkyChartPoint>();
            foreach (var obj in DeepSkyCatalog.All)
            {
                var horizontal = SkyCoordinates.EquatorialToHorizontal(
                    obj.RaDeg, obj.DecDeg, localMeridianRaDeg, ObservatorySite.LatitudeDeg);
                if (!horizontal.IsAboveHorizon(0.0)) continue;

                points.Add(new SkyChartPoint
                {
                    IsDeepSky = true,
                    DeepSky = obj,
                    AltitudeDeg = horizontal.AltitudeDeg,
                    AzimuthDeg = horizontal.AzimuthDeg,
                    IsSelectedTarget = pointing.IsEquatorial
                                    && Math.Abs(pointing.RaDeg - obj.RaDeg) < 1e-6
                                    && Math.Abs(pointing.DecDeg - obj.DecDeg) < 1e-6,
                });
            }

            // The galaxies come from the packed catalogue rather than a hand-written list, so the
            // chart is cut by brightness: at B = 15 there are of order a hundred thousand of them
            // and the chart would be a wall of markers. 11 is roughly the reach of the RedCat on an
            // extended source in a single sub, and leaves a few hundred on the whole sky.
            GalaxyCatalog catalog = SolarSystemCameraTexture.GalaxyCatalog;
            if (catalog != null && catalog.IsLoaded)
            {
                foreach (Galaxy g in catalog.Search(0.0, 0.0, 180.0, ChartGalaxyMagnitudeLimit))
                {
                    var h = SkyCoordinates.EquatorialToHorizontal(
                        g.RaDeg, g.DecDeg, localMeridianRaDeg, ObservatorySite.LatitudeDeg);
                    if (!h.IsAboveHorizon(0.0)) continue;

                    points.Add(new SkyChartPoint
                    {
                        IsDeepSky = true,
                        DeepSky = new DeepSkyObject
                        {
                            Id = g.Name,
                            CommonName = null,
                            RaDeg = g.RaDeg,
                            DecDeg = g.DecDeg,
                            MajorArcmin = g.D25Arcmin,
                            MinorArcmin = g.D25Arcmin * g.AxisRatio,
                            Kind = DeepSkyKind.Galaxy,
                        },
                        AltitudeDeg = h.AltitudeDeg,
                        AzimuthDeg = h.AzimuthDeg,
                        IsSelectedTarget = pointing.IsEquatorial
                                        && Math.Abs(pointing.RaDeg - g.RaDeg) < 1e-6
                                        && Math.Abs(pointing.DecDeg - g.DecDeg) < 1e-6,
                    });
                }
            }
            return points;
        }

        /// <summary>Faintest total B magnitude a galaxy is drawn on the sky chart at. The camera itself has no such cut; it draws whatever clears the frame's own noise floor.</summary>
        private const double ChartGalaxyMagnitudeLimit = 11.0;

        /// <summary>Applies a completed background chart render (see StartSkyChartRefresh), the only part of the pipeline that's allowed to touch the Texture2D.</summary>
        void PollSkyChartRenderTask()
        {
            if (skyChartRenderTask == null || !skyChartRenderTask.IsCompleted) return;
            if (skyChartRenderTask.IsFaulted)
            {
                Debug.LogError("[ExoInstruments] Sky chart render task failed: " + skyChartRenderTask.Exception);
                skyChartRenderTask = null;
                return;
            }
            var (points, pixels) = skyChartRenderTask.Result;
            cachedSkyChartPoints = points;
            skyChartTexture = SkyChartTexture.ApplyToTexture(pixels, SkyChartWidth, SkyChartHeight, skyChartTexture);
            skyChartRenderTask = null;
        }

        // --- Observing-quality forecast heatmap --------------------------------

        /// <summary>
        /// Kicks off a forecast recompute on a background Task when the selected
        /// (target, instrument) pairing changed or the clock moved a quarter-night
        /// past the last compute, same background-Task treatment as the sky
        /// chart, for the same reason (thousands of conditions evaluations).
        /// ObservingForecast and ForecastTexture.ComputePixels touch no
        /// UnityEngine.Object API, so both are safe off the main thread.
        /// </summary>
        void MaybeStartForecastRefresh()
        {
            if (forecastRenderTask != null) return;
            if (selectedStar == null || !selectedStar.RaDeg.HasValue || !selectedStar.DecDeg.HasValue) return;
            InstrumentSpec instrument = SelectedInstrument;
            if (instrument.IsSpaceBased) return;

            double ut = Planetarium.GetUniversalTime();
            bool stale = forecastRenderedStar != selectedStar
                || forecastRenderedInstrumentIndex != selectedObservatoryIndex
                || double.IsNaN(forecastComputedUt)
                || Math.Abs(ut - forecastComputedUt) > ForecastRefreshUtSeconds;
            if (!stale) return;

            var star = selectedStar;
            int instrumentIndex = selectedObservatoryIndex;
            // Request markers: what the in-flight task is computing for. The
            // applied markers (forecastAppliedStar/-InstrumentIndex, set when
            // the result lands) are what DrawForecastPanel trusts; these ones
            // only exist so this method doesn't re-request the same compute
            // every frame while one is running.
            forecastRenderedStar = star;
            forecastRenderedInstrumentIndex = instrumentIndex;
            forecastComputedUt = ut;
            ImagingObserverContext observer = BuildImagingObserverContext();

            forecastRenderTask = Task.Run(() =>
            {
                var forecast = ObservingForecast.Compute(star, instrument, observer, ut, ForecastNights, ForecastColumns);
                var pixels = ForecastTexture.ComputePixels(forecast, ForecastWidth, ForecastHeight);
                return (forecast, pixels, star, instrumentIndex);
            });
        }

        /// <summary>Applies a completed background forecast render, the only part allowed to touch the Texture2D.</summary>
        void PollForecastRenderTask()
        {
            if (forecastRenderTask == null || !forecastRenderTask.IsCompleted) return;
            if (forecastRenderTask.IsFaulted)
            {
                Debug.LogError("[ExoInstruments] Forecast render task failed: " + forecastRenderTask.Exception);
                forecastRenderTask = null;
                // The request markers were written optimistically at task start;
                // clear them so the next Update retries instead of treating the
                // failed compute as fresh until the clock moves.
                forecastRenderedStar = null;
                forecastComputedUt = double.NaN;
                return;
            }
            var (forecast, pixels, star, instrumentIndex) = forecastRenderTask.Result;
            forecastRenderTask = null;
            forecastResult = forecast;
            forecastAppliedStar = star;
            forecastAppliedInstrumentIndex = instrumentIndex;
            forecastTexture = ForecastTexture.ApplyToTexture(pixels, ForecastWidth, ForecastHeight, forecastTexture);
        }

        /// <summary>
        /// The forecast heatmap under the observatory selector: one row per
        /// upcoming night, one column per time of night, every constraint the
        /// session models folded into a single color. Click a cell to warp
        /// there. Fog-safe by construction: the grid consumes only the target's
        /// position and magnitude, both things the sky already gives away.
        /// </summary>
        void DrawForecastPanel()
        {
            GUILayout.Space(10);
            if (SelectedInstrument.IsSpaceBased)
            {
                GUILayout.Label("Observing forecast: unnecessary. Space-based coverage is continuous, airmass-free and moon-proof. That is what the capital bought.", smallCaptionStyle);
                return;
            }
            if (!selectedStar.RaDeg.HasValue || !selectedStar.DecDeg.HasValue)
            {
                GUILayout.Label("Observing forecast unavailable: this target has no sky coordinates on record.", smallCaptionStyle);
                return;
            }
            // A grid computed for a previously selected star/instrument stays on
            // screen (and clickable!) until the new background compute lands;
            // warping the player to another target's "best window" would be a
            // genuine bug, so a mismatched grid is treated as still computing.
            if (forecastTexture == null || forecastResult == null
                || forecastAppliedStar != selectedStar
                || forecastAppliedInstrumentIndex != selectedObservatoryIndex)
            {
                GUILayout.Label("Computing observing forecast...", smallCaptionStyle);
                return;
            }

            GUILayout.Label($"Observing forecast, next {ForecastNights} nights (top row = tonight, left edge = now+0h):");
            Rect rect = GUILayoutUtility.GetRect(ForecastWidth, ForecastHeight,
                GUILayout.Width(ForecastWidth), GUILayout.Height(ForecastHeight));
            GUI.DrawTexture(rect, forecastTexture);

            double nowUt = Planetarium.GetUniversalTime();
            string hoverLine = "Hover a cell for details. Click to warp to that moment.";
            Event e = Event.current;
            if (rect.Contains(e.mousePosition)
                && ForecastTexture.TryHitCell(forecastResult, ForecastWidth, ForecastHeight,
                        e.mousePosition.x - rect.x, e.mousePosition.y - rect.y, out int row, out int col))
            {
                double cellUt = forecastResult.CellUt(row, col);
                double quality = forecastResult.Quality01[row * forecastResult.Columns + col];
                double hoursAway = (cellUt - nowUt) / 3600.0;
                hoverLine = quality <= 0.0
                    ? $"+{hoursAway:F1} h: unobservable (day, below the telescope limit, or a moon in the way)"
                    : $"+{hoursAway:F1} h: {quality * 100.0:F0}% of this target's best upcoming window. Click to warp.";
                if (e.type == EventType.MouseDown && e.button == 0)
                {
                    if (cellUt > nowUt)
                    {
                        BetterTimeWarpIntegration.WarpTo(cellUt);
                        forecastWarpTargetUt = cellUt;
                    }
                    e.Use();
                }
            }

            if (!double.IsNaN(forecastWarpTargetUt))
            {
                if (nowUt >= forecastWarpTargetUt) forecastWarpTargetUt = double.NaN;
                else hoverLine = $"Warping... +{(forecastWarpTargetUt - nowUt) / 3600.0:F1} h to go.";
            }
            GUILayout.Label(hoverLine, smallCaptionStyle);

            if (!double.IsNaN(forecastResult.BestUt) && forecastResult.PeakQualityRaw > 0.0
                && forecastResult.BestUt > nowUt + 60.0)
            {
                if (GUILayout.Button($"Warp to best window (+{(forecastResult.BestUt - nowUt) / 3600.0:F1} h)", GUILayout.Height(24)))
                {
                    BetterTimeWarpIntegration.WarpTo(forecastResult.BestUt);
                }
            }

            GUILayout.Label(DescribeForecastInputs(SelectedInstrument), smallCaptionStyle);
        }

        /// <summary>
        /// Same heatmap widget as DrawForecastPanel (ForecastTexture render +
        /// click-to-warp + "warp to best window"), fed by a body altitude
        /// timeline instead of a star's fixed RA/Dec. "Quality" here is simply
        /// 1.0 when the body is up AND it's night, 0.0 otherwise; there's no
        /// airmass/precision model for a photograph the way there is for
        /// photometry, so the map is a clean observable/not-observable
        /// calendar rather than a graded one.
        /// </summary>
        void DrawPhotographyForecastPanel()
        {
            GUILayout.Space(10);
            RefreshPhotographyForecastIfStale();

            if (photoForecastTexture == null || photoForecastResult == null || photoForecastAppliedTarget != selectedPhotographyTarget)
            {
                GUILayout.Label("Computing observing forecast...", smallCaptionStyle);
                return;
            }

            GUILayout.Label($"Observing forecast, next {ForecastNights} nights (top row = tonight, left edge = now+0h):");
            Rect rect = GUILayoutUtility.GetRect(ForecastWidth, ForecastHeight,
                GUILayout.Width(ForecastWidth), GUILayout.Height(ForecastHeight));
            GUI.DrawTexture(rect, photoForecastTexture);

            double nowUt = Planetarium.GetUniversalTime();
            string hoverLine = "Hover a cell for details. Click to warp to that moment.";
            Event e = Event.current;
            if (rect.Contains(e.mousePosition)
                && ForecastTexture.TryHitCell(photoForecastResult, ForecastWidth, ForecastHeight,
                        e.mousePosition.x - rect.x, e.mousePosition.y - rect.y, out int row, out int col))
            {
                double cellUt = photoForecastResult.CellUt(row, col);
                double quality = photoForecastResult.Quality01[row * photoForecastResult.Columns + col];
                double hoursAway = (cellUt - nowUt) / 3600.0;
                hoverLine = quality <= 0.0
                    ? $"+{hoursAway:F1} h: unobservable (day, or {selectedPhotographyTarget.DisplayName} below the horizon)"
                    : $"+{hoursAway:F1} h: {quality * 100.0:F0}% zenith-equivalent seeing efficiency. Click to warp.";
                if (e.type == EventType.MouseDown && e.button == 0)
                {
                    if (cellUt > nowUt)
                    {
                        BetterTimeWarpIntegration.WarpTo(cellUt);
                        photoForecastWarpTargetUt = cellUt;
                    }
                    e.Use();
                }
            }

            if (!double.IsNaN(photoForecastWarpTargetUt))
            {
                if (nowUt >= photoForecastWarpTargetUt) photoForecastWarpTargetUt = double.NaN;
                else hoverLine = $"Warping... +{(photoForecastWarpTargetUt - nowUt) / 3600.0:F1} h to go.";
            }
            GUILayout.Label(hoverLine, smallCaptionStyle);

            if (!double.IsNaN(photoForecastResult.BestUt) && photoForecastResult.PeakQualityRaw > 0.0
                && photoForecastResult.BestUt > nowUt + 60.0)
            {
                if (GUILayout.Button($"Warp to best window (+{(photoForecastResult.BestUt - nowUt) / 3600.0:F1} h)", GUILayout.Height(24)))
                {
                    BetterTimeWarpIntegration.WarpTo(photoForecastResult.BestUt);
                }
            }

            GUILayout.Label("Compiles: twilight, target altitude and airmass seeing efficiency (1/X^2), the same real variable behind the camera's own atmospheric blur. No weather term: stock KSP has none to read.", smallCaptionStyle);
        }

        /// <summary>Recomputes the body forecast (synchronous, cheap enough) when the target changed or the clock moved a quarter-night since the last compute.</summary>
        void RefreshPhotographyForecastIfStale()
        {
            if (!selectedPhotographyTarget.HasTarget) return;
            double ut = Planetarium.GetUniversalTime();
            bool stale = photoForecastAppliedTarget != selectedPhotographyTarget
                || double.IsNaN(photoForecastComputedUt)
                || Math.Abs(ut - photoForecastComputedUt) > ForecastRefreshUtSeconds;
            if (!stale) return;

            photoForecastComputedUt = ut;
            photoForecastAppliedTarget = selectedPhotographyTarget;
            photoForecastResult = ComputeBodyForecast(selectedPhotographyTarget, ut, ForecastNights, ForecastColumns);
            var pixels = ForecastTexture.ComputePixels(photoForecastResult, ForecastWidth, ForecastHeight);
            photoForecastTexture = ForecastTexture.ApplyToTexture(pixels, ForecastWidth, ForecastHeight, photoForecastTexture);
        }

/// <summary>
        /// Forecast d'observabilité d'un corps du système solaire pour le RC20.
        ///
        /// Chaque cellule évalue la vraie position orbitale future du corps,
        /// l'altitude du site KSC à cette UT, et le même seuil de nuit que le
        /// reste de l'observatoire.
        ///
        /// Important : contrairement au forecast stellaire, Quality01 n'est PAS
        /// renormalisé au meilleur créneau de la fenêtre de 12 nuits. Pour une
        /// planète, ce maximum change lorsque la fenêtre glisse, ce qui ferait
        /// recolorer toute la carte à chaque refresh au lieu de simplement faire
        /// défiler les bandes vers la gauche.
        ///
        /// Ici Quality01 est donc une qualité absolue :
        ///   0       = fermé / sous l'horizon / jour
        ///   1 / X²  = efficacité de seeing à l'airmass X, multipliée par la
        ///             transmission nuageuse EVE (couverture actuelle sur KSC,
        ///             appliquée à toutes les cellules, pas de simulation du
        ///             déplacement futur des nuages)
        ///   1       = zénith, ciel dégagé
        /// </summary>
        ObservingForecast.ForecastResult ComputeBodyForecast(
            SkyTarget target,
            double startUt,
            int nights,
            int columnsPerNight)
        {
            CelestialBody body = target.Body;
            var result = new ObservingForecast.ForecastResult
            {
                StartUt = startUt,
                CellSeconds = 0.0,
                Columns = columnsPerNight,
                Rows = nights,
                Quality01 = new double[nights * columnsPerNight],
                BestUt = double.NaN,
                PeakQualityRaw = 0.0,
            };

            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || !target.HasTarget)
            {
                result.CellSeconds = 21600.0 / columnsPerNight;
                return result;
            }

            double rotationSeconds = home.rotationPeriod > 0.0
                ? home.rotationPeriod
                : 21600.0;

            double cellSeconds = rotationSeconds / columnsPerNight;
            result.CellSeconds = cellSeconds;

            // Même calcul de nuit que DrawPhotographyObservability() et le
            // reste des instruments terrestres : Soleil sous -12 degrés.
            ImagingObserverContext observer = BuildImagingObserverContext();

            /*
             * Le site KSC est connu dans le repère monde à l'UT courante.
             * Pour une cellule future, on fait tourner sa normale de surface
             * autour de l'axe de rotation de Kerbin.
             *
             * Le signe négatif est conservé car c'est celui qui a rendu la
             * cellule "now" cohérente avec la vraie position de la Mun lors du
             * test précédent.
             */
            double nowUt = Planetarium.GetUniversalTime();

            // EVE clouds aren't a time simulation; this samples the current sky over KSC
            // and applies it to every future cell, same "clouds persist" approximation the
            // live RC20 camera already makes for wind drift.
            float cloudCoverage = SolarSystemCameraTexture.ComputeCloudCoverage();
            double cloudTransmission = 1.0 - cloudCoverage * SolarSystemCameraTexture.CloudMaxAttenuation;

            Vector3d observerNow = home.GetWorldSurfacePosition(
                ObservatorySite.LatitudeDeg,
                ObservatorySite.LongitudeDeg,
                100.0);

            Vector3d homeNow = home.position;
            Vector3d observerUpNow = (observerNow - homeNow).normalized;
            Vector3d spinAxis = ((Vector3d)home.transform.up).normalized;

            for (int i = 0; i < result.Quality01.Length; i++)
            {
                double cellUt = startUt + (i + 0.5) * cellSeconds;

                // Rotation du sol sous le ciel entre maintenant et cette UT.
                double rotationDegrees = home.rotationPeriod > 0.0
                    ? -(cellUt - nowUt) / home.rotationPeriod * 360.0
                    : 0.0;

                Vector3 rotatedUp = Quaternion.AngleAxis(
                    (float)rotationDegrees,
                    (Vector3)spinAxis) * (Vector3)observerUpNow;

                Vector3d observerUpAtUt = ((Vector3d)rotatedUp).normalized;

                /*
                 * Position future prédite par les Orbites KSP.
                 *
                * GetBodyPositionAtUt reconstruit une position monde absolue à la
                * future UT demandée. C'est indispensable pour les lunes : Mun et
                * Minmus sont exprimées relativement à Kerbin, dont la position doit
                * elle aussi être avancée à cette même UT.
                */
                Vector3d homePositionAtUt = GetBodyPositionAtUt(home, cellUt);
                Vector3d observerPositionAtUt =
                    homePositionAtUt + observerUpAtUt * (home.Radius + 100.0);

                double altitudeDeg = double.NegativeInfinity;
                if (target.IsBody)
                {
                    Vector3d bodyPositionAtUt = GetBodyPositionAtUt(body, cellUt);
                    Vector3d observerToBody = bodyPositionAtUt - observerPositionAtUt;
                    if (observerToBody.sqrMagnitude > 0.0)
                    {
                        altitudeDeg = 90.0 - Vector3d.Angle(
                            observerUpAtUt,
                            observerToBody.normalized);
                    }
                }
                else
                {
                    // A fixed position needs no orbit propagation: only the sky's own rotation
                    // moves it, which is what the local meridian already tracks.
                    double meridianRaDeg = SkyCoordinates.ComputeLocalMeridianRaDeg(
                        cellUt, home.rotationPeriod, home.initialRotation, ObservatorySite.LongitudeDeg);
                    altitudeDeg = SkyCoordinates.EquatorialToHorizontal(
                        target.RaDeg, target.DecDeg, meridianRaDeg, ObservatorySite.LatitudeDeg).AltitudeDeg;
                }

                // Même définition de nuit que l'interface du RC20.
                ImagingConditionsSnapshot sky = ImagingObservingConditions.Evaluate(
                    cellUt,
                    null,
                    null,
                    observer);

                bool isNight = sky.IsNight;

                // Même règle que DrawSolarSystemCameraView :
                // le bouton Capture devient utilisable au-dessus de l'horizon.
                bool bodyUp = altitudeDeg > 0.0;

                double quality = 0.0;

                if (isNight && bodyUp)
                {
                    double airmass = ImagingObservingConditions.AirmassAt(altitudeDeg);

                    if (!double.IsInfinity(airmass)
                        && !double.IsNaN(airmass)
                        && airmass > 0.0)
                    {
                        // Valeur absolue, déjà dans [0, 1].
                        // Pas de normalisation ultérieure : c'est ce qui rend le
                        // déplacement du forecast stable pendant le time warp.
                        quality = 1.0 / (airmass * airmass) * cloudTransmission;
                    }
                }

                result.Quality01[i] = quality;

                if (quality > result.PeakQualityRaw)
                {
                    result.PeakQualityRaw = quality;
                    result.BestUt = cellUt;
                }
            }

            /*
             * Ne PAS normaliser Quality01 par PeakQualityRaw ici.
             *
             * Le forecast stellaire le fait car c'est un outil de planification
             * relatif. Pour une planète mobile, le meilleur créneau sort et
             * entre constamment dans la fenêtre de 12 nuits : la normalisation
             * recolore alors toute la heatmap à chaque refresh.
             *
             * En gardant 1 / X² brut, une UT donnée conserve sa couleur entre
             * deux refreshes. Les bandes défilent donc au lieu de muter.
             */

            return result;
        }
/// <summary>
        /// Position monde absolue d'un corps céleste à une UT future.
        ///
        /// Il ne faut pas utiliser directement getPositionAtUT() pour le
        /// forecast d'une lune : Mun et Minmus orbitent Kerbin, et Kerbin se
        /// déplace lui-même autour de Kerbol. Une position correcte doit être :
        ///
        ///     position future du parent
        ///   + position orbitale future relative au parent.
        ///
        /// La récursion couvre aussi les systèmes moddés avec plusieurs niveaux
        /// de satellites.
        /// </summary>
        private static Vector3d GetBodyPositionAtUt(CelestialBody body, double ut)
        {
            if (body == null)
            {
                return Vector3d.zero;
            }

            Orbit orbit = body.orbit;

            // Corps racine : typiquement Kerbol. Il n'a pas d'orbite autour
            // d'un autre corps, donc sa position monde actuelle est la racine
            // du système de coordonnées.
            if (orbit == null
                || orbit.referenceBody == null
                || orbit.referenceBody == body)
            {
                return body.position;
            }

            /*
             * Position ABSOLUE future du parent.
             *
             * Exemples :
             *
             * Kerbin :
             *   position future de Kerbol
             * + position orbitale future de Kerbin relative à Kerbol.
             *
             * Mun :
             *   position future de Kerbin
             * + position orbitale future de Mun relative à Kerbin.
             *
             * Minmus :
             *   position future de Kerbin
             * + position orbitale future de Minmus relative à Kerbin.
             */
            Vector3d parentPositionAtUt =
                GetBodyPositionAtUt(orbit.referenceBody, ut);

            /*
             * getRelativePositionAtUT est explicitement une position relative
             * au corps parent.
             *
             * Les vecteurs fournis par Orbit utilisent le repère orbital KSP,
             * où Y et Z sont inversés vis-à-vis de Unity / body.position /
             * transform.up / GetWorldSurfacePosition. On les convertit donc
             * avant de les additionner à une position monde.
             */
            Vector3d relativeOrbitPositionKsp =
                orbit.getRelativePositionAtUT(ut);

            Vector3d relativeOrbitPositionWorld = new Vector3d(
                relativeOrbitPositionKsp.x,
                relativeOrbitPositionKsp.z,
                relativeOrbitPositionKsp.y);

            return parentPositionAtUt + relativeOrbitPositionWorld;
        }

        /// <summary>
        /// Les vecteurs fournis par Orbit dans KSP ont Y et Z inversés par
        /// rapport aux coordonnées monde Unity/KSP utilisées par CelestialBody
        /// .position, transform.up, GetWorldSurfacePosition, etc.
        /// </summary>
        private static Vector3d ConvertOrbitVectorToWorld(Vector3d orbitVector)
        {
            return new Vector3d(
                orbitVector.x,
                orbitVector.z,
                orbitVector.y);
        }
        /// <summary>What the forecast folds together for this method, honest about what each pipeline actually pays for, and about the absence of weather.</summary>
        static string DescribeForecastInputs(InstrumentSpec instrument)
        {
            string methodInputs;
            switch (instrument.Method)
            {
                case DetectionMethod.Transit:
                    methodInputs = "twilight, target altitude, airmass scintillation, Mün/Minmus moonlit-sky pollution and occultation";
                    break;
                case DetectionMethod.DirectImaging:
                    methodInputs = "twilight, target altitude, AO airmass efficiency (1/X^2) and lunar occultation. H-band imaging shrugs off moonlight itself";
                    break;
                default:
                    methodInputs = "twilight, target altitude and lunar occultation. Spectrograph line positions don't care about airmass or moonlight";
                    break;
            }
            return $"Compiles: {methodInputs}. No weather term: stock KSP has no weather to read.";
        }

        /// <summary>
        /// Cheap path: reuses the already-computed Alt/Az from the last full
        /// refresh, just flips IsHighlighted and re-renders. Called on every
        /// search text change so typing feels responsive despite the throttle above.
        /// </summary>
        void RefreshSkyChartHighlights()
        {
            for (int i = 0; i < cachedSkyChartPoints.Count; i++)
            {
                var p = cachedSkyChartPoints[i];
                if (p.IsBody) continue; // bodies have no Target and aren't filtered by the star search
                p.IsHighlighted = MatchesFilter(p.Target, searchFilter);
                cachedSkyChartPoints[i] = p;
            }
            RenderSkyChartTexture();
        }

        /// <summary>Synchronous: re-rasters the already-computed points at the current zoom/pan, with no catalog work, used for drag/zoom/recenter, where the user expects an immediate response.</summary>
        void RenderSkyChartTexture()
        {
            var view = new SkyChartView { Zoom = skyChartZoom, Pan = skyChartPan };
            var pixels = SkyChartTexture.ComputePixels(cachedSkyChartPoints, SkyChartWidth, SkyChartHeight, view, !string.IsNullOrEmpty(searchFilter));
            skyChartTexture = SkyChartTexture.ApplyToTexture(pixels, SkyChartWidth, SkyChartHeight, skyChartTexture);
        }

        private static bool MatchesFilter(StarTarget star, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            // Matches what the player is allowed to see: searching "51 Peg" in
            // career must not light up an unscanned 51 Peg (that would be the
            // whole fog answered by the search box). Unscanned stars are instead
            // findable by their provisional designation ("J2257+2046").
            return GetDisplayName(star).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void ClearTextures()
        {
            if (rawPlotTexture != null) { Destroy(rawPlotTexture); rawPlotTexture = null; }
            if (ttvPlotTexture != null) { Destroy(ttvPlotTexture); ttvPlotTexture = null; }
            if (rvRawPlotTexture != null) { Destroy(rvRawPlotTexture); rvRawPlotTexture = null; }
            if (imagingTexture != null) { Destroy(imagingTexture); imagingTexture = null; }
            ClearTransitPhaseFoldedTextures();
            ClearRvPhaseFoldedTextures();
        }

        void ResetSession()
        {
            session = null;
            rvSession = null;
            imagingSession = null;
            imagingRenderGeneration++; // discard any in-flight background render from the ending session
            lastImagingRefreshUt = double.NaN;
            imagingDetectionUt = double.NaN;
            imagingNextWindowUt = double.NaN;
            lastTransitStages = null;
            lastTtvResult = null;
            lastTtvScienceAwarded = 0f;
            lastRvStages = null;
            lastRmResult = null;
            lastRmPlanet = null;
            lastRmScienceAwarded = 0f;
            lastImagingResult = null;
            lastScanScienceAwarded = 0f;
            lastScanWasFirstForStar = false;
            lastScanJackpotPlanetCount = 0;
            lastScanCharacterized = false;
            ClearTextures();
        }
    }
}
using System;
using System.Collections.Generic;
using System.Globalization;

namespace ExoInstruments
{
    /// <summary>
    /// Per-save state for the career progression loop: fog-of-war (which stars
    /// have been scanned/rewarded) and the instrument unlock economy (which
    /// telescopes have been purchased, and cumulative Science earned toward the
    /// next one). Standard KSP scenario persistence; OnSave/OnLoad ride the
    /// save-file ConfigNode, so all of it survives game restarts and stays
    /// separate between saves.
    ///
    /// Stars are identified by StarTarget.CatalogKey (a normalized designation,
    /// see StarNames.CatalogKeyForHost), never by object reference or list
    /// index: the catalog is rebuilt from CSV on every launch and its order or
    /// content may change between mod versions without invalidating old saves.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.SPACECENTER)]
    public class ExoInstrumentsScenario : ScenarioModule
    {
        public static ExoInstrumentsScenario Instance { get; private set; }

        private readonly HashSet<string> scannedStars = new HashSet<string>();
        private readonly HashSet<string> rewardedDetections = new HashSet<string>();

        // Stars whose one-time stellar-characterization award (direct imaging on
        // a star with a measurable temperature) has been claimed. Separate from
        // scannedStars: a star identified earlier by transit/RV can still yield
        // its characterization the first time it's actually imaged.
        private readonly HashSet<string> characterizedStars = new HashSet<string>();

        // Hosts whose one-time TTV award (sinusoidal transit-timing signal
        // confirmed against a real catalog companion) has been claimed, and
        // hosts whose Rossiter-McLaughlin spin-orbit measurement has been
        // claimed. Both separate from the detection bonus: they're follow-up
        // science on an already-detected system.
        private readonly HashSet<string> ttvRewardedHosts = new HashSet<string>();
        private readonly HashSet<string> rmRewardedHosts = new HashSet<string>();

        // Instrument unlock economy (see Core.InstrumentSpec/Observatories):
        // purchased instruments beyond each one's UnlockedByDefault, keyed by
        // InstrumentSpec.Name. TotalScienceEarned is *this mod's own* running
        // total awarded via RegisterScanCompleted, deliberately independent of
        // ResearchAndDevelopment.Instance.Science (the player's spendable R&D
        // balance), which drops every time they buy a stock tech-tree node. Using
        // the stock balance as the unlock gate would let spending Science on
        // unrelated parts lock the player back out of an instrument they'd
        // already earned the right to buy.
        private readonly HashSet<string> unlockedInstruments = new HashSet<string>();
        private double totalScienceEarned;

        // The supernova history is a pure function of this seed (see Core.Supernovae), so the
        // whole record of what exploded and when needs no persistence at all; only what the
        // player DISCOVERED does, keyed by event and carrying the designation plus the sky
        // position the logbook and the chart need without recomputing the host's light map.
        private long supernovaSeed;
        private readonly Dictionary<string, string> discoveredSupernovae = new Dictionary<string, string>();

        private const string ScannedValueName = "scannedStar";
        private const string RewardedValueName = "rewardedDetection";
        private const string CharacterizedValueName = "characterizedStar";
        private const string TtvRewardedValueName = "ttvRewardedHost";
        private const string RmRewardedValueName = "rmRewardedHost";
        private const string UnlockedInstrumentValueName = "unlockedInstrument";
        private const string TotalScienceEarnedValueName = "totalScienceEarned";
        private const string SupernovaSeedValueName = "supernovaSeed";
        private const string DiscoveredSupernovaValueName = "discoveredSupernova";

        public double TotalScienceEarned => totalScienceEarned;

        /// <summary>Nonzero once a save has one; generated lazily so pre-feature saves grow a history on first use.</summary>
        public long SupernovaSeed
        {
            get
            {
                if (supernovaSeed == 0)
                    supernovaSeed = DateTime.UtcNow.Ticks ^ (long)(Planetarium.GetUniversalTime() * 1000.0) ^ 0x53757065726e6fL;
                return supernovaSeed;
            }
        }

        /// <summary>One discovered supernova, as the chart and the logbook need it.</summary>
        public struct DiscoveredSupernova
        {
            public string Key;
            public string Designation;
            public string HostName;
            public string ClassLabel;
            public double RaDeg;
            public double DecDeg;
            public double ExplosionUt;
        }

        public IEnumerable<DiscoveredSupernova> DiscoveredSupernovae
        {
            get
            {
                foreach (KeyValuePair<string, string> kv in discoveredSupernovae)
                {
                    if (TryParseDiscovery(kv.Key, kv.Value, out DiscoveredSupernova d)) yield return d;
                }
            }
        }

        public bool IsSupernovaDiscovered(string key) => key != null && discoveredSupernovae.ContainsKey(key);

        public string SupernovaDesignation(string key)
        {
            return key != null && discoveredSupernovae.TryGetValue(key, out string v) ? v.Split('|')[0] : null;
        }

        /// <summary>
        /// Records a discovery and assigns its designation, "SN {year}{letters}" in the real
        /// convention: the year of the EXPLOSION on this save's own calendar, letters by order of
        /// discovery within it. False when it was already known.
        /// </summary>
        public bool TryDiscoverSupernova(string key, string hostName, string classLabel,
                                         double raDeg, double decDeg, double explosionUt,
                                         out string designation)
        {
            designation = SupernovaDesignation(key);
            if (designation != null) return false;

            int year = (int)(explosionUt / KSPUtil.dateTimeFormatter.Year) + 1;
            int inYear = 0;
            foreach (string v in discoveredSupernovae.Values)
            {
                string[] parts = v.Split('|');
                if (parts.Length >= 6
                    && double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double ut)
                    && (int)(ut / KSPUtil.dateTimeFormatter.Year) + 1 == year) inYear++;
            }

            designation = "SN " + year + Letters(inYear);
            discoveredSupernovae[key] = string.Join("|", designation, hostName, classLabel,
                raDeg.ToString("R", CultureInfo.InvariantCulture),
                decDeg.ToString("R", CultureInfo.InvariantCulture),
                explosionUt.ToString("R", CultureInfo.InvariantCulture));
            return true;
        }

        // a..z, then aa..az.., the IAU sequence.
        private static string Letters(int index)
        {
            string result = "";
            index += 1;
            while (index > 0)
            {
                index -= 1;
                result = (char)('a' + index % 26) + result;
                index /= 26;
            }
            return result;
        }

        private static bool TryParseDiscovery(string key, string value, out DiscoveredSupernova d)
        {
            d = default(DiscoveredSupernova);
            string[] p = value.Split('|');
            if (p.Length < 6) return false;
            d.Key = key;
            d.Designation = p[0];
            d.HostName = p[1];
            d.ClassLabel = p[2];
            if (!double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out d.RaDeg)) return false;
            if (!double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out d.DecDeg)) return false;
            if (!double.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out d.ExplosionUt)) return false;
            return true;
        }

        public bool IsInstrumentUnlocked(string instrumentName)
        {
            return instrumentName != null && unlockedInstruments.Contains(instrumentName);
        }

        public void MarkInstrumentUnlocked(string instrumentName)
        {
            if (instrumentName != null) unlockedInstruments.Add(instrumentName);
        }

        public void AddEarnedScience(double amount)
        {
            if (amount > 0.0) totalScienceEarned += amount;
        }

        public override void OnAwake()
        {
            base.OnAwake();
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// How many distinct stars the programme has surveyed. Drives the diminishing-returns
        /// curve on the first-scan award (see ScienceRewards.FirstScanAward): it is the count of
        /// stars, not of observations, so re-observing one target never moves it.
        /// </summary>
        public int ScannedCount => scannedStars.Count;

        public bool IsScanned(string catalogKey)
        {
            return catalogKey != null && scannedStars.Contains(catalogKey);
        }

        /// <summary>True when this call newly revealed the star (false if it was already scanned).</summary>
        public bool MarkScanned(string catalogKey)
        {
            return catalogKey != null && scannedStars.Add(catalogKey);
        }

        /// <summary>True when this call newly claimed the host's one-time detection bonus.</summary>
        public bool MarkDetectionRewarded(string catalogKey)
        {
            return catalogKey != null && rewardedDetections.Add(catalogKey);
        }

        /// <summary>True when this call newly claimed the star's one-time stellar-characterization award.</summary>
        public bool MarkCharacterized(string catalogKey)
        {
            return catalogKey != null && characterizedStars.Add(catalogKey);
        }

        /// <summary>True when this call newly claimed the host's one-time transit-timing-variation award.</summary>
        public bool MarkTtvRewarded(string catalogKey)
        {
            return catalogKey != null && ttvRewardedHosts.Add(catalogKey);
        }

        /// <summary>True when this call newly claimed the host's one-time Rossiter-McLaughlin award.</summary>
        public bool MarkRmRewarded(string catalogKey)
        {
            return catalogKey != null && rmRewardedHosts.Add(catalogKey);
        }

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            scannedStars.Clear();
            rewardedDetections.Clear();
            characterizedStars.Clear();
            ttvRewardedHosts.Clear();
            rmRewardedHosts.Clear();
            unlockedInstruments.Clear();
            totalScienceEarned = 0.0;
            supernovaSeed = 0;
            discoveredSupernovae.Clear();
            if (node.HasValue(SupernovaSeedValueName))
                long.TryParse(node.GetValue(SupernovaSeedValueName), NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out supernovaSeed);
            foreach (string entry in node.GetValues(DiscoveredSupernovaValueName))
            {
                int split = entry.IndexOf('=');
                if (split > 0) discoveredSupernovae[entry.Substring(0, split)] = entry.Substring(split + 1);
            }
            foreach (string key in node.GetValues(ScannedValueName)) scannedStars.Add(key);
            foreach (string key in node.GetValues(RewardedValueName)) rewardedDetections.Add(key);
            foreach (string key in node.GetValues(CharacterizedValueName)) characterizedStars.Add(key);
            foreach (string key in node.GetValues(TtvRewardedValueName)) ttvRewardedHosts.Add(key);
            foreach (string key in node.GetValues(RmRewardedValueName)) rmRewardedHosts.Add(key);
            foreach (string key in node.GetValues(UnlockedInstrumentValueName)) unlockedInstruments.Add(key);
            if (node.HasValue(TotalScienceEarnedValueName))
            {
                double.TryParse(node.GetValue(TotalScienceEarnedValueName), NumberStyles.Float, CultureInfo.InvariantCulture, out totalScienceEarned);
            }
        }

        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            foreach (string key in scannedStars) node.AddValue(ScannedValueName, key);
            foreach (string key in rewardedDetections) node.AddValue(RewardedValueName, key);
            foreach (string key in characterizedStars) node.AddValue(CharacterizedValueName, key);
            foreach (string key in ttvRewardedHosts) node.AddValue(TtvRewardedValueName, key);
            foreach (string key in rmRewardedHosts) node.AddValue(RmRewardedValueName, key);
            foreach (string key in unlockedInstruments) node.AddValue(UnlockedInstrumentValueName, key);
            node.AddValue(TotalScienceEarnedValueName, totalScienceEarned.ToString(CultureInfo.InvariantCulture));
            node.AddValue(SupernovaSeedValueName, SupernovaSeed.ToString(CultureInfo.InvariantCulture));
            foreach (KeyValuePair<string, string> kv in discoveredSupernovae)
                node.AddValue(DiscoveredSupernovaValueName, kv.Key + "=" + kv.Value);
        }
    }
}

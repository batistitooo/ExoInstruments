using System.Collections.Generic;
using System.Globalization;

namespace ExoInstruments
{
    /// <summary>
    /// Per-save state for the career progression loop: fog-of-war (which stars
    /// have been scanned/rewarded) and the instrument unlock economy (which
    /// telescopes have been purchased, and cumulative Science earned toward the
    /// next one). Standard KSP scenario persistence -- OnSave/OnLoad ride the
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
        // total awarded via RegisterScanCompleted -- deliberately independent of
        // ResearchAndDevelopment.Instance.Science (the player's spendable R&D
        // balance), which drops every time they buy a stock tech-tree node. Using
        // the stock balance as the unlock gate would let spending Science on
        // unrelated parts lock the player back out of an instrument they'd
        // already earned the right to buy.
        private readonly HashSet<string> unlockedInstruments = new HashSet<string>();
        private double totalScienceEarned;

        private const string ScannedValueName = "scannedStar";
        private const string RewardedValueName = "rewardedDetection";
        private const string CharacterizedValueName = "characterizedStar";
        private const string TtvRewardedValueName = "ttvRewardedHost";
        private const string RmRewardedValueName = "rmRewardedHost";
        private const string UnlockedInstrumentValueName = "unlockedInstrument";
        private const string TotalScienceEarnedValueName = "totalScienceEarned";

        public double TotalScienceEarned => totalScienceEarned;

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
        }
    }
}

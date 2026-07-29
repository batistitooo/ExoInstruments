using System;
using ExoInstruments.Core;

namespace ExoInstruments.Visualization
{
    public enum SkyTargetKind
    {
        None,
        /// <summary>A solar-system body, whose position is read live from the game.</summary>
        Body,
        /// <summary>A fixed direction on the celestial sphere: a star, a nebula, a galaxy, or a hand-entered coordinate.</summary>
        Equatorial,
    }

    /// <summary>
    /// What the telescope is pointed at.
    ///
    /// A CelestialBody moves and is looked up by transform; everything else on the sky sits at a
    /// fixed right ascension and declination and has to be aimed at through the observatory's own
    /// horizontal frame. Both are targets, and the capture pipeline should not care which it has.
    /// </summary>
    public struct SkyTarget : IEquatable<SkyTarget>
    {
        public SkyTargetKind Kind;
        public CelestialBody Body;
        public double RaDeg;
        public double DecDeg;
        private string name;

        public bool IsBody => Kind == SkyTargetKind.Body && Body != null;
        public bool IsEquatorial => Kind == SkyTargetKind.Equatorial;
        public bool HasTarget => IsBody || IsEquatorial;

        public static readonly SkyTarget None = default(SkyTarget);

        public static SkyTarget FromBody(CelestialBody body)
            => body == null ? None : new SkyTarget { Kind = SkyTargetKind.Body, Body = body };

        public static SkyTarget FromEquatorial(double raDeg, double decDeg, string displayName)
        {
            if (double.IsNaN(raDeg) || double.IsNaN(decDeg)) return None;
            return new SkyTarget
            {
                Kind = SkyTargetKind.Equatorial,
                RaDeg = SexagesimalCoordinates.Normalize360(raDeg),
                DecDeg = Math.Max(-90.0, Math.Min(90.0, decDeg)),
                name = displayName,
            };
        }

        public string DisplayName
        {
            get
            {
                if (IsBody) return Body.bodyName;
                if (!IsEquatorial) return "none";
                return string.IsNullOrEmpty(name) ? FormatCoordinates(RaDeg, DecDeg) : name;
            }
        }

        /// <summary>Sexagesimal, the form a catalogue quotes and the form an observer types.</summary>
        public static string FormatCoordinates(double raDeg, double decDeg)
            => SexagesimalCoordinates.Format(raDeg, decDeg);

        /// <summary>Parses "05 35 17.3" / "-05 23 28" or decimal degrees. See SexagesimalCoordinates.</summary>
        public static bool TryParse(string raText, string decText, out double raDeg, out double decDeg)
            => SexagesimalCoordinates.TryParse(raText, decText, out raDeg, out decDeg);

        /// <summary>Stable per-target seed for the capture RNG, standing in for a body's flightGlobalsIndex.</summary>
        public long Seed
        {
            get
            {
                if (IsBody) return Body.flightGlobalsIndex;
                if (!IsEquatorial) return 0L;
                // Milliarcsecond quantisation, so re-selecting the same catalogue entry reproduces
                // the same defect and cosmic-ray realisation.
                long ra = (long)Math.Round(RaDeg * 3.6e6);
                long dec = (long)Math.Round((DecDeg + 90.0) * 3.6e6);
                return unchecked(ra * 1000003L + dec);
            }
        }

        public bool Equals(SkyTarget other)
        {
            if (Kind != other.Kind) return false;
            if (Kind == SkyTargetKind.Body) return ReferenceEquals(Body, other.Body);
            if (Kind == SkyTargetKind.Equatorial) return RaDeg == other.RaDeg && DecDeg == other.DecDeg;
            return true;
        }

        public override bool Equals(object obj) => obj is SkyTarget t && Equals(t);
        public override int GetHashCode() => (int)Seed ^ (int)Kind;
        public static bool operator ==(SkyTarget a, SkyTarget b) => a.Equals(b);
        public static bool operator !=(SkyTarget a, SkyTarget b) => !a.Equals(b);

    }
}

// Minimal stand-ins for the Visualization-side types the Core files under test reference but
// never exercise. The harness runs Core physics only, outside Unity and outside KSP.
namespace ExoInstruments.Visualization
{
    public enum CameraFilter { Luminance, Red, Green, Blue, HAlpha, OIII, SII }
}

namespace ExoInstruments.Core
{
    /// <summary>
    /// Only the members SpaceObservingConditions and the pupil/PSF paths touch. The real
    /// VisualTelescopeSpec lives in VisualTelescopeCatalog.cs, which pulls in the whole
    /// instrument roster and its Unity-side dependencies; the physics under test here takes its
    /// numbers as arguments rather than from the catalogue, which is what makes that separation
    /// possible at all.
    /// </summary>
    public sealed class VisualTelescopeSpec
    {
        public string Name;
        public SpacePlatformSpec SpacePlatform;
        public bool IsSpaceBased => SpacePlatform != null;
    }
}

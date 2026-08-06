// Minimal stand-in for the one Visualization-side type VisualTelescopeCatalog names in its filter
// slots. The harness reads the catalogue's detector parameters and never touches a filter, so the
// enum only has to exist for the catalogue to compile outside Unity.
//
// Same approach as tools/spacecraft-tests/Stub.cs, and for the same reason: the physics under test
// is Core, and Core does not depend on Unity.
namespace ExoInstruments.Visualization
{
    public enum CameraFilter { Luminance, Red, Green, Blue, HAlpha, OIII, SII }
}

// Minimal stand-in for the type InstrumentSpec references and this harness never exercises.
//
// The real VisualTelescopeSpec lives in VisualTelescopeCatalog.cs, which pulls in the whole
// instrument roster; this harness only compares the pupil's diffraction pattern against POPPY and
// takes its aperture and obstruction as literals. Same pattern as tools/skyfield-tests/Stub.cs.
namespace ExoInstruments.Core
{
    public sealed class VisualTelescopeSpec
    {
        public string Name;
    }
}

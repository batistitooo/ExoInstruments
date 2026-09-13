// Stand-ins for the two Visualization enums the catalogue names. The harness reads optics and
// never a filter or an ND stop, so they only have to exist for Core to compile outside Unity.
namespace ExoInstruments.Visualization
{
    public enum CameraFilter { Luminance, Red, Green, Blue, HAlpha, OIII, SII, NII, OII, OI, Nuv220, Nuv250, Nuv330 }

    public enum NdFilterStop { None, Nd8, Nd64, Nd1000, Nd6300, Nd100000, ZimpolNd1, ZimpolNd2, ZimpolNd4 }
}

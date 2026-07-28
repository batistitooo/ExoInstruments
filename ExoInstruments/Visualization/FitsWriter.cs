using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ExoInstruments.Visualization
{
    /// <summary>
    /// Minimal, standards-conformant FITS (Flexible Image Transport System) writer: real
    /// astronomical image format, 16-bit data via the standard BZERO=32768/BSCALE=1 unsigned-
    /// representation convention, real acquisition-software header keywords matching the
    /// SharpCap/NINA/MaximDL conventions (EXPTIME, XPIXSZ/YPIXSZ, EGAIN, FOCALLEN, GAIN, FILTER,
    /// OBJECT, DATE-OBS), big-endian byte order and 80-byte-card/2880-byte-block padding as the
    /// FITS standard requires -- what a real telescope+camera would actually write to disk.
    ///
    /// The header carries a full TAN world coordinate system (see Core.FitsWcs), the instrument
    /// and site identity, and the observing conditions the exposure was taken through. That is
    /// what makes the file usable rather than merely well-formed: without a WCS nothing can be
    /// plate-solved or cross-matched, and without the conditions nothing downstream can recover
    /// the airmass, seeing or sky brightness, none of which survive in the pixels.
    /// </summary>
    public static class FitsWriter
    {
        private const int BlockSizeBytes = 2880;
        private const int CardSizeBytes = 80;

        public struct FitsHeaderInfo
        {
            public double ExposureSeconds;
            public double PixelSizeMicrons;
            public double FullWellElectrons;
            /// <summary>Real conversion factor K, electrons per ADU, at the gain this frame was taken with. Goes straight into EGAIN.</summary>
            public double ElectronsPerAdu;
            /// <summary>Bit depth of the real converter that produced these counts.</summary>
            public int AdcBits;
            /// <summary>Count at which this frame stopped responding -- the smaller of the physical well and the converter ceiling, expressed in ADU.</summary>
            public double SaturationAdu;
            /// <summary>
            /// True for a raw single frame straight off the converter, where the counts really
            /// are ADU and EGAIN really does return electrons. False for a processed product
            /// (a stacked, aligned, luminance-transferred LRGB composite), where quoting a
            /// conversion factor would be a lie: the pixel values have been through steps no
            /// header keyword describes.
            /// </summary>
            public bool IsCalibratedAdu;
            public double FocalLengthMm;
            public float Gain;
            public string FilterName;
            public string ObjectName;
            public DateTime UtcTimestamp;

            // --- Instrument and site identity ---------------------------------------------
            // A reduction pipeline keys off these: which telescope, which detector, which sky.
            /// <summary>Telescope name, TELESCOP.</summary>
            public string TelescopeName;
            /// <summary>Instrument/camera name, INSTRUME.</summary>
            public string InstrumentName;
            /// <summary>Observatory/site name, OBSERVAT.</summary>
            public string ObservatoryName;
            public double SiteLatitudeDeg;
            public double SiteLongitudeDeg;
            public double SiteElevationMeters;
            /// <summary>On-chip binning factor, written to XBINNING/YBINNING (square binning throughout this pipeline).</summary>
            public int BinningFactor;
            /// <summary>Read noise in electrons, RDNOISE -- the number a reduction needs to weight pixels correctly.</summary>
            public double ReadNoiseElectrons;
            /// <summary>Dark current, e-/s/pixel at the detector's own operating temperature.</summary>
            public double DarkCurrentElectronsPerSecond;
            /// <summary>Detector operating temperature in Celsius, CCD-TEMP. NaN when the instrument's is not modelled.</summary>
            public double DetectorTemperatureCelsius;
            /// <summary>Aperture diameter in metres, APTDIA.</summary>
            public double ApertureMeters;

            // --- Observing conditions ----------------------------------------------------
            // These are the frame's own metadata in the real sense: they were true of this
            // exposure and nothing else, and no amount of later processing can recover them.
            /// <summary>Airmass at the field centre. NaN or non-finite when the target was below the horizon.</summary>
            public double Airmass;
            /// <summary>
            /// The atmospheric FWHM (arcsec) the PSF was actually built with, and the instrument's
            /// diffraction-core FWHM at this filter. Written as two separate keywords rather than
            /// one "delivered FWHM", because the pipeline knows these two and combining them into a
            /// single figure would require an addition rule that is exact for neither an Airy
            /// pattern nor a Kolmogorov profile -- the same reason OpticalPsf solves for the
            /// atmospheric residual by bisection instead of subtracting in quadrature.
            /// </summary>
            public double SeeingFwhmArcsec;
            public double DiffractionFwhmArcsec;
            /// <summary>Sky background, V mag/arcsec^2 -- the quantity SkyBrightnessModel computes and publishes in.</summary>
            public double SkyBrightnessVMagPerArcsec2;
            /// <summary>Filter's central wavelength and FWHM, nanometres, so a colour term can be recomputed downstream.</summary>
            public double FilterCentralWavelengthNm;
            public double FilterBandwidthNm;
            /// <summary>IMAGETYP, following the de-facto acquisition-software vocabulary ('Light Frame', 'Dark Frame', ...).</summary>
            public string ImageType;

            /// <summary>
            /// The two numbers that make this frame's photometry reproducible from the header
            /// alone: the grey optical throughput (mirrors, relay, filter peak) and the effective
            /// photometric width in Angstrom for a flat spectrum (see Core.SystemResponse). With
            /// the aperture area and EXPTIME, a reader can recompute what electron count any
            /// apparent magnitude should have produced, instead of taking the frame on trust.
            /// </summary>
            public double OpticalThroughput;
            public double EffectiveWidthAngstrom;

            // --- World coordinate system --------------------------------------------------
            /// <summary>Where this frame pointed. Written only when Wcs.IsValid: a wrong WCS is worse than none.</summary>
            public Core.FitsWcs Wcs;
            /// <summary>
            /// True when the frame was taken unguided, so the sky rotated during the exposure. The
            /// WCS then describes the sky at DATE-OBS, the start of the exposure, and every source
            /// is a trail running forward from its catalogued position -- which a HISTORY card says
            /// outright, because a plate solve of such a frame will not converge and the reason
            /// should be in the file rather than inferred.
            /// </summary>
            public bool TrailedByDrift;
        }

        /// <summary>
        /// Writes a frame of real ADU counts as a 16-bit FITS file.
        ///
        /// The values handed in are the detector's own digital output, not a display image: they
        /// are written unaltered, so EGAIN really does convert them back to electrons and the
        /// file can be reduced like an observed one. Writing a normalised [0,1] display frame
        /// rescaled to 65535 -- which is what this used to receive -- produced a file whose
        /// EGAIN was meaningless, because the counts had already been through a stretch and a
        /// renormalisation that no header keyword described.
        /// </summary>
        public static void WriteGrayscale(string path, float[] aduCounts, int width, int height, FitsHeaderInfo info)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                WriteHeader(stream, width, height, info);
                WriteData(stream, aduCounts, width, height);
            }
        }

        private static void WriteHeader(FileStream stream, int width, int height, FitsHeaderInfo info)
        {
            var sb = new StringBuilder();
            AppendCard(sb, "SIMPLE", "T", "conforms to FITS standard");
            AppendCard(sb, "BITPIX", "16", "16-bit signed (BZERO/BSCALE give unsigned)");
            AppendCard(sb, "NAXIS", "2", "2-dimensional image");
            AppendCard(sb, "NAXIS1", width.ToString(CultureInfo.InvariantCulture), "image width, pixels");
            AppendCard(sb, "NAXIS2", height.ToString(CultureInfo.InvariantCulture), "image height, pixels");
            AppendCard(sb, "BZERO", "32768", "offset for unsigned 16-bit representation");
            AppendCard(sb, "BSCALE", "1", "data scaling");
            AppendCard(sb, "EXPTIME", info.ExposureSeconds.ToString("F6", CultureInfo.InvariantCulture), "exposure time (s)");
            AppendCard(sb, "XPIXSZ", info.PixelSizeMicrons.ToString("F3", CultureInfo.InvariantCulture), "pixel width (um)");
            AppendCard(sb, "YPIXSZ", info.PixelSizeMicrons.ToString("F3", CultureInfo.InvariantCulture), "pixel height (um)");
            AppendCard(sb, "FULLWELL", info.FullWellElectrons.ToString("F1", CultureInfo.InvariantCulture), "pixel full well (e-)");
            AppendCard(sb, "ADCBITS", info.AdcBits.ToString(CultureInfo.InvariantCulture), "converter bit depth");
            if (info.IsCalibratedAdu)
            {
                AppendCard(sb, "EGAIN", info.ElectronsPerAdu.ToString("F6", CultureInfo.InvariantCulture), "electrons per ADU (real conversion factor K)");
                AppendCard(sb, "SATURATE", info.SaturationAdu.ToString("F1", CultureInfo.InvariantCulture), "saturation level (adu)");
                AppendStringCard(sb, "BUNIT", "adu", "raw converter counts");
            }
            else
            {
                AppendStringCard(sb, "BUNIT", "", "processed product -- not raw counts");
                AppendCommentaryCard(sb, "HISTORY", "stacked/aligned composite; EGAIN omitted deliberately");
            }
            AppendCard(sb, "FOCALLEN", info.FocalLengthMm.ToString("F2", CultureInfo.InvariantCulture), "focal length (mm)");
            AppendCard(sb, "GAIN", info.Gain.ToString("F3", CultureInfo.InvariantCulture), "camera gain setting");
            AppendStringCard(sb, "FILTER", info.FilterName, "filter name");
            AppendStringCard(sb, "OBJECT", info.ObjectName, "target name");
            AppendStringCard(sb, "DATE-OBS", info.UtcTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture), "UTC observation start");

            WriteInstrumentCards(sb, info);
            WriteConditionCards(sb, info);
            WriteWcsCards(sb, info);

            AppendEnd(sb);

            // The header, like the data, must be padded to a whole number of 2880-byte
            // blocks -- with blank (all-space) 80-byte cards, per the FITS standard.
            int cardCount = sb.Length / CardSizeBytes;
            int cardsPerBlock = BlockSizeBytes / CardSizeBytes;
            int remainderCards = cardCount % cardsPerBlock;
            if (remainderCards != 0)
            {
                int padCards = cardsPerBlock - remainderCards;
                sb.Append(new string(' ', padCards * CardSizeBytes));
            }

            byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
        }

        /// <summary>
        /// Which telescope, which detector, which site. Keyword names follow the conventions real
        /// acquisition software writes and real reduction software reads (TELESCOP/INSTRUME/
        /// OBSERVAT from the FITS standard's own reserved list; SITELAT/SITELONG/XBINNING/CCD-TEMP
        /// from the SBIG-derived vocabulary that SharpCap, NINA, MaxIm DL and PixInsight all use).
        /// </summary>
        private static void WriteInstrumentCards(StringBuilder sb, FitsHeaderInfo info)
        {
            AppendStringCard(sb, "TELESCOP", info.TelescopeName, "telescope");
            AppendStringCard(sb, "INSTRUME", info.InstrumentName, "instrument / camera");
            AppendStringCard(sb, "OBSERVAT", info.ObservatoryName, "observatory site");
            AppendStringCard(sb, "IMAGETYP", info.ImageType, "frame type");
            AppendCard(sb, "SITELAT", info.SiteLatitudeDeg.ToString("F6", CultureInfo.InvariantCulture), "site latitude (deg)");
            AppendCard(sb, "SITELONG", info.SiteLongitudeDeg.ToString("F6", CultureInfo.InvariantCulture), "site longitude (deg, E positive)");
            AppendCard(sb, "SITEELEV", info.SiteElevationMeters.ToString("F1", CultureInfo.InvariantCulture), "site elevation (m)");
            if (info.ApertureMeters > 0.0)
                AppendCard(sb, "APTDIA", (info.ApertureMeters * 1000.0).ToString("F1", CultureInfo.InvariantCulture), "aperture diameter (mm)");
            if (info.BinningFactor > 0)
            {
                AppendCard(sb, "XBINNING", info.BinningFactor.ToString(CultureInfo.InvariantCulture), "binning factor, x");
                AppendCard(sb, "YBINNING", info.BinningFactor.ToString(CultureInfo.InvariantCulture), "binning factor, y");
            }
            if (info.ReadNoiseElectrons > 0.0)
                AppendCard(sb, "RDNOISE", info.ReadNoiseElectrons.ToString("F2", CultureInfo.InvariantCulture), "read noise (e-)");
            if (info.DarkCurrentElectronsPerSecond > 0.0)
                AppendCard(sb, "DARKCURR", info.DarkCurrentElectronsPerSecond.ToString("G6", CultureInfo.InvariantCulture), "dark current (e-/s/pixel)");
            if (IsFinite(info.DetectorTemperatureCelsius))
                AppendCard(sb, "CCD-TEMP", info.DetectorTemperatureCelsius.ToString("F1", CultureInfo.InvariantCulture), "detector temperature (C)");
        }

        /// <summary>
        /// What the sky was doing. Recorded because it is irrecoverable: a reduction can re-derive
        /// a plate scale from the WCS, but nothing downstream can reconstruct the airmass, seeing
        /// or sky brightness this particular exposure was taken through.
        /// </summary>
        private static void WriteConditionCards(StringBuilder sb, FitsHeaderInfo info)
        {
            if (IsFinite(info.Airmass) && info.Airmass >= 1.0)
                AppendCard(sb, "AIRMASS", info.Airmass.ToString("F4", CultureInfo.InvariantCulture), "airmass at field centre");
            if (IsFinite(info.SeeingFwhmArcsec) && info.SeeingFwhmArcsec > 0.0)
                AppendCard(sb, "SEEING", info.SeeingFwhmArcsec.ToString("F4", CultureInfo.InvariantCulture), "atmospheric FWHM used (arcsec)");
            if (IsFinite(info.DiffractionFwhmArcsec) && info.DiffractionFwhmArcsec > 0.0)
                AppendCard(sb, "DIFFLIM", info.DiffractionFwhmArcsec.ToString("F5", CultureInfo.InvariantCulture), "diffraction core FWHM (arcsec)");
            if (IsFinite(info.SkyBrightnessVMagPerArcsec2))
                AppendCard(sb, "SKYMAG", info.SkyBrightnessVMagPerArcsec2.ToString("F3", CultureInfo.InvariantCulture), "sky background (V mag/arcsec2)");
            if (info.FilterCentralWavelengthNm > 0.0)
                AppendCard(sb, "WAVELNTH", info.FilterCentralWavelengthNm.ToString("F2", CultureInfo.InvariantCulture), "filter central wavelength (nm)");
            if (info.FilterBandwidthNm > 0.0)
                AppendCard(sb, "BANDWID", info.FilterBandwidthNm.ToString("F2", CultureInfo.InvariantCulture), "filter FWHM (nm)");
            if (info.OpticalThroughput > 0.0)
                AppendCard(sb, "THROUGHP", info.OpticalThroughput.ToString("F5", CultureInfo.InvariantCulture), "optics throughput (mirrors x relay x filter)");
            if (info.EffectiveWidthAngstrom > 0.0)
                AppendCard(sb, "PHOTWIDT", info.EffectiveWidthAngstrom.ToString("F3", CultureInfo.InvariantCulture), "effective photometric width, flat SED (A)");
        }

        /// <summary>
        /// The world coordinate system: a TAN (gnomonic) projection described exactly as
        /// Calabretta &amp; Greisen (2002) prescribe. Written only when the geometry resolved -- a
        /// header claiming a pointing it does not have would send a plate solve or a catalogue
        /// cross-match somewhere wrong and silently, which is worse than a frame that admits it
        /// does not know where it looked.
        /// </summary>
        private static void WriteWcsCards(StringBuilder sb, FitsHeaderInfo info)
        {
            if (!info.Wcs.IsValid) return;

            AppendStringCard(sb, "CTYPE1", "RA---TAN", "gnomonic projection, RA axis");
            AppendStringCard(sb, "CTYPE2", "DEC--TAN", "gnomonic projection, Dec axis");
            AppendStringCard(sb, "CUNIT1", "deg", "");
            AppendStringCard(sb, "CUNIT2", "deg", "");
            AppendCard(sb, "EQUINOX", "2000.0", "equinox of the catalogue frame");
            AppendStringCard(sb, "RADESYS", "ICRS", "reference frame of the star catalogue");
            AppendCard(sb, "CRVAL1", info.Wcs.ReferenceRaDeg.ToString("F9", CultureInfo.InvariantCulture), "RA at reference pixel (deg)");
            AppendCard(sb, "CRVAL2", info.Wcs.ReferenceDecDeg.ToString("F9", CultureInfo.InvariantCulture), "Dec at reference pixel (deg)");
            AppendCard(sb, "CRPIX1", info.Wcs.ReferencePixelX.ToString("F4", CultureInfo.InvariantCulture), "reference pixel, x (1-based)");
            AppendCard(sb, "CRPIX2", info.Wcs.ReferencePixelY.ToString("F4", CultureInfo.InvariantCulture), "reference pixel, y (1-based)");
            AppendCard(sb, "CD1_1", info.Wcs.Cd11.ToString("E12", CultureInfo.InvariantCulture), "deg/pixel");
            AppendCard(sb, "CD1_2", info.Wcs.Cd12.ToString("E12", CultureInfo.InvariantCulture), "deg/pixel");
            AppendCard(sb, "CD2_1", info.Wcs.Cd21.ToString("E12", CultureInfo.InvariantCulture), "deg/pixel");
            AppendCard(sb, "CD2_2", info.Wcs.Cd22.ToString("E12", CultureInfo.InvariantCulture), "deg/pixel");
            AppendCard(sb, "SECPIX1", info.Wcs.ScaleXArcsecPerPixel.ToString("F6", CultureInfo.InvariantCulture), "plate scale, x (arcsec/pixel)");
            AppendCard(sb, "SECPIX2", info.Wcs.ScaleYArcsecPerPixel.ToString("F6", CultureInfo.InvariantCulture), "plate scale, y (arcsec/pixel)");
            AppendStringCard(sb, "OBJCTRA", FormatRaSexagesimal(info.Wcs.ReferenceRaDeg), "RA at reference pixel (h m s)");
            AppendStringCard(sb, "OBJCTDEC", FormatDecSexagesimal(info.Wcs.ReferenceDecDeg), "Dec at reference pixel (d m s)");

            if (info.TrailedByDrift)
                AppendCommentaryCard(sb, "HISTORY", "unguided: WCS valid at DATE-OBS; sources are trailed");
        }

        /// <summary>Right ascension as hours, minutes and seconds -- the sexagesimal form OBJCTRA carries by convention.</summary>
        private static string FormatRaSexagesimal(double raDeg)
        {
            double hours = ((raDeg % 360.0) + 360.0) % 360.0 / 15.0;
            int h = (int)hours;
            double remainderMinutes = (hours - h) * 60.0;
            int m = (int)remainderMinutes;
            double s = (remainderMinutes - m) * 60.0;
            return string.Format(CultureInfo.InvariantCulture, "{0:00} {1:00} {2:00.00}", h, m, s);
        }

        /// <summary>Declination as degrees, arcminutes and arcseconds, sign always explicit.</summary>
        private static string FormatDecSexagesimal(double decDeg)
        {
            char sign = decDeg < 0.0 ? '-' : '+';
            double absolute = Math.Abs(decDeg);
            int d = (int)absolute;
            double remainderMinutes = (absolute - d) * 60.0;
            int m = (int)remainderMinutes;
            double s = (remainderMinutes - m) * 60.0;
            return string.Format(CultureInfo.InvariantCulture, "{0}{1:00} {2:00} {3:00.0}", sign, d, m, s);
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private static void AppendCard(StringBuilder sb, string keyword, string value, string comment)
        {
            string card = keyword.PadRight(8) + "= " + value.PadLeft(20) + " / " + comment;
            sb.Append(FitCard(card));
        }

        private static void AppendStringCard(StringBuilder sb, string keyword, string value, string comment)
        {
            string safe = (value ?? "unknown").Replace("'", "");
            // The card is truncated to 80 bytes at the end, so a long value would lose its own
            // CLOSING QUOTE and leave an unparseable card behind. Bounding the value here means an
            // over-long one costs its tail and the comment, never the syntax. 68 = 80 - 8 for the
            // keyword - 2 for "= " - 2 for the quotes.
            const int MaxValueLength = 68;
            if (safe.Length > MaxValueLength) safe = safe.Substring(0, MaxValueLength);
            string quoted = "'" + safe.PadRight(Math.Max(8, safe.Length)) + "'";
            string card = keyword.PadRight(8) + "= " + quoted + " / " + comment;
            sb.Append(FitCard(card));
        }

        /// <summary>
        /// A commentary card (HISTORY, COMMENT). These are NOT value cards: the FITS standard
        /// gives them the keyword in columns 1-8 and free text from column 9, with no '= ' value
        /// indicator, and a conforming parser will reject or mangle one written in value form.
        /// </summary>
        private static void AppendCommentaryCard(StringBuilder sb, string keyword, string text)
        {
            sb.Append(FitCard(keyword.PadRight(8) + " " + (text ?? string.Empty)));
        }

        private static void AppendEnd(StringBuilder sb)
        {
            sb.Append("END".PadRight(CardSizeBytes));
        }

        private static string FitCard(string card)
        {
            return card.Length > CardSizeBytes ? card.Substring(0, CardSizeBytes) : card.PadRight(CardSizeBytes);
        }

        private static void WriteData(FileStream stream, float[] aduCounts, int width, int height)
        {
            int n = width * height;
            byte[] buffer = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                int unsignedValue = Mathf.Clamp(Mathf.RoundToInt(aduCounts[i]), 0, 65535);
                short signedValue = (short)(unsignedValue - 32768);

                // FITS data is big-endian regardless of host byte order.
                buffer[i * 2] = (byte)((signedValue >> 8) & 0xFF);
                buffer[i * 2 + 1] = (byte)(signedValue & 0xFF);
            }
            stream.Write(buffer, 0, buffer.Length);

            // Data (like the header) must be padded to a multiple of 2880 bytes; zero-padded, not space-padded.
            int remainder = buffer.Length % BlockSizeBytes;
            if (remainder != 0)
            {
                int padLength = BlockSizeBytes - remainder;
                var pad = new byte[padLength];
                stream.Write(pad, 0, padLength);
            }
        }
    }
}

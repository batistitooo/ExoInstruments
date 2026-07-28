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
                AppendStringCard(sb, "HISTORY", "stacked/aligned composite; EGAIN omitted deliberately", "");
            }
            AppendCard(sb, "FOCALLEN", info.FocalLengthMm.ToString("F2", CultureInfo.InvariantCulture), "focal length (mm)");
            AppendCard(sb, "GAIN", info.Gain.ToString("F3", CultureInfo.InvariantCulture), "camera gain setting");
            AppendStringCard(sb, "FILTER", info.FilterName, "filter name");
            AppendStringCard(sb, "OBJECT", info.ObjectName, "target name");
            AppendStringCard(sb, "DATE-OBS", info.UtcTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture), "UTC observation start");
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

        private static void AppendCard(StringBuilder sb, string keyword, string value, string comment)
        {
            string card = keyword.PadRight(8) + "= " + value.PadLeft(20) + " / " + comment;
            sb.Append(FitCard(card));
        }

        private static void AppendStringCard(StringBuilder sb, string keyword, string value, string comment)
        {
            string safe = (value ?? "unknown").Replace("'", "");
            string quoted = "'" + safe.PadRight(Math.Max(8, safe.Length)) + "'";
            string card = keyword.PadRight(8) + "= " + quoted + " / " + comment;
            sb.Append(FitCard(card));
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

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ExoInstruments.Core
{
    /// <summary>
    /// Reads a 2-D image out of a FITS file: the primary HDU's header, its data array, and the
    /// linear transform that turns stored integers into the physical values they stand for.
    ///
    /// WHY THIS EXISTS. `Visualization/FitsWriter.cs` has been able to WRITE a frame since the
    /// pipeline started producing calibratable data. Nothing could read one, which meant every
    /// quantity this pipeline works from had to be either computed or generated from a seed. That is
    /// the whole of section 12's old item 64: a measured per-pixel flat could not be loaded, where
    /// Pyxel can take a real flat-field image as its PRNU map. A measured map is strictly better
    /// than any parametric model, and the reason is not that the model is bad but that the thing
    /// being modelled is not a property of the instrument at all (see Core.MeasuredFlatField).
    ///
    /// WHAT IS SUPPORTED, and it is the intersection of the standard and what real acquisition
    /// software actually writes:
    ///
    ///   * The PRIMARY HDU only. Extensions are not read. A flat written by SharpCap, NINA, MaxIm
    ///     DL, PixInsight, ccdproc or this mod's own writer is a primary-HDU image.
    ///   * NAXIS = 2. A cube or a table is rejected by name rather than silently reinterpreted.
    ///   * Every BITPIX the standard defines: 8 (unsigned bytes), 16 and 32 and 64 (signed,
    ///     two's complement), -32 and -64 (IEEE floats). All big-endian, which FITS is regardless
    ///     of the host.
    ///   * BZERO and BSCALE, applied as physical = BZERO + BSCALE * stored. This is not optional
    ///     decoration: it is how FITS represents unsigned 16-bit data, which is what essentially
    ///     every camera writes, and a reader that ignores it reads a 60000-count flat as -5536.
    ///   * BLANK, for integer BITPIX, mapped to NaN so an undefined pixel stays undefined instead
    ///     of becoming a very large number.
    ///
    /// WHAT IS DELIBERATELY REJECTED RATHER THAN GUESSED. A file whose SIMPLE card is not T is not
    /// a FITS file and is refused by that name; the standard says a conforming reader may not
    /// assume anything about it. A truncated data segment is refused rather than zero-filled. A
    /// header with no END card inside a sane number of blocks is refused rather than scanned to the
    /// end of the disk. Each one throws with the keyword and the value that failed, because the
    /// person who has to act on the message is holding the file.
    ///
    /// ROW ORDER IS THE FILE'S, NOT FLIPPED. FITS numbers rows from the bottom, and most display
    /// software flips on load; this reader returns the array in the order the file stores it and
    /// says so, because the caller that matters here is matching a flat to a frame, and the frame
    /// this project writes is written in its own order by `FitsWriter.WriteData` with no flip
    /// either. Round-tripping through the two is therefore the identity, which is what
    /// tools/flat-tests checks first.
    ///
    /// Pure C#, no Unity dependency.
    /// </summary>
    public static class FitsImageReader
    {
        public const int BlockSizeBytes = 2880;
        public const int CardSizeBytes = 80;

        /// <summary>A header block is 36 cards; refuse a header longer than this many blocks rather than scanning forever.</summary>
        private const int MaxHeaderBlocks = 256;

        /// <summary>What came out of the file: the image, its shape, and the header cards verbatim.</summary>
        public sealed class Image
        {
            /// <summary>Physical values, BZERO/BSCALE already applied, in the file's own row order. NaN where BLANK.</summary>
            public double[] Values;
            public int Width;
            public int Height;
            public int BitPix;
            public double BZero;
            public double BScale;

            /// <summary>Every keyword the header carried, uppercased and trimmed, for a caller that wants one.</summary>
            public System.Collections.Generic.Dictionary<string, string> Cards;

            public int PixelCount => Width * Height;

            /// <summary>The raw card value for a keyword, or null. Commentary cards are not indexed.</summary>
            public string Card(string keyword)
            {
                if (Cards == null || keyword == null) return null;
                return Cards.TryGetValue(keyword.Trim().ToUpperInvariant(), out string v) ? v : null;
            }
        }

        /// <summary>Thrown for a file that is not readable as a 2-D FITS image, with the reason a user can act on.</summary>
        public sealed class FormatException : Exception
        {
            public FormatException(string message) : base(message) { }
        }

        public static Image Read(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new FormatException("No file path given.");
            if (!File.Exists(path)) throw new FormatException("No such file: " + path);

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                return Read(stream, path);
        }

        public static Image Read(Stream stream, string nameForMessages)
        {
            var cards = ReadHeader(stream, nameForMessages, out int headerBlocks);

            // SIMPLE must be T. The standard is explicit that a conforming reader may assume
            // nothing about a file that says otherwise, so this is refused rather than attempted.
            string simple = Value(cards, "SIMPLE");
            if (simple == null || simple.Trim().ToUpperInvariant() != "T")
                throw new FormatException(nameForMessages + ": not a FITS file (SIMPLE is "
                    + (simple ?? "absent") + ", expected T).");

            int naxis = RequireInt(cards, "NAXIS", nameForMessages);
            if (naxis != 2)
                throw new FormatException(nameForMessages + ": NAXIS is " + naxis
                    + ", and only a 2-dimensional image can be read. A cube or a table is not"
                    + " reinterpreted as one.");

            int width = RequireInt(cards, "NAXIS1", nameForMessages);
            int height = RequireInt(cards, "NAXIS2", nameForMessages);
            if (width <= 0 || height <= 0)
                throw new FormatException(nameForMessages + ": image dimensions are "
                    + width + "x" + height + ", which is empty.");

            int bitpix = RequireInt(cards, "BITPIX", nameForMessages);
            int bytesPerValue = BytesPerValue(bitpix, nameForMessages);

            double bzero = OptionalDouble(cards, "BZERO", 0.0, nameForMessages);
            double bscale = OptionalDouble(cards, "BSCALE", 1.0, nameForMessages);
            if (bscale == 0.0)
                throw new FormatException(nameForMessages + ": BSCALE is zero, which would map"
                    + " every pixel to the same value.");

            bool hasBlank = false;
            long blank = 0;
            if (bitpix > 0)
            {
                string raw = Value(cards, "BLANK");
                if (raw != null && long.TryParse(raw.Trim(), NumberStyles.Integer,
                                                 CultureInfo.InvariantCulture, out blank))
                    hasBlank = true;
            }

            long pixelCount = (long)width * height;
            long dataBytes = pixelCount * bytesPerValue;

            var buffer = new byte[dataBytes];
            int read = ReadFully(stream, buffer, dataBytes);
            if (read != dataBytes)
                throw new FormatException(nameForMessages + ": data segment is truncated. The header"
                    + " declares " + width + "x" + height + " at BITPIX " + bitpix + " (" + dataBytes
                    + " bytes) and the file holds " + read + ". Refused rather than zero-filled.");

            var values = new double[pixelCount];
            Decode(buffer, values, bitpix, bytesPerValue, bzero, bscale, hasBlank, blank);

            return new Image
            {
                Values = values,
                Width = width,
                Height = height,
                BitPix = bitpix,
                BZero = bzero,
                BScale = bscale,
                Cards = cards,
            };
        }

        // ------------------------------------------------------------------ header

        private static System.Collections.Generic.Dictionary<string, string> ReadHeader(
            Stream stream, string nameForMessages, out int headerBlocks)
        {
            var cards = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            var block = new byte[BlockSizeBytes];
            headerBlocks = 0;

            while (headerBlocks < MaxHeaderBlocks)
            {
                int got = ReadFully(stream, block, BlockSizeBytes);
                if (got != BlockSizeBytes)
                    throw new FormatException(nameForMessages + ": file ends inside the header,"
                        + " after " + headerBlocks + " complete block(s).");
                headerBlocks++;

                for (int offset = 0; offset < BlockSizeBytes; offset += CardSizeBytes)
                {
                    string card = Encoding.ASCII.GetString(block, offset, CardSizeBytes);
                    string keyword = card.Substring(0, 8).Trim().ToUpperInvariant();

                    if (keyword == "END") return cards;
                    if (keyword.Length == 0) continue;

                    // Commentary cards (COMMENT, HISTORY, and blank-keyword cards) carry no value
                    // in the '= ' sense and are not indexed. A reader that parsed them as values
                    // would be inventing keywords out of free text.
                    if (keyword == "COMMENT" || keyword == "HISTORY") continue;
                    if (card.Length < 10 || card[8] != '=') continue;

                    cards[keyword] = ParseValue(card.Substring(9));
                }
            }

            throw new FormatException(nameForMessages + ": no END card in the first "
                + MaxHeaderBlocks + " header blocks; refusing to scan further.");
        }

        /// <summary>
        /// The value field of a card, with the trailing comment removed and a quoted string
        /// unquoted.
        ///
        /// The slash that starts a comment does not count inside a quoted string, which is why this
        /// walks the field rather than calling IndexOf('/'): a FILTER card reading 'Ha 3nm / OIII'
        /// is one string value, not a value and a comment.
        /// </summary>
        private static string ParseValue(string field)
        {
            int i = 0;
            while (i < field.Length && field[i] == ' ') i++;
            if (i >= field.Length) return string.Empty;

            if (field[i] == '\'')
            {
                var sb = new StringBuilder();
                i++;
                while (i < field.Length)
                {
                    if (field[i] == '\'')
                    {
                        // Two quotes in a row are one literal quote, per the standard.
                        if (i + 1 < field.Length && field[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        break;
                    }
                    sb.Append(field[i]);
                    i++;
                }
                return sb.ToString().TrimEnd();
            }

            int slash = field.IndexOf('/', i);
            string value = slash >= 0 ? field.Substring(i, slash - i) : field.Substring(i);
            return value.Trim();
        }

        private static string Value(System.Collections.Generic.Dictionary<string, string> cards, string keyword)
            => cards.TryGetValue(keyword, out string v) ? v : null;

        private static int RequireInt(System.Collections.Generic.Dictionary<string, string> cards,
                                      string keyword, string nameForMessages)
        {
            string raw = Value(cards, keyword);
            if (raw == null)
                throw new FormatException(nameForMessages + ": mandatory keyword " + keyword + " is missing.");
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                throw new FormatException(nameForMessages + ": " + keyword + " is '" + raw
                    + "', which is not an integer.");
            return v;
        }

        private static double OptionalDouble(System.Collections.Generic.Dictionary<string, string> cards,
                                             string keyword, double fallback, string nameForMessages)
        {
            string raw = Value(cards, keyword);
            if (raw == null) return fallback;
            // FITS permits Fortran's D exponent as well as E.
            string normalised = raw.Trim().Replace("D", "E").Replace("d", "E");
            if (!double.TryParse(normalised, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                throw new FormatException(nameForMessages + ": " + keyword + " is '" + raw
                    + "', which is not a number.");
            return v;
        }

        // ------------------------------------------------------------------ data

        private static int BytesPerValue(int bitpix, string nameForMessages)
        {
            switch (bitpix)
            {
                case 8: return 1;
                case 16: return 2;
                case 32: return 4;
                case 64: return 8;
                case -32: return 4;
                case -64: return 8;
                default:
                    throw new FormatException(nameForMessages + ": BITPIX is " + bitpix
                        + ", which is not one of the values the FITS standard defines"
                        + " (8, 16, 32, 64, -32, -64).");
            }
        }

        private static void Decode(byte[] buffer, double[] values, int bitpix, int bytesPerValue,
                                   double bzero, double bscale, bool hasBlank, long blank)
        {
            int n = values.Length;
            for (int i = 0; i < n; i++)
            {
                int at = i * bytesPerValue;
                double physical;

                switch (bitpix)
                {
                    case 8:
                        {
                            // BITPIX 8 is UNSIGNED, alone among the integer types.
                            long v = buffer[at];
                            physical = (hasBlank && v == blank) ? double.NaN : bzero + bscale * v;
                            break;
                        }
                    case 16:
                        {
                            short v = (short)((buffer[at] << 8) | buffer[at + 1]);
                            physical = (hasBlank && v == blank) ? double.NaN : bzero + bscale * v;
                            break;
                        }
                    case 32:
                        {
                            int v = (buffer[at] << 24) | (buffer[at + 1] << 16)
                                  | (buffer[at + 2] << 8) | buffer[at + 3];
                            physical = (hasBlank && v == blank) ? double.NaN : bzero + bscale * v;
                            break;
                        }
                    case 64:
                        {
                            long v = 0;
                            for (int b = 0; b < 8; b++) v = (v << 8) | buffer[at + b];
                            physical = (hasBlank && v == blank) ? double.NaN : bzero + bscale * v;
                            break;
                        }
                    case -32:
                        {
                            int bits = (buffer[at] << 24) | (buffer[at + 1] << 16)
                                     | (buffer[at + 2] << 8) | buffer[at + 3];
                            float v = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
                            // A NaN in the file stays a NaN: floating-point FITS has no BLANK card
                            // because NaN already is one.
                            physical = float.IsNaN(v) ? double.NaN : bzero + bscale * v;
                            break;
                        }
                    default: // -64
                        {
                            long bits = 0;
                            for (int b = 0; b < 8; b++) bits = (bits << 8) | buffer[at + b];
                            double v = BitConverter.Int64BitsToDouble(bits);
                            physical = double.IsNaN(v) ? double.NaN : bzero + bscale * v;
                            break;
                        }
                }

                values[i] = physical;
            }
        }

        private static int ReadFully(Stream stream, byte[] buffer, long count)
        {
            int total = 0;
            while (total < count)
            {
                int got = stream.Read(buffer, total, (int)Math.Min(count - total, int.MaxValue));
                if (got <= 0) break;
                total += got;
            }
            return total;
        }
    }
}

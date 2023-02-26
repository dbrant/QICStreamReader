using System;
using System.IO;

namespace QicUtils
{
    /// <summary>
    /// Decompression utility compatible with QIC-122 (rev B) compressed frames.
    /// https://www.qic.org/html/standards/12x.x/qic122b.pdf
    ///
    /// Copyright Dmitry Brant, 2020.
    /// </summary>
    public class Qic122Decompressor : Decompressor
    {
        private const int HISTORY_SIZE = 0x800;

        /// <summary>
        /// Decompress data from a compressed stream.
        /// </summary>
        /// <param name="stream">Stream containing a single compression frame that's compressed using the QIC-122 algorithm.</param>
        public Qic122Decompressor(Stream stream)
            : base(stream, HISTORY_SIZE)
        {
        }

        public void DecompressTo(Stream outStream)
        {
            int historySizeMask = HISTORY_SIZE - 1;

            int type, offset, length;
            byte b;

            while (true)
            {
                type = NextBit();
                if (type == 0)
                {
                    // raw byte
                    b = (byte)NextNumBits(8);
                    outStream.WriteByte(b);
                    history[historyPtr] = b;
                    historyPtr++;
                    historyPtr %= HISTORY_SIZE;
                }
                else
                {
                    // compressed bytes
                    offset = NextOffset();
                    if (offset == 0) { break; }

                    length = NextLength();

                    for (int i = 0; i < length; i++)
                    {
                        b = history[(historyPtr - offset) & historySizeMask];
                        outStream.WriteByte(b);
                        history[historyPtr] = b;
                        historyPtr++;
                        historyPtr %= HISTORY_SIZE;
                    }
                }
            }
        }

        private int NextOffset()
        {
            int type = NextBit();
            int offsetLen = type == 1 ? 7 : 11;
            return NextNumBits(offsetLen);
        }

        private int NextLength()
        {
            int length = 2;
            length += NextNumBits(2);
            if (length < 5) { return length; }
            length += NextNumBits(2);
            if (length < 8) { return length; }

            int chunk;
            while (true)
            {
                chunk = NextNumBits(4);
                length += chunk;
                if (chunk < 0xF) { break; }
            }
            return length;
        }
    }
}

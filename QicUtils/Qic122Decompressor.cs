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
    public class Qic122Decompressor
    {
        private Stream stream;

        private int curByte;
        private int curBitMask = 0;

        private byte[] history;
        private int historyPtr;

        private const int HISTORY_SIZE = 0x800;

        /// <summary>
        /// Decompress data from one stream to another.
        /// </summary>
        /// <param name="stream">Stream containing a single compression frame that's compressed using the QIC-122 algorithm.</param>
        /// <param name="outStream">Stream to which uncompressed data will be written.</param>
        public Qic122Decompressor(Stream stream, Stream outStream)
        {
            this.stream = stream;
            history = new byte[HISTORY_SIZE];
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

        private int NextBit()
        {
            if (curBitMask == 0)
            {
                curByte = stream.ReadByte();
                curBitMask = 0x80;
            }
            int ret = (curByte & curBitMask) != 0 ? 1 : 0;
            curBitMask >>= 1;
            return ret;
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

        private int NextNumBits(int bits)
        {
            int num = 0;
            for (int i = 0; i < bits; i++)
            {
                num <<= 1;
                num |= NextBit();
            }
            return num;
        }
    }
}

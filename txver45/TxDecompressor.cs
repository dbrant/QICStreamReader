using System;
using System.Collections.Generic;
using System.IO;

namespace txver45
{
    /// <summary>
    /// 
    /// Decompressor for tape images saved using the TXPLUS tool.
    /// Copyright Dmitry Brant, 2023.
    ///
    /// A few rough notes about the compression algorithm:
    ///
    /// * The algorithm is a sequence of "indices" and "instructions", one followed by another:
    ///   [index][instruction][index][instruction]...
    /// * Each index is 8 bits, and each instruction is 4 bits. The sequence of indices
    ///   and instructions is packed into bytes, so the next index and/or instruction does not
    ///   necessarily start on a byte boundary.
    /// * Very importantly, the indices and instructions are stored in LSB-order in each byte.
    ///   For example, take this sequence of bytes: 00 21 10
    ///   - The first index is 00 (the first byte), and the first instruction is 1, because "1"
    ///     is the low 4 bits of the second byte.
    ///   - The second index is 02, because "2" is the next 4 bits of the second byte, and "0"
    ///     is the low 4 bits of the third byte.
    ///   - The second instruction is 1, because "1" is the high 4 bits of the third byte.
    ///
    /// * Depending on the instruction, the index has a different meaning:
    ///   - Instruction 0: Take the "index" as a literal byte, to be output to the uncompressed
    ///     stream. Store this byte in a new dictionary entry.
    ///   - Instruction 1: Repeat byte(s), as dictated by the index:
    ///     - Index 0: Indicates the start of a compressed stream. If this occurs in the middle
    ///       of a compressed stream, indicates that the dictionary should be reset.
    ///     - Index 1: Indicates the end of the compressed stream.
    ///     - Index 2: Read the next byte, and then read that many more bytes literally from the
    ///       compressed to the uncompressed stream. For each of these bytes, store it in a new
    ///       dictionary entry.
    ///     - Index >= 3: Subtract 3 from the index, and get the true index into the current
    ///       dictionary. Repeat bytes from that dictionary entry, plus the first byte from the
    ///       next dictionary entry. Store this sequence in a new dictionary entry, and output
    ///       it to the uncompressed stream.
    ///   - Instruction >= 2:
    ///     - Same as Instruction 1, but no special cases for Index, and now the Index is treated
    ///       like this: take the Index, add (Instruction-1)*0x100 to it, then subtract 3.
    ///     - Perform the same repetition rule as written for Instruction 1 / Index >= 3.
    /// 
    /// </summary>
    internal class TxDecompressor
    {
        protected Stream stream;

        private int curByte;
        private int curBitMask = 0x100;

        private readonly List<byte>?[] history = new List<byte>?[0x10000];
        private int histPtr = 0;

        public TxDecompressor(Stream stream)
        {
            this.stream = stream;
        }

        public void DecompressTo(Stream outStream)
        {
            while (stream.Position < stream.Length)
            {
                int offset = NextNumBits(8);
                int instr = NextNumBits(4);

                if (instr == 0)
                {
                    // literal byte
                    outStream.WriteByte((byte)offset);
                    pushValue(offset);
                    continue;
                }

                offset += (instr - 1) * 0x100;

                if (offset == 0)
                {
                    // start of compressed data / clear dictionary
                    for (int i = 0; i < history.Length; i++)
                        history[i] = null;
                    histPtr = 0;
                }
                else if (offset == 1)
                {
                    // end of compressed data
                    break;
                }
                else if (offset == 2)
                {
                    // repeat the next number of literal bytes
                    int n;
                    if (Aligned())
                        n = NextNumBits(8);
                    else
                    {
                        n = NextNumBits(4);
                        // if offset is 0, then the actual number is the next byte
                        if (n == 0)
                            n = NextNumBits(8);
                    }
                    // if offset is 0, then the actual number is 0x100
                    if (n == 0)
                        n = 0x100;

                    for (int i = 0; i < n; i++)
                    {
                        byte a = (byte)stream.ReadByte();
                        pushValue(a);
                        outStream.WriteByte(a);
                    }
                }
                else
                {
                    // repeat past bytes
                    offset -= 3;

                    if (offset < 0 || history[offset] == null)
                    {
                        throw new ApplicationException("Invalid offset");
                    }
                    else
                    {
                        var list = history[offset]!;
                        var newList = new List<byte>();

                        newList.AddRange(list);

                        offset++;
                        if (offset < history.Length && (history[offset] != null))
                            newList.Add(history[offset]!.First());
                        else
                            newList.Add(list.First());

                        for (int i = 0; i < newList.Count; i++)
                            outStream.WriteByte(newList[i]);

                        pushValue(newList);
                    }
                }
            }
        }

        private void pushValue(int i)
        {
            pushValue(new List<byte> { (byte)i });
        }

        private void pushValue(List<byte> list)
        {
            history[histPtr] = list;
            histPtr++;
            if (histPtr >= history.Length) { histPtr = 0; }
        }

        private bool Aligned()
        {
            return curBitMask > 0x80;
        }

        private int NextBit()
        {
            if (curBitMask > 0x80)
            {
                curByte = stream.ReadByte();
                curBitMask = 0x1;
            }
            int ret = (curByte & curBitMask) != 0 ? 1 : 0;
            curBitMask <<= 1;
            return ret;
        }

        private int NextNumBits(int bits)
        {
            int num = 0;
            for (int i = 0; i < bits; i++)
            {
                num |= (NextBit() << i);
            }
            return num;
        }
    }
}

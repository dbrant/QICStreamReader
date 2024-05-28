using QicUtils;
using System;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Utility for decoding and uncompressing QIC-113 (rev G) formatted tape images.
/// This is specifically for transforming a compressed tape image into an uncompressed
/// image. The task of actually extracting the contents from the uncompressed image
/// is done using the other utilities in this toolkit.
/// 
/// https://www.qic.org/html/standards/11x.x/qic113g.pdf
/// 
/// One issue is that I have observed many different variations of this format in the wild,
/// with different widths of offsets and packing styles of compression frames within a segment.
/// This tool attempts to provide options for supporting all of these variations.
/// 
/// Copyright Dmitry Brant, 2020-.
/// 
/// </summary>
namespace qic113expand
{
    class Program
    {
        static void Main(string[] args)
        {
            string inFileName = "";
            string outFileName = "out.bin";

            long initialOffset = 0;
            int segSize = 0x7400;
            int absPosWidth = 8;
            int frameSizeWidth = 2;
            bool haveExtentOffset = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                else if (args[i] == "-o") { outFileName = args[i + 1]; }
                else if (args[i] == "--offset") { initialOffset = Utils.StringOrHexToLong(args[i + 1]); }
                else if (args[i] == "--segsize") { segSize = (int)Utils.StringOrHexToLong(args[i + 1]); }
                else if (args[i] == "--absposwidth") { absPosWidth = (int)Utils.StringOrHexToLong(args[i + 1]); }
                else if (args[i] == "--framesizewidth") { frameSizeWidth = (int)Utils.StringOrHexToLong(args[i + 1]); }
                else if (args[i] == "--haveextentoffset") { haveExtentOffset = true; }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage: qic113expand -f <file name> -o <out file name>");
                return;
            }

            byte[] bytes = new byte[0x10000];

            using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);
            stream.Position = initialOffset;

            Stream outStream = new FileStream(outFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            while (stream.Position < stream.Length)
            {
                // always align to segment boundary
                if ((stream.Position % 0x100) > 0)
                {
                    stream.Position += 0x100 - (int)(stream.Position % 0x100);
                }

                int segBytesLeft = segSize;

                if (haveExtentOffset)
                {
                    stream.Read(bytes, 0, 2);
                    segBytesLeft -= 2;
                    int extentOffset = BitConverter.ToUInt16(bytes, 0);
                    // TODO: correctly handle extent offsets (used in segment spanning compression extents)
                }

                long absolutePos;
                if (absPosWidth == 8) {
                    stream.Read(bytes, 0, 8);
                    segBytesLeft -= 8;
                    absolutePos = BitConverter.ToInt64(bytes, 0);
                } else if (absPosWidth == 4) {
                    stream.Read(bytes, 0, 4);
                    segBytesLeft -= 4;
                    absolutePos = BitConverter.ToUInt32(bytes, 0);
                } else {
                    throw new DecodeException("Absolute position width must be 4 or 8.");
                }

                while (segBytesLeft > 18)
                {
                    int frameSize;
                    if (frameSizeWidth == 2) {
                        stream.Read(bytes, 0, 2);
                        segBytesLeft -= 2;
                        frameSize = BitConverter.ToUInt16(bytes, 0);
                    } else if (frameSizeWidth == 4) {
                        stream.Read(bytes, 0, 4);
                        segBytesLeft -= 4;
                        frameSize = (int)BitConverter.ToUInt32(bytes, 0);
                    } else {
                        throw new DecodeException("Frame size width must be 2 or 4.");
                    }
                    
                    if (frameSize >= 0x10000)
                    {
                        throw new DecodeException("Frame size is unusually large: " + frameSize.ToString("X"));
                    }

                    bool compressed = (frameSize & 0x8000) == 0;
                    frameSize &= 0x7FFF;


                    if (frameSize < 0x4000 || frameSize > 0x7800)
                    {
                        Console.WriteLine("Warning: frame size is unusual: " + frameSize.ToString("X"));
                        frameSize = 0x73EF;
                    }


                    if (frameSize > segBytesLeft)
                    {
                        Console.WriteLine("Warning: frame extends beyond segment boundary.");
                    }

                    stream.Read(bytes, 0, frameSize);
                    segBytesLeft -= frameSize;

                    if (frameSize == 0)
                    {
                        Console.WriteLine("Warning: skipping empty frame.");
                        break;
                    }

                    Console.WriteLine("input: " + stream.Position.ToString("X") + ", frameSize: " + frameSize.ToString("X")
                        + ", absPos: " + absolutePos.ToString("X") + ", outputPos: " + outStream.Position.ToString("X"));

                    if (absolutePos < outStream.Position)
                    {
                        Console.WriteLine("Warning: frame position out of sync with output. Starting new stream.");
                        outFileName += "_";
                        outStream = new FileStream(outFileName, FileMode.OpenOrCreate, FileAccess.Write);
                    }

                    if (absolutePos > 0x100000000000)
                    {
                        throw new DecodeException("Absolute position a bit too large: " + absolutePos.ToString("X"));
                    }

                    if (absolutePos > 0 && absolutePos !=  outStream.Position)
                    {
                        Console.WriteLine("Warning: absolute position according to frame is greater than output. Possibly missing data.");
                        outStream.Position = absolutePos;
                    }

                    if (compressed)
                    {
                        try
                        {
                            new Qic122Decompressor(new MemoryStream(bytes)).DecompressTo(outStream);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("Warning: failed to decompress frame: " + ex.Message);
                        }
                    }
                    else
                    {
                        outStream.Write(bytes, 0, frameSize);
                    }
                    absolutePos = outStream.Position;
                    
                    //if ((segBytesLeft - 0x400) >= 0 && (segBytesLeft - 0x400) < 18)
                    {
                        break;
                    }
                }
            }
        }
    }
}
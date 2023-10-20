using System;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Utility for processing tape images that were acquired in "raw" mode from
/// a tape drive, and thus still have ECC data appended to each segment.
/// 
/// Copyright Dmitry Brant, 2020-.
/// 
/// </summary>
namespace deecc
{
    class Program
    {
        static void Main(string[] args)
        {
            string inFileName = "";
            string outFileName = "out.bin";

            long initialOffset = 0;
            int segSize = 0x8000;
            int eccSize = 0x400;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                else if (args[i] == "-o") { outFileName = args[i + 1]; }
                else if (args[i] == "--offset") { initialOffset = QicUtils.Utils.StringOrHexToLong(args[i + 1]); }
                else if (args[i] == "--segsize") { segSize = (int)QicUtils.Utils.StringOrHexToLong(args[i + 1]); }
                else if (args[i] == "--eccsize") { eccSize = (int)QicUtils.Utils.StringOrHexToLong(args[i + 1]); }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage: deecc -f <file name> -o <out file name>");
                return;
            }

            byte[] bytes = new byte[segSize];

            using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);
            stream.Position = initialOffset;

            using var oStream = new FileStream(outFileName, FileMode.Create, FileAccess.Write);

            while (stream.Position < stream.Length)
            {
                stream.Read(bytes, 0, segSize);

                // Each block of [segSize] bytes ends with [eccSize] bytes of ECC data.
                // TODO: actually use the ECC to verify and correct the data itself.
                oStream.Write(bytes, 0, segSize - eccSize);
            }
        }
    }
}
using System;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Decoder for QIC-113 (rev G) formatted tape images, made specifically with
/// the Windows 95 Backup utility.
/// https://www.qic.org/html/standards/11x.x/qic113g.pdf
/// 
/// Copyright Dmitry Brant, 2020.
/// 
/// TODO: combine with `qicstreamv1`.
/// 
/// </summary>
namespace qicstream1a
{
    class Program
    {
        private const uint FileHeaderMagic = 0x33CC33CC;
        private const uint DataHeaderMagic = 0x66996699;
        private const int SEG_SIZE = 0x7400;

        static void Main(string[] args)
        {
            string inFileName = "";
            string outFileName = "out.bin";
            string baseDirectory = "out";

            long initialOffset = 0;
            bool removeEcc = false;
            bool decompress = false;
            bool absPos = false;
            bool catDump = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                else if (args[i] == "-o") { outFileName = args[i + 1]; }
                else if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                else if (args[i] == "-ecc") { removeEcc = true; }
                else if (args[i] == "-x") { decompress = true; }
                else if (args[i] == "--offset") { initialOffset = QicUtils.Utils.StringOrHexToLong(args[i + 1]); }
                else if (args[i] == "--abspos") { absPos = true; }
                else if (args[i] == "--catdump") { catDump = true; }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("Phase 1 - remove/apply ECC data (if present):");
                Console.WriteLine("Usage: qicstreamv1 -ecc -f <file name> -o <out file name>");
                Console.WriteLine("Phase 2 - uncompress the archive (if compression is used):");
                Console.WriteLine("Usage: qicstreamv1 -x -f <file name> -o <out file name>");
                Console.WriteLine("Phase 3 - extract files/folders from archive:");
                Console.WriteLine("Usage: qicstreamv1 -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[0x10000];

            if (removeEcc)
            {
                try
                {
                    using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);
                    using var outStream = new FileStream(outFileName, FileMode.Create, FileAccess.Write);
                    while (stream.Position < stream.Length)
                    {
                        stream.Read(bytes, 0, 0x8000);

                        // Each block of 0x8000 bytes ends with 0x400 bytes of ECC and/or parity data.
                        // TODO: actually use the ECC to verify and correct the data itself.
                        outStream.Write(bytes, 0, 0x8000 - 0x400);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error: " + e.Message);
                }
                return;
            }
            else if (decompress)
            {
                try
                {
                    using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                    {
                        Stream outStream = new FileStream(outFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                        {
                            while (stream.Position < stream.Length)
                            {
                                // always align to segment boundary
                                if ((stream.Position % 0x100) > 0)
                                {
                                    stream.Position += 0x100 - (int)(stream.Position % 0x100);
                                }


                                int segBytesLeft = SEG_SIZE;

                                stream.Read(bytes, 0, 8);
                                segBytesLeft -= 8;
                                uint absolutePos = BitConverter.ToUInt32(bytes, 0);


                                while (segBytesLeft > 18)
                                {

                                    stream.Read(bytes, 0, 2);
                                    segBytesLeft -= 2;
                                    int frameSize = BitConverter.ToUInt16(bytes, 0);

                                    bool compressed = (frameSize & 0x8000) == 0;
                                    frameSize &= 0x7FFF;

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

                                    if (absolutePos > 0x10000000)
                                    {
                                        Console.WriteLine(">>> Absolute position a bit too large...");
                                        break;
                                    }

                                    if (absPos && (absolutePos != outStream.Position))
                                    {
                                        Console.WriteLine(">>> adjusting position!");
                                        outStream.Position = absolutePos;
                                    }

                                    if (compressed)
                                    {
                                        try
                                        {
                                            new QicUtils.Qic122Decompressor(new MemoryStream(bytes)).DecompressTo(outStream);
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
                                    absolutePos = (uint)outStream.Position;


                                    if ((segBytesLeft - 0x400) >= 0 && (segBytesLeft - 0x400) < 18)
                                    {
                                        break;
                                    }

                                }

                            }





                            /*
                            bool firstCompressedFrame = true;

                            while (stream.Position < stream.Length)
                            {
                                stream.Read(bytes, 0, 10);
                                long absolutePos = BitConverter.ToInt64(bytes, 0);
                                int frameSize = BitConverter.ToUInt16(bytes, 8);

                                bool compressed = (frameSize & 0x8000) == 0;
                                frameSize &= 0x7FFF;

                                if (compressed && firstCompressedFrame)
                                {
                                    firstCompressedFrame = false;
                                    // pad to 0x200
                                    if ((outStream.Position % 0x200) > 0)
                                    {
                                        Array.Clear(bytes, 0, bytes.Length);
                                        outStream.Write(bytes, 0, 0x200 - (int)(outStream.Position % 0x200));
                                    }
                                }

                                stream.Read(bytes, 0, frameSize);

                                if ((stream.Position % 0x200) > 0)
                                {
                                    stream.Seek(0x200 - (stream.Position % 0x200), SeekOrigin.Current);
                                }


                                if (absPos && (absolutePos != outStream.Position))
                                {
                                    outStream.Position = absolutePos;
                                }

                                if (compressed)
                                {
                                    try
                                    {
                                        new QicUtils.Qic122Decompressor(new MemoryStream(bytes), outStream);
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
                            }
                            */
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error: " + e.Message);
                }
                return;
            }

            try
            {
                using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                {
                    while (stream.Position < stream.Length)
                    {
                        long posBeforeHeader = stream.Position;

                        long headerPos = 0;
                        long dataPos = 0;
                        long nextHeaderPos = 0;

                        while (true)
                        {
                            stream.Read(bytes, 0, 4);
                            if (BitConverter.ToUInt32(bytes, 0) == FileHeaderMagic)
                            {
                                headerPos = stream.Position - 4;
                                break;
                            }
                            stream.Seek(-3, SeekOrigin.Current);
                        }

                        while (true)
                        {
                            stream.Read(bytes, 0, 4);
                            if (BitConverter.ToUInt32(bytes, 0) == DataHeaderMagic)
                            {
                                dataPos = stream.Position - 4;
                                stream.Position = headerPos + 4;
                                break;
                            }
                            stream.Seek(-3, SeekOrigin.Current);
                        }


                        while (true)
                        {
                            stream.Read(bytes, 0, 4);
                            if (BitConverter.ToUInt32(bytes, 0) == FileHeaderMagic)
                            {
                                nextHeaderPos = stream.Position - 4;
                                break;
                            }

                            if (stream.Position >= stream.Length)
                            {
                                Console.WriteLine("Warning: reached end of file.");
                                nextHeaderPos = stream.Position;
                                break;
                            }

                            stream.Seek(-3, SeekOrigin.Current);
                        }


                        if (nextHeaderPos < dataPos)
                        {
                            // zero-length file? skip it.
                            stream.Position = nextHeaderPos;
                            continue;
                        }


                        stream.Position = headerPos;

                        var header = new FileHeader(stream, false, dataPos, nextHeaderPos);
                        if (!header.Valid)
                        {
                            stream.Position = posBeforeHeader + 1;
                            continue;
                        }

                        stream.Position = dataPos + 6;



                        if (header.IsDirectory || header.Name.Trim() == "")
                        {
                            if (header.Size > 0)
                            {
                                stream.Seek(header.Size, SeekOrigin.Current);
                            }
                            continue;
                        }

                        string filePath = baseDirectory;
                        if (header.Subdirectory.Length > 0)
                        {
                            string[] dirArray = header.Subdirectory.Split('\0');
                            for (int i = 0; i < dirArray.Length; i++)
                            {
                                filePath = Path.Combine(filePath, dirArray[i]);
                            }
                        }

                        Directory.CreateDirectory(filePath);
                        filePath = Path.Combine(filePath, header.Name);

                        while (File.Exists(filePath))
                        {
                            Console.WriteLine("Warning: file already exists (amending name): " + filePath);
                            filePath += "_";
                        }

                        Console.WriteLine(stream.Position.ToString("X") + ": " + filePath + " - "
                            + header.Size.ToString() + " bytes - " + header.DateTime.ToShortDateString());

                        using (var f = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                        {
                            long bytesLeft = header.Size;
                            while (bytesLeft > 0)
                            {
                                int bytesToRead = bytes.Length;
                                if (bytesToRead > bytesLeft) { bytesToRead = (int)bytesLeft; }
                                stream.Read(bytes, 0, bytesToRead);
                                f.Write(bytes, 0, bytesToRead);

                                if (bytesLeft == header.Size)
                                {
                                    if (!QicUtils.Utils.VerifyFileFormat(header.Name, bytes))
                                    {
                                        Console.WriteLine(stream.Position.ToString("X") + " -- Warning: file format doesn't match: " + filePath);
                                        Console.ReadKey();
                                    }
                                }

                                bytesLeft -= bytesToRead;
                            }
                        }

                        try
                        {
                            File.SetCreationTime(filePath, header.DateTime);
                            File.SetLastWriteTime(filePath, header.DateTime);
                            File.SetAttributes(filePath, header.Attributes);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
        }

        private class FileHeader
        {
            public long Size { get; }
            public string Name { get; }
            public string DosName { get; }
            public DateTime DateTime { get; }
            public FileAttributes Attributes { get; }
            public string Subdirectory { get; }
            public bool Valid { get; }
            public bool IsDirectory { get; }
            public bool IsLastEntry { get; }
            public bool IsFinalEntry { get; }

            public FileHeader(Stream stream, bool isCatalog, long dataPos, long nextHeaderPos)
            {
                Subdirectory = "";
                byte[] bytes = new byte[1024];
                long initialPos = stream.Position;

                if (!isCatalog)
                {
                    stream.Read(bytes, 0, 4);
                    if (BitConverter.ToInt32(bytes, 0) != FileHeaderMagic) { return; }
                }

                stream.Read(bytes, 0, 0x45);

                DateTime = new DateTime(1970, 1, 1).AddSeconds(BitConverter.ToUInt32(bytes, 0x2D));

                stream.Read(bytes, 0, 2);
                int nameLength = BitConverter.ToUInt16(bytes, 0);
                if (nameLength > 0x100)
                {
                    return;
                }
                if (nameLength > 0)
                {
                    stream.Read(bytes, 0, nameLength);
                    Name = Encoding.Unicode.GetString(bytes, 0, nameLength);
                }
                else
                {
                    Name = "";
                }

                stream.Read(bytes, 0, 0x15);

                stream.Read(bytes, 0, 2);
                nameLength = BitConverter.ToUInt16(bytes, 0);
                if (nameLength > 0)
                {
                    stream.Read(bytes, 0, nameLength);
                    DosName = Encoding.Unicode.GetString(bytes, 0, nameLength);
                }
                else
                {
                    DosName = "";
                }


                int dirLen = (int)(dataPos - stream.Position);

                stream.Read(bytes, 0, dirLen);

                Subdirectory = Encoding.Unicode.GetString(bytes, 2, dirLen - 2);

                Size = nextHeaderPos - dataPos - 6;


                Valid = true;
            }
        }

    }
}
using System;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Decoder for QIC-113 (rev G) formatted tape images, made specifically with
/// the Arcada Backup utility.
/// https://www.qic.org/html/standards/11x.x/qic113g.pdf
/// 
/// Copyright Dmitry Brant, 2021.
/// 
/// TODO: combine with `qicstreamv1`.
/// 
/// </summary>
namespace arcadabackup1
{
    class Program
    {
        private const uint FileHeaderMagic = 0x33CC33CC;
        private const uint DataHeaderMagic = 0x66996699;

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
                else if (args[i] == "--offset") { initialOffset = Convert.ToInt64(args[i + 1]); }
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
                    using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                    {
                        using (var outStream = new FileStream(outFileName, FileMode.Create, FileAccess.Write))
                        {
                            while (stream.Position < stream.Length)
                            {
                                stream.Read(bytes, 0, 0x8000);

                                // Each block of 0x8000 bytes ends with 0x400 bytes of ECC and/or parity data.
                                // TODO: actually use the ECC to verify and correct the data itself.
                                outStream.Write(bytes, 0, 0x8000 - 0x400);
                            }
                        }
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
                        using (var outStream = new FileStream(outFileName, FileMode.Create, FileAccess.Write))
                        {
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
                    while (stream.Position < (stream.Length - 16))
                    {
                        long headerPos = 0;
                        long dataPos = 0;

                        while (stream.Position < (stream.Length - 16))
                        {
                            stream.Read(bytes, 0, 4);
                            if (BitConverter.ToUInt32(bytes, 0) == FileHeaderMagic)
                            {
                                headerPos = stream.Position - 4;
                                break;
                            }
                            stream.Seek(-3, SeekOrigin.Current);
                        }

                        while (stream.Position < (stream.Length - 16))
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

                        if (stream.Position >= (stream.Length - 16))
                        {
                            break;
                        }


                        stream.Position = headerPos;

                        var header = new FileHeader(stream, false, dataPos);
                        if (!header.Valid)
                        {
                            stream.Position = headerPos + 1;
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

                        // Make sure the fully qualified name does not exceed 260 chars
                        if (filePath.Length >= 260)
                        {
                            filePath = filePath.Substring(0, 259);
                        }

                        while (File.Exists(filePath))
                        {
                            Console.WriteLine("Warning: file already exists (amending name): " + filePath);
                            filePath += "_";
                        }

                        Console.WriteLine(stream.Position.ToString("X") + ": " + filePath + " - " + header.Size.ToString() + " bytes - " + header.DateTime.ToShortDateString());

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
                                        //Console.ReadKey();
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
                Console.Write(e.StackTrace);
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

            public FileHeader(Stream stream, bool isCatalog, long dataPos)
            {
                Subdirectory = "";
                byte[] bytes = new byte[1024];
                long initialPos = stream.Position;

                if (!isCatalog)
                {
                    stream.Read(bytes, 0, 4);
                    if (BitConverter.ToInt32(bytes, 0) != FileHeaderMagic) { return; }
                }

                //stream.Read(bytes, 0, 0x46);
                stream.Read(bytes, 0, 0x45);

                Size = BitConverter.ToUInt32(bytes, 0x11);

                //DateTime = new DateTime(1970, 1, 1).AddSeconds(BitConverter.ToUInt32(bytes, 0x2D));
                DateTime = QicUtils.Utils.DateTimeFromTimeT(BitConverter.ToUInt32(bytes, 0x3D));

                stream.Read(bytes, 0, 2);
                int nameLength = BitConverter.ToUInt16(bytes, 0);
                if (nameLength > 0)
                {
                    stream.Read(bytes, 0, nameLength);
                    Name = Encoding.Unicode.GetString(bytes, 0, nameLength);
                }
                else
                {
                    Name = "";
                }

                if (Name.Length > 100)
                {
                    Console.WriteLine("Warning: trimming very long name: " + Name);
                    Name = Name.Substring(0, 100);
                }

                stream.Read(bytes, 0, 0x15);

                stream.Read(bytes, 0, 2);
                nameLength = BitConverter.ToUInt16(bytes, 0);

                if (nameLength > 255)
                {
                    Console.WriteLine("Warning: suspicious name length (" + nameLength + "). Skipping...");
                    return;
                }

                if (nameLength > 0)
                {
                    stream.Read(bytes, 0, nameLength);
                    DosName = Encoding.Unicode.GetString(bytes, 0, nameLength);
                }
                else
                {
                    DosName = "";
                }


                if (Size == 0)
                {
                    Console.WriteLine("Warning: skipping zero-length file: " + Name);
                    return;
                }

                int dirLen = (int)(dataPos - stream.Position);

                stream.Read(bytes, 0, dirLen);

                Subdirectory = Encoding.Unicode.GetString(bytes, 2, dirLen - 2);

                Valid = true;
            }
        }

    }
}

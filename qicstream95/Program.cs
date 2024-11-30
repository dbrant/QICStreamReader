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
namespace qicstream95
{
    class Program
    {
        private const uint FileHeaderMagic = 0x33CC33CC;
        private const uint DataHeaderMagic = 0x66996699;

        static void Main(string[] args)
        {
            string inFileName = "";
            string baseDirectory = "out";
            long initialOffset = 0;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                else if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                else if (args[i] == "--offset") { initialOffset = QicUtils.Utils.StringOrHexToLong(args[i + 1]); }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage: qicstream95 -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[0x10000];

            using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);
            stream.Position = initialOffset;

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

                try {
                    Directory.CreateDirectory(filePath);
                } catch {
                    Console.WriteLine("Warning: Directory with same name as file: " + filePath);
                    filePath += "_";
                    Directory.CreateDirectory(filePath);
                }
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
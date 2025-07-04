using QicUtils;
using System;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Decoder for an unknown backup format found on some QIC-150 cartridges.
/// 
/// Copyright Dmitry Brant, 2025-
/// 
/// 
/// Brief outline of the format, to the best of my reverse-engineering ability:
/// 
/// * Every file and directory record is aligned to a block boundary (512 bytes).
/// * Little-endian.
/// * Here is a high-level organization of the stream:
/// 
/// [block 1] Volume header?
/// [block 2..n] Catalog contents
/// [block n...] File contents with headers
/// 
/// For each file:
/// Every file starts on a block boundary and has a header of 0x59 bytes, after which the file contents follow.
/// 
/// The header contains the following fields:
/// offset    | size | description
/// 0         | 2    | magic (0x55AA)
/// 2         | 2    | unknown
/// 4         | var. | file name (null-terminated ASCII string, up to 0x51 bytes)
/// 0x55      | 4    | file size (little-endian)
/// 0x59      | ...  | file contents
/// 
/// </summary>
namespace unknownqic1
{
    class Program
    {
        private const int BLOCK_SIZE = 0x200;

        static void Main(string[] args)
        {
            string inFileName = "";
            string baseDirectory = "out";

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                if (args[i] == "-d") { baseDirectory = args[i + 1]; }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage: unknownqic1 -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[65536];

            using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);
            // Read the volume header
            stream.Read(bytes, 0, BLOCK_SIZE);

            string volName = Utils.GetNullTerminatedString(Encoding.ASCII.GetString(bytes, 0xB, 0x40));
            Console.WriteLine("Backup label: " + volName);

            Directory.CreateDirectory(baseDirectory);
            string currentDirectory = baseDirectory;

            // And now begins the main sequence of the backup, which consists of a file or directory header,
            // and the contents of the file.
            // Each new file/directory header is aligned on a block boundary (512 bytes).

            while (stream.Position < stream.Length)
            {
                AlignToNextBlock(stream);

                FileHeader header = new(stream);
                if (!header.Valid)
                    continue;

                string fileName = Path.Combine(currentDirectory, header.Name);
                if (File.Exists(fileName))
                {
                    Console.WriteLine("Warning: file exists: " + header.Name);
                    Console.ReadKey();
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fileName));

                using (var f = new FileStream(fileName, FileMode.Create, FileAccess.Write))
                {
                    long bytesLeft = header.Size;
                    while (bytesLeft > 0)
                    {
                        int bytesToRead = bytes.Length;
                        if (bytesToRead > bytesLeft) { bytesToRead = (int)bytesLeft; }
                        if (bytesToRead > (stream.Length - stream.Position))
                        {
                            bytesToRead = (int)(stream.Length - stream.Position);
                            if (bytesToRead == 0)
                            {
                                // reached the end of the tape image, but still need data. This implies
                                // that the file continues on another tape.
                                Console.WriteLine("Warning: file contents truncated. Probably continues on another tape.");
                                break;
                            }
                        }
                        int bytesRead = stream.Read(bytes, 0, bytesToRead);
                        f.Write(bytes, 0, bytesToRead);

                        if (bytesLeft == header.Size)
                        {
                            if (!Utils.VerifyFileFormat(header.Name, bytes))
                            {
                                Console.WriteLine(stream.Position.ToString("X") + " -- Warning: file format doesn't match: " + fileName);
                                Console.ReadKey();
                            }
                        }

                        bytesLeft -= bytesToRead;
                    }
                }
                //File.SetCreationTime(fileName, header.DateTime);
                //File.SetLastWriteTime(fileName, header.DateTime);
                //File.SetAttributes(fileName, header.Attributes);

                Console.WriteLine(stream.Position.ToString("X") + ": " + fileName + ", " + header.Size.ToString() + " bytes - " + header.DateTime.ToShortDateString());
            }
        }

        static void AlignToNextBlock(Stream stream)
        {
            long align = stream.Position % BLOCK_SIZE;
            if (align > 0) { stream.Seek(BLOCK_SIZE - align, SeekOrigin.Current); }
        }

        private class FileHeader
        {
            public long Size { get; }
            public string Name { get; }
            public DateTime DateTime { get; }
            public FileAttributes Attributes { get; }
            public bool IsDirectory { get; }
            public bool Valid { get; }

            public FileHeader(Stream stream)
            {
                byte[] bytes = new byte[0x59];
                stream.Read(bytes, 0, 0x59);

                if (bytes[0] != 0x55 && bytes[1] != 0xAA)
                    return;

                Name = Utils.GetNullTerminatedString(Encoding.ASCII.GetString(bytes, 0x4, 0x40));
                if (Name.StartsWith("\\"))
                    Name = Name.Substring(1);

                Size = Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0x55));

                Valid = true;
            }
        }
    }
}

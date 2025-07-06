using QicUtils;
using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// 
/// Decoder for Mountain FileSafe backups found on some QIC-150 cartridges.
/// 
/// Copyright Dmitry Brant, 2025-
/// 
/// 
/// Brief outline of the format, to the best of my reverse-engineering ability:
/// 
/// The backup starts with a volume header, then a catalog of files and directories, and then the actual file contents.
/// The volume header is one block (512 bytes).
/// The catalog is a sequence of records, each 0x20 bytes long, which describe files and directories. The catalog
/// contains the file name, attributes, size, and date/time, and therefore must be decoded before dumping the files.
/// Immediately after the catalog, the actual file contents follow, each file starting on a block boundary.
/// (Or, if it's a continuation of backup from another tape, the last file's contents start immediately.)
/// 
/// Here are some important bits in the volume header (first 512 bytes):
/// offset    | size | description
/// ----------|------|---------------------------------------------------
/// 0         | 2    | version number (I have observed 0x4 so far)
/// 2         | 2    | magic (0xAA55)
/// 4         | 2    | unknown (observed 0x5 so far)
/// 6         | 2    | Number of this tape in the total backup set (1-based).
/// 0xB       | var  | volume name (null-terminated ASCII string)
/// 
/// 
/// 
/// 
/// 
/// For each file:
/// Every file starts on a block boundary and has a header of 0x59 bytes, after which the file contents follow.
/// 
/// The header contains the following fields:
/// offset    | size | description
/// ----------|------|---------------------------------------------------
/// 0         | 2    | magic (0x55AA)
/// 2         | 2    | unknown
/// 4         | var. | file name (null-terminated ASCII string, up to 0x51 bytes)
/// 0x55      | 4    | file size (little-endian)
/// 0x59      | ...  | file contents
/// 
/// </summary>
namespace mountainqic
{
    class Program
    {
        private const int BLOCK_SIZE = 0x200;

        private enum FormatVersion
        {
            Ver4, Ver4b, Ver5
        }

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
                Console.WriteLine("Usage: mountain -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[65536];

            using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);
            // Read the volume header
            stream.Read(bytes, 0, BLOCK_SIZE);

            var version = FormatVersion.Ver4;

            if (bytes[0] == 0x4 && bytes[2] == 0xAA && bytes[3] == 0x55)
            {
                version = FormatVersion.Ver4;
                if (bytes[0xE0] > 0)
                    version = FormatVersion.Ver4b;
            }
            else if (bytes[0] == 0x55 && bytes[1] == 0xAA && bytes[3] == 0x5)
            {
                version = FormatVersion.Ver5;
            }
            else
            {
                Console.WriteLine("Warning: does not appear to be a valid Mountain FileSafe backup file.");
            }

            string volName = Utils.GetNullTerminatedString(Encoding.Latin1.GetString(bytes, version == FormatVersion.Ver5 ? 0x88 : 0xB, 0x40));
            Console.WriteLine("Backup label: " + volName);

            Directory.CreateDirectory(baseDirectory);
            string currentDirectory = baseDirectory;

            var catalog = new Dictionary<string, CatalogEntry>();
            string currentCatalogDir = "";
            var catalogNumPaddingEntries = 0;

            // Read the catalog
            while (stream.Position < stream.Length)
            {
                stream.Read(bytes, 0, 0x20);
                if (bytes[0] == 0xFF && bytes[1] == 0xFF)
                {
                    catalogNumPaddingEntries++;
                    if (catalogNumPaddingEntries > 1)
                    {
                        // End of catalog!
                        break;
                    }
                    continue;
                }
                catalogNumPaddingEntries = 0;

                if (bytes[0] == 0x55 && bytes[1] == 0xAA)
                {
                    Console.WriteLine("Warning: looks like file contents starting before end of catalog.");
                    stream.Seek(-0x20, SeekOrigin.Current);
                    break;
                }

                if (bytes[0] == 0x5C)
                {
                    // new catalog directory
                    currentCatalogDir = Utils.GetNullTerminatedString(Encoding.Latin1.GetString(bytes, 0, 0x20)).Trim();
                }
                else
                {
                    var entry = new CatalogEntry(bytes);
                    if (!entry.Valid)
                    {
                        Console.WriteLine("Warning: malformed catalog entry. Assuming beginning of file data.");
                        stream.Seek(-0x20, SeekOrigin.Current);
                    }
                    else
                    {
                        var catName = currentCatalogDir + "\\" + entry.Name;
                        while (catName.StartsWith("\\"))
                            catName = catName.Substring(1);
                        if (!entry.IsDirectory)
                        {
                            catalog[catName] = entry;
                        }
                        continue;
                    }
                }
            }


            // Read the file contents
            while (stream.Position < stream.Length)
            {
                AlignToNextBlock(stream);

                FileHeader header = new(stream, version);
                if (!header.Valid)
                    continue;

                catalog.Remove(header.Name, out CatalogEntry catalogEntry);
                if (catalogEntry == null)
                {
                    Console.WriteLine("Warning: file not found in catalog: " + header.Name);
                }
                else if (catalogEntry.Size != header.Size)
                {
                    Console.WriteLine("Warning: file size mismatch for " + header.Name + ": catalog says " + catalogEntry.Size + ", but header says " + header.Size);
                    if (header.Size == 0 && catalogEntry.Size > 0)
                        header.Size = catalogEntry.Size;
                }

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
                if (catalogEntry != null && !catalogEntry.IsDirectory)
                {
                    try
                    {
                        File.SetCreationTime(fileName, catalogEntry.DateTime);
                        File.SetLastWriteTime(fileName, catalogEntry.DateTime);
                        File.SetAttributes(fileName, catalogEntry.Attributes);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Warning: could not set file attributes for " + fileName + ": " + ex.Message);
                    }
                }

                Console.WriteLine(stream.Position.ToString("X") + ": " + fileName + ", " + header.Size.ToString() + " bytes - " + (catalogEntry?.DateTime ?? header.DateTime).ToShortDateString());
            }

            if (catalog.Count > 0)
            {
                Console.WriteLine("Warning: the following files were in the catalog, but not found in archive:");
                foreach (var entry in catalog)
                    Console.WriteLine(" - " + entry.Key);
            }
            else
            {
                Console.WriteLine("All files found in the catalog.");
            }
        }

        static void AlignToNextBlock(Stream stream)
        {
            long align = stream.Position % BLOCK_SIZE;
            if (align > 0) { stream.Seek(BLOCK_SIZE - align, SeekOrigin.Current); }
        }

        private class CatalogEntry
        {
            public string Name { get; }
            public bool IsDirectory { get { return (Attributes & FileAttributes.Directory) != 0; } }
            public DateTime DateTime { get; }
            public FileAttributes Attributes { get; }
            public long Size { get; }
            public bool Valid { get; }
            public int Sequence { get; }

            public CatalogEntry(byte[] bytes)
            {
                if (bytes[0] < 0x20 || bytes[0] == 0xFF)
                    return;

                Name = Utils.GetNullTerminatedString(Encoding.Latin1.GetString(bytes, 0, 8)).Trim();
                string Extension = Utils.GetNullTerminatedString(Encoding.Latin1.GetString(bytes, 8, 3)).Trim();
                if (Extension.Length > 0)
                    Name += "." + Extension;

                Attributes = (FileAttributes)bytes[0xB];
                DateTime = Utils.GetDosDateTime(BitConverter.ToUInt16(bytes, 0x18), BitConverter.ToUInt16(bytes, 0x16));
                Sequence = Utils.LittleEndian(BitConverter.ToUInt16(bytes, 0x1A));
                Size = Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0x1C));
                Valid = true;
            }
        }

        private class FileHeader
        {
            public long Size { get; set; }
            public string Name { get; }
            public DateTime DateTime { get; }
            public FileAttributes Attributes { get; }
            public bool IsDirectory { get; }
            public bool Valid { get; }

            public FileHeader(Stream stream, FormatVersion version)
            {
                byte[] bytes = new byte[0x100];

                stream.Read(bytes, 0, 0x10);
                if (bytes[0] != 0x55 || bytes[1] != 0xAA)
                    return;

                stream.Seek(-0x10, SeekOrigin.Current);

                if (version == FormatVersion.Ver5)
                {
                    var headerLen = bytes[0xF];
                    stream.Read(bytes, 0, headerLen);

                    Size = Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0xB));

                    var nameLen = bytes[0x17];
                    Name = Utils.GetNullTerminatedString(Encoding.Latin1.GetString(bytes, 0x19, nameLen));
                    if (Name.StartsWith("\\"))
                        Name = Name.Substring(1);
                }
                else if (version == FormatVersion.Ver4 || version == FormatVersion.Ver4b)
                {
                    stream.Read(bytes, 0, version == FormatVersion.Ver4b ? 0xB9 : 0x59);

                    Name = Utils.GetNullTerminatedString(Encoding.Latin1.GetString(bytes, 0x4, 0x40));
                    if (Name.StartsWith("\\"))
                        Name = Name.Substring(1);

                    Size = Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0x55));
                }
                Valid = true;
            }
        }
    }
}

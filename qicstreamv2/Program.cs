using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Decoder for QICStream (V2?) formatted tape images.
/// 
/// Copyright Dmitry Brant, 2019
/// 
/// 
/// Brief outline of the format, to the best of my reverse-engineering ability:
/// 
/// * Every block of 0x8000 bytes ends with 0x402 bytes of extra data (for parity checking or
///   some other kind of error correction?). In other words, for every 0x8000 bytes, only the
///   first 0x7BFE bytes are useful data.
/// * Little-endian.
/// * At a high level, the archive basically consists of file and directory records in succession.
///   Catalog information usually appears at the end of the archive, although catalog data may also
///   be present in random places in the middle.
/// 
/// The structure of the archive consists of a volume header, followed by structures that are
/// delimited by "control codes". For example, a control code might indicate that the bytes that
/// follow are a file.
/// It basically looks like this:
/// 
/// [Volume header]
/// [Control code]
/// [Data specific to the above control code]
/// [Control code]
/// [Data specific to the above control code]
/// ....
/// 
/// Here are the possible control codes:
/// 
/// 1 - Start of contents area (no additional data after this code).
/// 2 - Start of catalog area (no additional data after this code).
/// 3 - Go up to the parent directory (i.e. the current directory stack should pop the
///     last subdirectory) (no additional data).
/// 5 - File. This code is followed by a file header, which should then be followed by one
///     or more data chunks.
/// 6 - Directory. This code is followed by a file header which will contain the name of this
///     directory. When a new directory is reached, it becomes the "current" directory, and all
///     subsequent files should be placed into it.  If there is already a "current" directory,
///     then this directory should become a subdirectory. In other words, a "stack" of directories
///     is built, with each directory entry pushed onto the top of the stack.
/// 9 - Data chunk. This code is followed by a 16-bit "length" field which specifies the size of
///     the data that follows it, and then the actual data that applies to the current file
///     that is being read.  If a file is smaller than 0x10000 bytes, it should have just one
///     data chunk. But if it's larger, then it will have multiple consecutive chunks.
/// 
/// Note: the space between two control codes may sometimes be padded with a 0 byte, possibly
/// for alignment on a 16-bit boundary.
/// 
/// 
/// == Volume Header structure ==
/// 
/// Byte offset (hex)     Meaning
/// ----------------------------------------
/// 4                     Magic characters "FSET".
/// 
/// 3C-3D                 Length of volume label.
/// 3E-[variable]         Volume label.
/// 
/// ...after the volume label:
/// 0-1                   Length of drive name.
/// 2-[variable]          Drive name.
/// 
/// ...after the drive name:
/// 0-5                   Padding bytes.
/// --------------------------------------------------------------
/// 
/// 
/// == File Header structure ==
/// 
/// Here is a breakdown of the file header:
/// 
/// Byte offset (hex)     Meaning
/// ----------------------------------------
/// 0                     Always seems to be 1, but does not appear to be important.
/// 
/// 1-2                   Size of this header, NOT INCLUDING these first three bytes.
/// 
/// 3-4                   File attributes (the usual DOS attribute bits).
/// 
/// 7-A                   File date, encoded as time_t (seconds since 1970 epoch).
/// 
/// B-12                  64-bit file size. Should be 0 if this is a directory entry.
/// 
/// 19-[variable]         File or directory name, the length of which is determined by the "size"
///                       field in this header.
/// --------------------------------------------------------------
/// 
/// 
/// == Catalog data ==
/// 
/// If a catalog area is reached (control code 2), then it will be followed by a series of
/// file headers, but no contents. All of this can be safely skipped until a new contents area
/// (control code 1) is reached.
/// 
/// 
/// </summary>
namespace QicStreamV2
{
    class Program
    {
        private enum ControlCode
        {
            None = 0,
            ContentsStart = 1,
            CatalogStart = 2,
            ParentDirectory = 3,
            File = 5,
            Directory = 6,
            DataChunk = 9
        }


        static void Main(string[] args)
        {
            string inFileName = "";
            string tempFileName;
            string baseDirectory = "out";
            long customOffset = 0;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                if (args[i] == "--offset") { customOffset = Convert.ToInt64(args[i + 1]); }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage: qicstreamv2 -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[65536];
            tempFileName = inFileName + ".tmp";

            // Pass 1: remove unused bytes (parity?) from the original file, and write to temporary file

            using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
            {
                using (var outStream = new FileStream(tempFileName, FileMode.Create, FileAccess.Write))
                {
                    while (stream.Position < stream.Length)
                    {
                        stream.Read(bytes, 0, 0x8000);

                        // Each block of 0x8000 bytes ends with 0x402 bytes of something (perhaps for parity checking)
                        // We'll just remove it and write the good bytes to the temporary file.
                        outStream.Write(bytes, 0, 0x8000 - 0x402);
                    }
                }
            }

            // Pass 2: extract files.

            try
            {
                using (var stream = new FileStream(tempFileName, FileMode.Open, FileAccess.Read))
                {
                    if (customOffset == 0)
                    {
                        // Read the volume header, which doesn't really contain much vital information.
                        stream.Read(bytes, 0, 0x3e);

                        string magic = Encoding.ASCII.GetString(bytes, 4, 4);
                        if (magic != "FSET")
                        {
                            throw new ApplicationException("Incorrect magic value.");
                        }

                        // The volume header continues with a dynamically-sized volume label:
                        int volNameLen = BitConverter.ToUInt16(bytes, 0x3C);
                        stream.Read(bytes, 0, volNameLen);

                        string volName = Encoding.ASCII.GetString(bytes, 0, volNameLen);
                        Console.WriteLine("Backup label: " + volName);

                        // ...followed by a dynamically-sized drive name:
                        // (which must be aligned on a 2-byte boundary)
                        if (stream.Position % 2 == 1)
                        {
                            stream.ReadByte();
                        }
                        stream.Read(bytes, 0, 2);
                        int driveNameLen = BitConverter.ToUInt16(bytes, 0);
                        stream.Read(bytes, 0, driveNameLen);

                        string driveName = Encoding.ASCII.GetString(bytes, 0, driveNameLen);
                        Console.WriteLine("Drive name: " + driveName);
                    }
                    else
                    {
                        // adjust offset to account for removed bytes
                        customOffset -= ((customOffset / 0x8000) * 0x402);

                        stream.Seek(customOffset, SeekOrigin.Begin);
                    }

                    // Maintain a list of subdirectories into which we'll descend and un-descend.
                    List<string> currentDirList = new List<string>();
                    Directory.CreateDirectory(baseDirectory);
                    string currentDirectory = baseDirectory;

                    bool isCatalogRegion = false;

                    // And now begins the main sequence of the backup, which consists of a control code,
                    // followed by the data (if any) that the control code represents.

                    while (stream.Position < stream.Length)
                    {
                        ControlCode code = (ControlCode)stream.ReadByte();

                        if (code == ControlCode.CatalogStart)
                        {
                            isCatalogRegion = true;
                        }
                        else if (code == ControlCode.ContentsStart)
                        {
                            isCatalogRegion = false;
                        }
                        else if (code == ControlCode.ParentDirectory && !isCatalogRegion)
                        {
                            // Go "up" to the parent directory
                            if (currentDirList.Count > 0) { currentDirList.RemoveAt(currentDirList.Count - 1); }
                        }
                        else if (code == ControlCode.Directory)
                        {
                            // This control code is followed by a directory header which tells us the name
                            // of the directory that we're descending into.
                            var header = new DirectoryHeader(stream);

                            if (!isCatalogRegion)
                            {
                                currentDirList.Add(header.Name);

                                currentDirectory = baseDirectory;
                                for (int i = 0; i < currentDirList.Count; i++)
                                {
                                    currentDirectory = Path.Combine(currentDirectory, currentDirList[i]);
                                }

                                Directory.CreateDirectory(currentDirectory);
                                Directory.SetCreationTime(currentDirectory, header.DateTime);
                                Directory.SetLastWriteTime(currentDirectory, header.DateTime);

                                Console.WriteLine(stream.Position.ToString("X") + ": New directory - " + currentDirectory + " - " + header.DateTime.ToShortDateString());
                            }
                        }
                        else if (code == ControlCode.File)
                        {
                            // This control code is followed by a file header which tells us all the details
                            // about the file, followed by the actual file contents.
                            var header = new FileHeader(stream);

                            if (!header.Valid)
                            {
                                Console.WriteLine("Invalid file header, probably end of archive.");
                                break;
                            }

                            if (!isCatalogRegion)
                            {
                                string fileName = Path.Combine(currentDirectory, header.Name);
                                using (var f = new FileStream(Path.Combine(currentDirectory, header.Name), FileMode.Create, FileAccess.Write))
                                {
                                    int bytesLeft = header.Size;

                                    while (bytesLeft > 0)
                                    {
                                        do
                                        {
                                            if (stream.Position >= stream.Length) { return; }
                                            code = (ControlCode)stream.ReadByte();
                                        }
                                        while (code != ControlCode.DataChunk);

                                        stream.Read(bytes, 0, 3);
                                        int chunkSize = BitConverter.ToUInt16(bytes, 1);

                                        stream.Read(bytes, 0, chunkSize);
                                        f.Write(bytes, 0, chunkSize);

                                        if (bytesLeft == header.Size)
                                        {
                                            if (!QicUtils.Utils.VerifyFileFormat(header.Name, bytes))
                                            {
                                                Console.WriteLine(stream.Position.ToString("X") + " -- Warning: file format doesn't match: " + fileName);
                                                Console.ReadKey();
                                            }
                                        }

                                        bytesLeft -= chunkSize;
                                    }
                                }
                                File.SetCreationTime(fileName, header.DateTime);
                                File.SetLastWriteTime(fileName, header.DateTime);
                                File.SetAttributes(fileName, header.Attributes);

                                Console.WriteLine(stream.Position.ToString("X") + ": " + fileName + " - " + header.Size.ToString() + " bytes - " + header.DateTime.ToShortDateString());
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
            finally
            {
                File.Delete(tempFileName);
            }
        }

        private class FileHeader
        {
            public int Size { get; }
            public string Name { get; }
            public DateTime DateTime { get; }
            public FileAttributes Attributes { get; }
            public bool Valid { get; }

            public FileHeader(Stream stream)
            {
                byte[] bytes = new byte[1024];
                stream.Read(bytes, 0, 3);

                int structLength = BitConverter.ToUInt16(bytes, 1);
                if (structLength == 0) { return; }

                stream.Read(bytes, 0, structLength);

                Attributes = (FileAttributes)bytes[0];
                DateTime = QicUtils.Utils.DateTimeFromTimeT(BitConverter.ToUInt32(bytes, 4));
                Size = BitConverter.ToInt32(bytes, 8);

                int nameLength = structLength - 0x16;
                Name = Encoding.ASCII.GetString(bytes, structLength - nameLength, nameLength);

                Valid = true;
            }
        }

        private class DirectoryHeader : FileHeader
        {
            public DirectoryHeader(Stream stream)
                : base(stream)
            {
            }
        }

    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// Decoder for Backup Exec formatted tape images.
/// Backup Exec used to be branded (licensed?) under many names, including but not limited to:
/// "Colorado Backup", "HP Backup", "Arcada Backup", etc.
/// 
/// Copyright Dmitry Brant, 2019
/// 
/// 
/// The backup is organized using three separate files (records) on the tape. The first is a volume
/// header, which is 512 bytes. Next is the catalog, which contains a full inventory of the files
/// and directories in the backup. And lastly, the third record contains the actual contents of
/// all the files, which are literally concatenated one after the other, with no other delimiting
/// data.
/// 
/// NOTE: The block size for these backups seems to be set to 16KB (0x4000 bytes). To read these
/// backups using the `dd` tool, you might need to manually set the block size, otherwise it will
/// report a memory allocation error:
/// 
/// $ dd if=/dev/nst0 of=foo.bin bs=16k
/// 
/// 
/// == Volume header ==
/// 
/// The volume header is 512 bytes, and does not seem to contain any particularly vital data for
/// reconstructing the backup.
/// 
/// == Catalog ==
/// 
/// The catalog is made up of file and directory headers, one after the other.
/// Here is a breakdown of the header structure:
/// 
/// Byte offset (hex)     Meaning
/// ----------------------------------------
/// 0-1                   16-bit size of this header, including these bytes.
/// 
/// 2-3                   Always seems to be 1.
/// 
/// 4-A                   File date and time, and possibly attributes. This seems to be encoded in a
///                       non-standard way. See the implementation for the binary breakdown of the
///                       date and time.
/// 
/// E-11                  File size.
/// 
/// 12-[variable]         File or directory name. The length is determined by the total size of this
///                       header, which is given in the first field.
/// -------------------------------------------------------------------------
/// 
/// NOTE: If a header is too close to a block boundary (0x4000 bytes), it will be aligned on the
/// next block boundary, with the unused bytes being padded with 0s.
/// 
/// == File contents ==
/// 
/// The file contents are literally packed together, one after the other, in the same order as the
/// files appear in the catalog, with no other delimiting information. Therefore, the file sizes
/// that are provided in the catalog should precisely determine the boundaries of each file within
/// the contents.
/// 
/// 
/// </summary>
namespace hpbackup
{
    class Program
    {
        private const int BLOCK_SIZE = 0x4000;

        static void Main(string[] args)
        {
            string catFileName = "";
            string inFileName = "";
            string baseDirectory = "out";

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-c") { catFileName = args[i + 1]; }
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                if (args[i] == "-d") { baseDirectory = args[i + 1]; }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage: hpbackup -c <catalog file name> -f <data file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[65536];

            // First, read the catalog.
            var catalog = new List<FileHeader>();
            try
            {
                using (var stream = new FileStream(catFileName, FileMode.Open, FileAccess.Read))
                {
                    while (stream.Position < stream.Length)
                    {
                        var header = new FileHeader(stream);

                        if (!header.Valid)
                        {
                            AlignToNextBlock(stream);
                            continue;
                        }

                        catalog.Add(header);
                        Console.WriteLine(stream.Position.ToString("X") + ":  " + header.Size.ToString("X") + " -- " + header.DateTime.ToString() + " -- " + header.Name);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }

            // ...And read the contents, hoping that the data perfectly lines up with the contents.
            try
            {
                using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                {
                    while (stream.Position < stream.Length && catalog.Count > 0)
                    {
                        FileHeader currentFile = null;
                        do
                        {
                            currentFile = catalog[0];
                            catalog.RemoveAt(0);
                        }
                        while (currentFile.Size == 0);
                        if (currentFile == null)
                        {
                            Console.WriteLine("Reached end of catalog.");
                            break;
                        }

                        string filePath = baseDirectory;
                        if (currentFile.Subdirectory.Length > 0)
                        {
                            string[] dirArray = currentFile.Subdirectory.Split('\\');
                            for (int i = 0; i < dirArray.Length; i++)
                            {
                                filePath = Path.Combine(filePath, dirArray[i]);
                            }
                        }
                        Directory.CreateDirectory(filePath);
                        filePath = Path.Combine(filePath, currentFile.Name);

                        Console.WriteLine("Restoring: " + filePath);

                        using (var f = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                        {
                            long bytesLeft = currentFile.Size;
                            while (bytesLeft > 0)
                            {
                                int bytesToRead = bytes.Length;
                                if (bytesToRead > bytesLeft) { bytesToRead = (int)bytesLeft; }
                                stream.Read(bytes, 0, bytesToRead);
                                f.Write(bytes, 0, bytesToRead);

                                if (bytesLeft == currentFile.Size)
                                {
                                    if (!VerifyFileFormat(currentFile.Name, bytes))
                                    {
                                        Console.WriteLine(stream.Position.ToString("X") + " -- Warning: file format doesn't match: " + filePath);
                                        Console.ReadKey();
                                    }
                                }

                                bytesLeft -= bytesToRead;
                            }
                        }

                        File.SetCreationTime(filePath, currentFile.DateTime);
                        File.SetLastWriteTime(filePath, currentFile.DateTime);
                        //File.SetAttributes(filePath, header.Attributes);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
        }

        private static void AlignToNextBlock(Stream stream)
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
            public string Subdirectory { get; }
            public bool Valid { get; }

            public FileHeader(Stream stream)
            {
                Subdirectory = "";
                byte[] bytes = new byte[1024];
                long initialPos = stream.Position;

                stream.Read(bytes, 0, 2);
                int headerLen = BitConverter.ToInt16(bytes, 0);

                if (headerLen == 0)
                {
                    return;
                }

                stream.Read(bytes, 2, headerLen - 2);

                // TODO: figure this out?
                //Attributes = (FileAttributes)bytes[0x5];

                int dateInt = BitConverter.ToInt32(bytes, 0x4);
                int year = dateInt & 0xFFF;
                int month = (dateInt & 0xF000) >> 12;
                int day = (dateInt & 0x1F0000) >> 16;

                int timeInt = BitConverter.ToInt32(bytes, 0x8);
                int hour = timeInt & 0x1F;
                int minute = (timeInt & 0x7E0) >> 5;

                DateTime = new DateTime(year, month, day, hour, minute, 0);

                Size = BitConverter.ToInt32(bytes, 0xE);

                Name = Encoding.ASCII.GetString(bytes, 0x12, headerLen - 0x12);
                Name = Name.Replace("A:\\", "").Replace("B:\\", "").Replace("C:\\", "").Replace("D:\\", "");

                string[] pathArray = Name.Split('\\');
                Name = pathArray[pathArray.Length - 1];

                if (pathArray.Length > 1)
                {
                    Subdirectory = String.Join("\\", pathArray, 0, pathArray.Length - 1);
                }

                Valid = true;
            }
        }

        private static bool VerifyFileFormat(string fileName, byte[] bytes)
        {
            string nameLower = fileName.ToLower();

            if (nameLower.EndsWith(".exe") && (bytes[0] != 'M' || bytes[1] != 'Z')) { return false; }
            if (nameLower.EndsWith(".zip") && (bytes[0] != 'P' || bytes[1] != 'K')) { return false; }
            if (nameLower.EndsWith(".dwg") && (bytes[0] != 'A' || bytes[1] != 'C')) { return false; }

            return true;
        }
    }
}

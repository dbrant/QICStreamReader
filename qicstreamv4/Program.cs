using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Decoder for QICStream (V4?) format.
/// 
/// Copyright Dmitry Brant, 2019
/// 
/// 
/// Brief outline of the format, to the best of my reverse-engineering ability:
/// 
/// * Every file and directory record is aligned to a block boundary (512 bytes).
/// * Little-endian.
/// * Here is a high-level organization of the stream:
/// 
/// [Volume header (1 block, 512 bytes)]
/// [File or directory entry (aligned to block)]
/// [File or directory entry (aligned to block)]
/// [File or directory entry (aligned to block)]
/// ....
/// 
/// Each file or directory entry starts with a header. If it's a file (not a directory) then
/// the file contents will follow immediately after the header.
/// 
/// == File Header structure ==
/// 
/// Byte offset (hex)     Meaning
/// ----------------------------------------
/// 0                     A value of 9 means it's a file header.
/// 
/// 18                    A value of 0 seems to mean that the file contents will appear immediately
///                       following this header. However, a value of 1 seems to mean that the header
///                       will be followed by 0x24 extra bytes of data, and then followed by the contents.
/// 
/// 4E-51                 32-bit size of the file.
/// 
/// 52-55                 Another 32-bit size of the file. If this size is nonzero, then this is the size
///                       that should be trusted, and not the other size fields. If this size is zero, then
///                       the other size fields should be used.
/// 
/// 56-5C                 64-bit size of the file.
/// 
/// 5F                    File attributes (the usual DOS attribute bits).
/// 
/// 68-69                 Creation year.
/// 6A-6B                 Creation month.
/// 6C-6D                 Creation day.
/// 6E-6F                 Creation hour.
/// 70-71                 Creation minute.
/// 72-73                 Creation second.
/// 
/// 78-79                 Modification year.
/// 7A-7B                 Modification month.
/// 7C-7D                 Modification day.
/// 7E-7F                 Modification hour.
/// 80-81                 Modification minute.
/// 82-83                 Modification second.
/// 
/// 9A-9B                 File name length, including null terminating character.
/// 
/// 9C-9D                 16-bit size of this header, which should be 0x9E.
/// ------------------------------------------------------------------------------
/// 
/// The header is immediately followed by the file name (the length of which is given in the header).
/// 
/// If the file header (plus file name) doesn't end on a two-byte boundary, then an extra
/// padding byte will be present.
/// 
/// The header (plus file name) is followed by the file contents, unless the header specifies
/// that extra data is present (see offset 18 in the header), in which case there will be an
/// extra 0x24 bytes, and then the file contents will begin.
/// 
/// == Directory Header structure ==
/// 
/// Byte offset (hex)     Meaning
/// ----------------------------------------
/// 0                     A value of 8 means it's a directory header.
/// 
/// 96-97                 Directory name length, including null terminating character. This could
///                       be 1, which would be just the null character, meaning that this is the
///                       root directory of the archive.
///                       This name may also contain one or more null characters in the middle, to
///                       denote subdirectories. For example, the name "FOO\0BAR\0BAZ" would translate
///                       into the directory structure of FOO\BAR\BAZ.
/// 
/// 98-99                 16-bit size of this header, which should be 0x9A.
/// ------------------------------------------------------------------------------
/// 
/// This header is followed immediately by the name of the directory (the length of which is given
/// in the header).
/// 
/// There are no other contents that follow the directory header (the next file or directory header will
/// start on the next block boundary).
/// 
/// When a directory header is present, that directory becomes the "current" directory, and all subsequent
/// files shall be placed in the current directory.
/// 
/// 
/// 
/// </summary>
namespace QicStreamV4
{
    class Program
    {
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
                Console.WriteLine("Usage: qicstreamv4 -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[65536];

            try
            {
                using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                {
                    // Read the volume header
                    stream.Read(bytes, 0, 0x200);

                    string volName = Encoding.ASCII.GetString(bytes, 0xAC, 0x80);
                    int zeroPos = volName.IndexOf("\0");
                    if (zeroPos > 0) { volName = volName.Substring(0, zeroPos); }
                    Console.WriteLine("Backup label: " + volName);


                    // Maintain a list of subdirectories into which we'll descend and un-descend.
                    List<string> currentDirList = new List<string>();
                    Directory.CreateDirectory(baseDirectory);
                    string currentDirectory = baseDirectory;

                    // And now begins the main sequence of the backup, which consists of a file or directory header,
                    // and the contents of the file.
                    // Each new file/directory header is aligned on a block boundary (512 bytes).

                    while (stream.Position < stream.Length)
                    {
                        FileHeader header = new FileHeader(stream);

                        if (!header.Valid)
                        {
                            continue;
                        }

                        if (header.IsDirectory)
                        {
                            if (header.Name.Length > 0)
                            {
                                string[] dirArray = header.Name.Split('\0');
                                currentDirectory = baseDirectory;
                                for (int i = 0; i < dirArray.Length; i++)
                                {
                                    currentDirectory = Path.Combine(currentDirectory, dirArray[i]);
                                }
                                Directory.CreateDirectory(currentDirectory);
                                Directory.SetCreationTime(currentDirectory, header.DateTime);
                                Directory.SetLastWriteTime(currentDirectory, header.DateTime);

                                Console.WriteLine(stream.Position.ToString("X") + ": New directory - " + currentDirectory + ", " + header.DateTime.ToShortDateString());
                            }
                        }
                        else
                        {
                            string fileName = Path.Combine(currentDirectory, header.Name);
                            using (var f = new FileStream(fileName, FileMode.Create, FileAccess.Write))
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
                                        if (!VerifyFileFormat(header.Name, bytes))
                                        {
                                            Console.WriteLine(stream.Position.ToString("X") + " -- Warning: file format doesn't match: " + fileName);
                                            Console.ReadKey();
                                        }
                                    }

                                    bytesLeft -= bytesToRead;
                                }
                            }
                            File.SetCreationTime(fileName, header.DateTime);
                            File.SetLastWriteTime(fileName, header.DateTime);
                            File.SetAttributes(fileName, header.Attributes);

                            Console.WriteLine(stream.Position.ToString("X") + ": " + fileName + ", " + header.Size.ToString() + " bytes - " + header.DateTime.ToShortDateString());
                        }

                        // align position to the next 0x200 bytes
                        long align = stream.Position % 0x200;
                        if (align > 0) { stream.Seek(0x200 - align, SeekOrigin.Current); }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
            finally
            {
            }
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
                byte[] bytes = new byte[1024];
                stream.Read(bytes, 0, 2);

                if (bytes[0] != 0x8 && bytes[0] != 9)
                {
                    Console.WriteLine(stream.Position.ToString("X") + " -- Warning: skipping over block.");
                    Console.ReadKey();

                    // skip over this block
                    stream.Seek(0x1fe, SeekOrigin.Current);
                    return;
                }

                IsDirectory = bytes[0] == 0x8;

                // Read extra unknown bytes at the beginning
                stream.Read(bytes, 0, IsDirectory ? 0x48 : 0x4C);

                bool hasExtraData = bytes[0x16] != 0;

                // Read header with meaningful data
                stream.Read(bytes, 0, 0x50);

                if (!IsDirectory)
                {
                    Size = BitConverter.ToUInt32(bytes, 0x4);
                    if (Size == 0)
                    {
                        Size = BitConverter.ToInt64(bytes, 0x8);
                    }
                    else
                    {
                        Console.WriteLine(stream.Position.ToString("X") + " -- Warning: using secondary size field.");
                        Console.ReadKey();

                        // hack?
                        //Size += 0x200;
                    }
                }

                Attributes = (FileAttributes)bytes[0x11];

                int year = BitConverter.ToUInt16(bytes, 0x1A);
                int month = BitConverter.ToUInt16(bytes, 0x1C);
                int day = BitConverter.ToUInt16(bytes, 0x1E);
                int hour = BitConverter.ToUInt16(bytes, 0x20);
                int minute = BitConverter.ToUInt16(bytes, 0x22);
                int second = BitConverter.ToUInt16(bytes, 0x24);
                try
                {
                    DateTime = new DateTime(year, month, day, hour, minute, second);
                }
                catch { }

                int nameLength = BitConverter.ToUInt16(bytes, 0x4C);

                stream.Read(bytes, 0, nameLength);
                // the name length includes null terminator, so trim it.
                Name = Encoding.ASCII.GetString(bytes, 0, nameLength - 1);

                // make sure we end on a two-byte boundary
                if (stream.Position % 2 == 1)
                {
                    stream.ReadByte();
                }

                if (hasExtraData)
                {
                    stream.Seek(0x24, SeekOrigin.Current);
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

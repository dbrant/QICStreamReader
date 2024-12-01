using QicUtils;
using System;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Decoder for tape images made by old versions of MaynStream backup software, from Maynard Electronics.
/// These backups are vaguely identifiable by the bytes 0x1 0x0 at the beginning of the file, and likely
/// a human-readable volume label at offset 0xAC.
/// 
/// Copyright Dmitry Brant, 2019-
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
/// 4E-51                 32-bit total size of the file.
/// 
/// 52-55                 32-bit size of this fragment of the file. If this size is zero, then the previous
///                       field should be used for the size. Otherwise, this size should be used to read the
///                       data that follows the header.
///                       This field becomes nonzero if a large file spans across two tape images, i.e. starts
///                       at the end of one tape image, and continues in the next one. In the starting header in
///                       the first tape image, this size will be zero, but in the next tape image this size
///                       will be populated, signaling to the reader that this is a continuation of the previous file.
///                       If this field is nonzero, then the contents will begin on the next block boundary, instead
///                       of immediately following the header.
/// 
/// 56-5C                 64-bit total size of the file.
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
namespace maynstream
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
                Console.WriteLine("Usage: maynstream -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[65536];

            using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);
            // Read the volume header
            stream.Read(bytes, 0, BLOCK_SIZE);

            string volName = Utils.GetNullTerminatedString(Encoding.ASCII.GetString(bytes, 0xAC, 0x80));
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
                    if (header.Continuation)
                    {
                        Console.WriteLine("Warning: continuing file " + header.Name + " (will append contents).");
                    }
                    else if (File.Exists(fileName))
                    {
                        Console.WriteLine("Warning: file exists: " + header.Name);
                        Console.ReadKey();
                        continue;
                    }
                    using (var f = new FileStream(fileName, header.Continuation ? FileMode.Append : FileMode.Create, FileAccess.Write))
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

                            if (bytesLeft == header.Size && !header.Continuation)
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
                    File.SetCreationTime(fileName, header.DateTime);
                    File.SetLastWriteTime(fileName, header.DateTime);
                    File.SetAttributes(fileName, header.Attributes);

                    Console.WriteLine(stream.Position.ToString("X") + ": " + fileName + ", " + header.Size.ToString() + " bytes - " + header.DateTime.ToShortDateString());
                }
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
            public bool Continuation { get; }

            public FileHeader(Stream stream)
            {
                byte[] bytes = new byte[1024];
                stream.Read(bytes, 0, 2);

                if (bytes[0] != 0x8 && bytes[0] != 9)
                {
                    Console.WriteLine(stream.Position.ToString("X") + " -- Warning: skipping over block.");
                    Console.ReadKey();

                    // skip over this block
                    AlignToNextBlock(stream);
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
                        Continuation = true;
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
                if (Continuation)
                {
                    // the contents will be aligned to the next block boundary.
                    AlignToNextBlock(stream);
                }
                Valid = true;
            }
        }

    }
}

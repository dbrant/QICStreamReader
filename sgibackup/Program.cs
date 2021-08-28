using System;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Decoder for tape backups written by an SGI workstation.
/// Not sure which actual program was used for writing these backups, but it was easy enough
/// to reverse-engineer.
/// 
/// * Firstly, if a tape was written on an SGI workstation (which is MIPS), and then you read
/// it using a x86 or x64 machine, you'll likely need to convert the endianness of the whole image,
/// i.e. reverse the byte order of every two bytes.
/// 
/// Once the endianness is properly converted, the format of the data is roughly as follows:
/// 
/// The data is organized in blocks of 0x800 bytes. Every block begins with a 0x100 byte header,
/// which has this structure:
/// 
/// NOTE about number representations in the header:
/// The number fields in the header are actually ASCII strings that contain the hex value of the
/// number, padded with spaces to the left.
/// 
/// NOTE about dates in the header:
/// Dates are stored as ASCII strings that contain the hex value which in turn is a big-endian
/// time_t value.
/// 
/// Offset       Length            Data
/// -----------------------------------------------------------------------------------
/// 0            0x80              Name of the current file, including path, padded with nulls
///                                at the end.
/// 
/// 0x80         0x8               unknown
/// 
/// 0x88         0x8               Sequence number of this block within the backup. Starts with
///                                "0", "1", "2", etc.
/// 
/// 0x90         0x8               Sequence number of this block within the current file. Starts
///                                with "0", "1", "2", etc.
/// 
/// 0x98         0x8               Modification date of the file.
/// 
/// 0xa0         0x8               unknown
/// 
/// 0xa8         0x8               unknown
/// 
/// 0xb0         0x4               Header type (see below). Can be one of "1234", "2345", "3456",
///                                or "7890".
/// 
/// 0xb4         0x4               unknown
/// 
/// 0xb8         0x4               unknown
/// 
/// 0xbc         0x4               unknown
/// 
/// 0xc0         0x4               unknown
/// ------------------------------------------------------------------------------------
/// 
/// Header types:
/// 
/// "1234": Volume name. Indicates that this block is actually the name of the backup volume, and
///         not associated with any specific file.
/// 
/// "2345": Beginning of new file. Indicates that this block contains metadata about the file, which
///         could be a file or a directory. (See below for beakdown of metadata structure)
/// 
/// "3456": File data. Indicates that this block contains data belonging to the current file. Since
///         the header takes up 0x100 bytes, the remaining 0x700 bytes are actual data, or rather
///         up to 0x700 bytes, depending on how many expected bytes are remaining.
/// 
/// "7890": Volume name (?)
/// 
/// 
/// File metadata structure (for "2345" blocks):
/// 
/// Offset       Length            Data
/// -----------------------------------------------------------------------------------
/// 0x180        0x8               File attributes. Importantly, if the first digit is "4", then this
///                                is a directory, and if the first digit is "8", then this is a
///                                regular file.
/// 
/// 0x188        0x8               unknown
/// 
/// 0x190        0x8               unknown
/// 
/// 0x198        0x8               unknown
/// 
/// 0x1a0        0x8               unknown
/// 
/// 0x1a8        0x8               unknown
/// 
/// 0x1b0        0x8               unknown
/// 
/// 0x1b8        0x8               File size
/// 
/// 0x1c0        0x8               Last access date
/// 
/// 0x1c8        0x8               Modification date
/// 
/// 0x1d0        0x8               Creation date
/// 
/// 0x1d8        0x8               unknown
/// 
/// 0x1e0        0x8               unknown
/// 
/// 
/// </summary>
namespace sgibackup
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
                Console.WriteLine("Usage: sgibackup -f <file name> [-d <output directory>]");
                return;
            }

            try
            {
                using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                {
                    int RemainingSize = 0;
                    FileHeader currentHeader = null;
                    int currentSequence = -1;

                    while (true)
                    {
                        var header = new FileHeader(stream);

                        if (!header.Valid)
                        {
                            Console.WriteLine("Invalid file header, probably end of archive.");
                            break;
                        }

                        if (header.Sequence - currentSequence != 1)
                        {
                            Console.WriteLine("Warning: sequence number inconsistent, ignoring block.");
                            continue;
                        }
                        currentSequence = header.Sequence;

                        if (header.HeaderType == HeaderType.Volume || header.HeaderType == HeaderType.Unknown)
                        {
                            continue;
                        }

                        if (header.HeaderType == HeaderType.Metadata)
                        {
                            currentHeader = header;
                            RemainingSize = header.Size;

                            Console.WriteLine(stream.Position.ToString("X") + ": " + header.Name + " - "
                                + header.Size.ToString() + " bytes - " + header.CreateDate.ToShortDateString());
                        }

                        var pathArr = header.Name.Split(new char[] { '/' });
                        string path = "";

                        for (int i = 0; i < (header.IsDirectory ? pathArr.Length : pathArr.Length - 1); i++)
                        {
                            if (i > 0) path += Path.DirectorySeparatorChar;
                            path += pathArr[i];
                        }

                        path = Path.Combine(baseDirectory, path);
                        if (header.HeaderType == HeaderType.Metadata)
                        {
                            Directory.CreateDirectory(path);
                        }

                        if (header.IsDirectory || header.HeaderType != HeaderType.Data)
                        {
                            continue;
                        }

                        string fileName = Path.Combine(path, pathArr[pathArr.Length - 1]);

                        using (var f = new FileStream(fileName, FileMode.Append, FileAccess.Write))
                        {
                            int bytesToWrite = 0x700;
                            if (bytesToWrite > RemainingSize)
                            {
                                bytesToWrite = RemainingSize;
                            }

                            f.Write(header.bytes, 0x100, bytesToWrite);

                            RemainingSize -= bytesToWrite;
                        }

                        if (RemainingSize == 0)
                        {
                            File.SetCreationTime(fileName, currentHeader.CreateDate);
                            File.SetLastWriteTime(fileName, currentHeader.ModifyDate);
                            //File.SetAttributes(fileName, header.Attributes);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
        }

        private enum HeaderType
        {
            Volume, Metadata, Data, Unknown
        }

        private class FileHeader
        {
            public bool IsDirectory { get; }
            public HeaderType HeaderType { get; }
            public int Sequence { get; }
            public int Size { get; }
            public string Name { get; }
            public DateTime CreateDate { get; }
            public DateTime ModifyDate { get; }
            public FileAttributes Attributes { get; }
            public bool Valid { get; }

            public byte[] bytes;

            public FileHeader(Stream stream)
            {
                bytes = new byte[0x800];
                stream.Read(bytes, 0, bytes.Length);

                Name = Encoding.ASCII.GetString(bytes, 0, 0x80).Replace("\0", "");
                if (Name.Length < 1)
                {
                    Valid = false;
                    return;
                }

                Sequence = Convert.ToInt32(Encoding.ASCII.GetString(bytes, 0x88, 8).Trim(), 16);

                var type = Encoding.ASCII.GetString(bytes, 0xb0, 4);

                if (type == "1234" || type == "7890")
                {
                    HeaderType = HeaderType.Volume;
                }
                else if (type == "2345")
                {
                    HeaderType = HeaderType.Metadata;
                }
                else if (type == "3456")
                {
                    HeaderType = HeaderType.Data;
                }
                else
                {
                    HeaderType = HeaderType.Unknown;
                }

                if (HeaderType == HeaderType.Metadata)
                {

                    var attrs = Encoding.ASCII.GetString(bytes, 0x180, 8).Trim();
                    IsDirectory = attrs[0] == '4';

                    Size = Convert.ToInt32(Encoding.ASCII.GetString(bytes, 0x1b8, 8).Trim(), 16);
                    ModifyDate = QicUtils.Utils.DateTimeFromTimeT(Convert.ToInt64(Encoding.ASCII.GetString(bytes, 0x1c0, 8).Trim(), 16));
                    CreateDate = QicUtils.Utils.DateTimeFromTimeT(Convert.ToInt64(Encoding.ASCII.GetString(bytes, 0x1c8, 8).Trim(), 16));
                }

                Valid = true;
            }
        }
    }
}

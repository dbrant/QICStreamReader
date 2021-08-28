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

            byte[] bytes = new byte[65536];

            try
            {
                using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                {
                    int RemainingSize = 0;

                    while (true)
                    {
                        var header = new FileHeader(stream);

                        if (!header.Valid)
                        {
                            Console.WriteLine("Invalid file header, probably end of archive.");
                            break;
                        }

                        Console.WriteLine(">>> " + header.Name + new String(' ', 64 - header.Name.Length) + header.stats + "    " + header.stats2);

                        if (header.IsVolume || header.IsDirectory)
                        {
                            continue;
                        }

                        if (!header.HasData && header.Size > 0)
                        {
                            RemainingSize = header.Size;
                            continue;
                        }


                        var pathArr = header.Name.Split(new char[] { '/' });
                        string path = "";
                        for (int i = 0; i < pathArr.Length - 1; i++)
                        {
                            if (i > 0) path += Path.DirectorySeparatorChar;
                            path += pathArr[i];
                        }

                        path = Path.Combine(baseDirectory, path);

                        Directory.CreateDirectory(path);

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

                    }



                    /*


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

                    */

                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
        }


        private class FileHeader
        {
            public bool IsVolume { get; }
            public bool IsDirectory { get; }
            public bool HasData { get; }
            public int Size { get; }
            public string Name { get; }
            public DateTime DateTime { get; }
            public FileAttributes Attributes { get; }
            public bool Valid { get; }

            public byte[] bytes;
            public string stats;
            public string stats2 = "";

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

                stats = Encoding.ASCII.GetString(bytes, 0x80, 0x80).Replace("\0", "");

                var statsArr = stats.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                var type = statsArr[4].Substring(7);

                IsVolume = type == "1234" || type == "7890";
                HasData = type == "3456";

                if (type == "2345")
                {
                    stats2 = Encoding.ASCII.GetString(bytes, 0x180, 0x80).Replace("\0", "");

                    var stats2Arr = stats2.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    IsDirectory = stats2Arr[0][0] == '4';

                    Size = Convert.ToInt32(stats2.Substring(0x38, 8).Trim(), 16);
                }

                Valid = true;
            }
        }
    }
}

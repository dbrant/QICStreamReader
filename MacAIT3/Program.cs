using System;
using System.IO;
using System.Text;

/// <summary>
/// 
/// (Dmitry Brant, Apr 2022)
/// 
/// Recently I came across a backup tape (an AIT-3 100GB tape) that was written with a format
/// I didn't recognize. The only thing I knew is that it came from a Mac workstation, which means
/// it was likely written using Retrospect, which was a popular backup tool at the time.
/// This is the result of my reverse-engineering effort to get the contents out of this archive.
/// ------------------
/// 
/// This backup format is composed of a sequence of blocks which use FourCC-style formatting.
/// (All data is big-endian. Dates are formatted as seconds since Jan 1 1904.)
/// 
/// Each block has the following format:
/// 
/// offset | length |
/// ----------------------------------
///   0    |   4    | Block name
///   4    |   4    | Block length (including the name and length fields)
///   8    |   n    | Block data
/// 
/// If the goal is simply to recover the file contents from the backup, we only need to care
/// about these block types: "Diry", "File", "Fork", and "Cont".
/// A "Diry" block indicates the directory in which any subsequent files are stored.
/// A "File" block indicates the start of a new file, and contains metadata about the file (name,
/// date, etc). After this block, there may be any number of "Fork" and "Cont" blocks that contain
/// the actual contents of the current file. In other words, the file contents consist of numerous
/// "Fork" blocks, and each fork block can have numerous continuation "Cont" blocks. And so, the
/// overall structure looks like this:
/// 
/// "Diry" < --current directory
/// "File" <-- first file
/// "Fork"
/// "Cont"
/// "Cont"
/// ...
/// "Fork"
/// "Cont"
/// "Cont"
/// ...
/// ...
/// "File" <-- second file
/// "Fork"
/// "Cont"
/// ...
/// 
/// Here is the structure of a "Diry" block:
/// 
/// offset | length |
/// ----------------------------------
///   0    |   4    | "Diry"
///   4    |   4    | Block length
///   8    |   4    | Access date
///   16   |   4    | Create date
///   1A   |   4    | Modify date
///   50   |   n    | Directory name, until end of block
/// 
/// Here is the structure of a "File" block:
/// 
/// offset | length |
/// ----------------------------------
///   0    |   4    | "File"
///   4    |   4    | Block length
///   8    |   4    | Access date
///   16   |   4    | Create date
///   1A   |   4    | Modify date
///   1E   |   8    | File size
///   46   |   n    | File name, until end of block
/// 
/// Here is the structure of a "Fork" block:
/// 
/// offset | length |
/// ----------------------------------
///   0    |   4    | "Fork"
///   4    |   4    | Block length
///   8    |   16   | Some kind of fork-specific header, seems to be ignorable.
///   1E   |   n    | File data, until end of block.
/// 
/// Here is the structure of a "Cont" block:
/// 
/// offset | length |
/// ----------------------------------
///   0    |   4    | "Cont"
///   4    |   4    | Block length
///   8    |   n    | File data, until end of block.
/// 
/// NOTE: Once in a while a block will be realigned onto a 0x200-byte boundary. If you're expecting
/// to read a new block and instead you get a null block name, try aligning to the next 0x200 byte
/// boundary.
/// 
/// NOTE 2: The tape backup is "segmented" into segments of 512MiB each, which are written as separate
/// file records on the tape. The very first segment starts with a 0x2000 byte header which does NOT
/// conform to the "block" format. After skipping over this header, the block structure starts.
/// 
/// There are several other types of blocks such as "Priv", "NodX", and "Sgmt" (which contains metadata
/// about the backup volume itself), but these seem to be safely ignorable.
/// 
/// </summary>
namespace MacAIT3
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

            byte[] bytes = new byte[65536];

            int fileNum = 0;

            try
            {
                FileHeader curHeader = null;
                string curDirectory = "";

                while (true)
                {
                    if (fileNum > 70) { break; }
                    if (!File.Exists(fileNum + ".bin"))
                    {
                        fileNum++;
                        continue;
                    }

                    Console.WriteLine("Opening: " + fileNum + ".bin");
                    using (var stream = new FileStream(fileNum + ".bin", FileMode.Open, FileAccess.Read))
                    {
                        while (stream.Position < stream.Length)
                        {
                            stream.Read(bytes, 0, 8);

                            if (bytes[0] == 0 && bytes[1] == 0)
                            {
                                // If we land on null values in the block name, we might need to align onto
                                // a 0x200-byte boundary.
                                if ((stream.Position % 0x200) > 0)
                                {
                                    stream.Seek(0x200 - (stream.Position % 0x200), SeekOrigin.Current);
                                }
                                continue;
                            }

                            string blockName = Encoding.ASCII.GetString(bytes, 0, 4);

                            if (blockName == "Rxvr")
                            {
                                // Probably the first segment, which begins with a 0x2000 byte header.
                                // Just skip it.
                                stream.Seek(0x2000 - 8, SeekOrigin.Current);
                                continue;
                            }

                            long blockSize = QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 4));

                            blockSize -= 8;


                            int bytesToWrite = 0;

                            // If we want to be verbose:
                            //Console.WriteLine(stream.Position.ToString("X2") + ": " + blockName + ": " + blockSize);

                            if (blockName == "File")
                            {

                                if (curHeader != null)
                                {
                                    // If we're closing out the current file, apply any metadata and attributes to it.
                                    try
                                    {
                                        File.SetCreationTime(curHeader.AbsPath, curHeader.DateTime);
                                        File.SetLastWriteTime(curHeader.AbsPath, curHeader.DateTime);
                                    }
                                    catch { }
                                }

                                curHeader = new FileHeader(stream, (int)blockSize);
                                Console.WriteLine(stream.Position.ToString("X2") + ": New file > " + curHeader.Name + ": " + curHeader.Size);
                            }
                            else if (blockName == "Diry")
                            {
                                var header = new DirectoryHeader(stream, (int)blockSize);
                                Console.WriteLine(stream.Position.ToString("X2") + ": New directory > " + header.Name);
                                curDirectory = header.Name;
                            }
                            else if (blockName == "Fork")
                            {
                                // The Fork block seems to have a 0x16-byte header, followed by the data.
                                // TODO: figure out the use of the header in the Fork block?
                                stream.Seek(0x16, SeekOrigin.Current);
                                blockSize -= 0x16;

                                bytesToWrite = (int)blockSize;

                            }
                            else if (blockName == "Cont")
                            {
                                bytesToWrite = (int)blockSize;
                            }
                            else
                            {
                                stream.Seek(blockSize, SeekOrigin.Current);
                            }

                            if (bytesToWrite < 1 || curHeader == null)
                            {
                                continue;
                            }

                            if (curHeader.AbsPath == null)
                            {
                                string filePath = baseDirectory;

                                // TODO: correctly handle subdirectories.
                                /*
                                if (curHeader.Subdirectory.Length > 0)
                                {
                                    string[] dirArray = curHeader.Subdirectory.Split('\0');
                                    for (int i = 0; i < dirArray.Length; i++)
                                    {
                                        filePath = Path.Combine(filePath, dirArray[i]);
                                    }
                                }
                                */

                                if (curDirectory.Length > 0)
                                {
                                    filePath = Path.Combine(filePath, curDirectory);
                                }

                                Directory.CreateDirectory(filePath);

                                filePath = Path.Combine(filePath, curHeader.Name);

                                while (File.Exists(filePath))
                                {
                                    filePath += "_";
                                    Console.WriteLine("Warning: file already exists (amending name): " + filePath);
                                }

                                curHeader.AbsPath = filePath;
                                Console.WriteLine("--> " + curHeader.AbsPath + ", " + curHeader.DateTime);
                            }

                            using (var f = new FileStream(curHeader.AbsPath, FileMode.Append, FileAccess.Write))
                            {
                                long bytesLeft = bytesToWrite;
                                while (bytesLeft > 0)
                                {
                                    int bytesToRead = bytes.Length;
                                    if (bytesToRead > bytesLeft) { bytesToRead = (int)bytesLeft; }
                                    stream.Read(bytes, 0, bytesToRead);
                                    f.Write(bytes, 0, bytesToRead);
                                    bytesLeft -= bytesToRead;
                                }
                            }

                        }
                    }

                    fileNum++;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
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
            public bool Continuation { get; }

            public string AbsPath = null;

            public FileHeader(Stream stream, int headerSize)
            {
                byte[] bytes = new byte[headerSize];
                stream.Read(bytes, 0, bytes.Length);

                Name = Encoding.ASCII.GetString(bytes, 0x3E, headerSize - 0x3E).Replace("/", "_").Replace("\0", "").Trim();
                Size = (long) QicUtils.Utils.BigEndian(BitConverter.ToUInt64(bytes, 0x16));
                DateTime = QicUtils.Utils.DateTimeFrom1904(QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0xE)));

                Valid = true;
            }
        }

        private class DirectoryHeader
        {
            public string Name { get; }
            public DateTime DateTime { get; }
            public FileAttributes Attributes { get; }
            public bool Valid { get; }

            public DirectoryHeader(Stream stream, int headerSize)
            {
                byte[] bytes = new byte[headerSize];
                stream.Read(bytes, 0, bytes.Length);

                Name = Encoding.ASCII.GetString(bytes, 0x48, headerSize - 0x48).Replace("/", "_").Replace("\0", "").Trim();

                Valid = true;
            }
        }

    }
}

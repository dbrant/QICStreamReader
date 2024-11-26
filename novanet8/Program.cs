using QicUtils;
using System;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;


/// <summary>
/// 
/// Decoder for tape backup images made with a legacy backup utility,
/// identifiable by the presence of the string "F600" in the header(s).
/// 
/// Copyright Dmitry Brant, 2024.
/// 
/// 
/// The structure is composed of blocks, each with a header and a payload.
/// The header is always 0x20 bytes long, and the payload is of variable length.
/// Each block seems to be aligned to a 0x20-byte boundary.
/// All numbers are little-endian.
/// 
/// Header structure:
/// (0x20 bytes total)
/// 
/// Offset      Length      Meaning
/// ----------------------------------------
/// 0           4           Magic string "F600"
/// 4           4           ?
/// 8           4           Size of this block
/// C           4           Type of this block (could be "MHDR", "FBGN", "DATA", etc)
/// 10          10          ?
/// 
/// Structure of a "DATA" block:
/// Offset      Length      Meaning
/// ----------------------------------------
/// 10          4           ?  can be 0, 1, ...
/// 14          8           Absolute offset
/// 
/// 
/// </summary>
namespace novanet8
{
    class Program
    {
        private const string Magic = "F600";


        static void Main(string[] args)
        {
            string inFileName = "";
            string baseDirectory = "out";

            bool dryRun = false;
            byte[] bytes = new byte[0x10000];

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                else if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                else if (args[i] == "--dry") { dryRun = true; }
            }

            string lastBlockName = "";
            long lastDataBlockPos = 0;
            long lastDataBlockOffset = 0;


            var streamStackArray = new Stack<DataObject>[100];
            

            using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);
            while (stream.Position < stream.Length)
            {
                // Make sure we're aligned to a block boundary.
                if ((stream.Position % 0x20) > 0)
                {
                    stream.Seek(0x20 - (stream.Position % 0x20), SeekOrigin.Current);
                }

                var blockHeader = new BlockHeader(stream);
                if (!blockHeader.Valid)
                {
                    if (lastBlockName != "DATA" && blockHeader.magic == "")
                    {
                        // try seeking to the next 4k block
                        if ((stream.Position % 0x1000) > 0)
                        {
                            stream.Seek(0x1000 - (stream.Position % 0x1000), SeekOrigin.Current);
                        }
                        continue;
                    }

                }
                else
                {
                    lastBlockName = blockHeader.Name;
                }

                Console.WriteLine(stream.Position.ToString("X") + ": " + blockHeader.Name + " - " + blockHeader.Size.ToString("X") + " bytes");



                if (streamStackArray[blockHeader.streamIndex] == null)
                    streamStackArray[blockHeader.streamIndex] = new Stack<DataObject>();
                var objectStack = streamStackArray[blockHeader.streamIndex];


                if (blockHeader.Name == "OBGN")
                {
                    // start a new object
                    var obj = new DataObject();
                    objectStack.Push(obj);
                }
                else if (blockHeader.Name == "OEND")
                {
                    // end the current object
                    var obj = objectStack.Pop();
                    obj.Stream?.Flush();
                    obj.Stream?.Close();
                }



                objectStack.TryPeek(out DataObject? currentObj);

                if (blockHeader.Name == "DATA")
                {
                    lastDataBlockPos = stream.Position - 0x20;
                    lastDataBlockOffset = blockHeader.offset;
                    Console.WriteLine("Data stream: type: " + blockHeader.long1.ToString("X") + ", offset: " + blockHeader.offset.ToString("X"));

                    if (blockHeader.Size > 0)
                    {
                        stream.Read(bytes, 0, (int)blockHeader.Size);

                        if (currentObj != null && currentObj.Header == null && blockHeader.Size > 0x80)
                        {
                            var fileHeader = new FileHeader(bytes, (int)blockHeader.Size);

                            if (fileHeader.Valid)
                            {
                                if (currentObj.Header != null)
                                {
                                    Console.WriteLine("Warning: previous stream not closed gracefully: " + currentObj.Header.Name);
                                }
                                currentObj.Stream?.Close();
                                currentObj.Stream = null;

                                currentObj.Header = fileHeader;
                                currentObj.gotDataStart = false;

                                string filePath = baseDirectory;
                                string[] dirArray = fileHeader.Name.Split("\\");
                                string fileName = dirArray[^1];
                                for (int i = 0; i < dirArray.Length - 1; i++)
                                    filePath = Path.Combine(filePath, dirArray[i]);

                                if (currentObj.Header.IsDirectory)
                                {
                                    Console.WriteLine("Directory: " + fileHeader.Name);
                                    if (!dryRun)
                                    {
                                        filePath = Path.Combine(filePath, fileName);
                                        Directory.CreateDirectory(filePath);
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("File: " + fileHeader.Name);
                                    if (!dryRun)
                                    {
                                        Directory.CreateDirectory(filePath);
                                        filePath = Path.Combine(filePath, fileName);

                                        while (File.Exists(filePath))
                                        {
                                            Console.WriteLine("Warning: file already exists (amending name): " + filePath);
                                            filePath += "_";
                                        }
                                        Console.WriteLine(">>> Starting file: " + filePath);

                                        currentObj.Stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                                    }
                                }
                            }
                        }
                        else if (currentObj != null && currentObj.Header != null && blockHeader.long1 == 1 && blockHeader.offset == 0 && !currentObj.gotDataStart && blockHeader.Size >= 0x54)
                        {
                            // first data block of the current stream...
                            currentObj.Header.Size = Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0x48));
                            int numBytes = (int)blockHeader.Size - 0x54;
                            currentObj.Stream?.Write(bytes, 0x54, numBytes);
                            currentObj.currentOutSeq = blockHeader.Size;
                            currentObj.gotDataStart = true;
                        }
                        else if (currentObj != null && currentObj.Header != null && blockHeader.long1 == 1 && blockHeader.offset > 0 && currentObj.gotDataStart)
                        {
                            // data continuation of the current stream...
                            if (blockHeader.offset != currentObj.currentOutSeq)
                            {
                               Console.WriteLine("Warning: data block out of sequence: " + blockHeader.offset.ToString("X") + " (expected: " + currentObj.currentOutSeq.ToString("X") + ")");
                            }
                            currentObj.Stream?.Write(bytes, 0, (int)blockHeader.Size);
                            currentObj.currentOutSeq = blockHeader.offset + blockHeader.Size;
                        }

                    }
                }
                else
                {
                    stream.Seek(blockHeader.Size, SeekOrigin.Current);
                }


            }


            foreach (var objectStack in streamStackArray)
            {
                if (objectStack == null)
                    continue;
                while (objectStack.Count > 0)
                {
                    var obj = objectStack.Pop();
                    obj.Stream?.Flush();
                    obj.Stream?.Close();
                }
            }
        }


        private class DataObject
        {
            public FileHeader? Header { get; set; }
            public FileStream? Stream { get; set; }
            public bool gotDataStart = false;
            public long currentOutSeq = 0;
        }


        private class FileHeader
        {
            public long Size { get; set; }
            public string Name { get; }
            public DateTime CreateDate { get; }
            public DateTime ModifyDate { get; }
            public FileAttributes Attributes { get; }
            public bool Valid { get; }
            public bool IsDirectory { get; }

            public FileHeader(byte[] bytes, int count)
            {
                int bytePtr = 0;

                long long1 = Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0));
                long long2 = Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0x10));
                long long3 = Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0x40));

                if (long1 != 1 || long2 != 4 || long3 != 4)
                {
                    return;
                }

                long nameLen = Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0x48));

                if (nameLen + 0x50 > count)
                {
                    return;
                }

                Name = "";
                bool haveFilePath = false;
                long maxNamePtr = 0x50 + nameLen;
                bytePtr = 0x50;

                while (bytePtr < maxNamePtr)
                {
                    int segSubType = bytes[bytePtr++];
                    int segType = bytes[bytePtr++];
                    bytePtr += 6; // more stuff here?

                    // read string until null
                    int strStart = bytePtr;
                    while (bytePtr < maxNamePtr && bytes[bytePtr] != 0)
                    {
                        bytePtr++;
                    }
                    bytePtr++;
                    string segName = Encoding.Latin1.GetString(bytes, strStart, bytePtr - strStart - 1);

                    if (segType == 1)
                    {
                        // storage server name
                    }
                    else if (segType == 2)
                    {
                        // network
                    }
                    else if (segType == 3)
                    {
                        // node name
                        if (Name.Length > 0) { Name += "\\"; }
                        Name += segName;
                    }
                    else if (segType == 4)
                    {
                        // local path
                        if (segSubType == 0)
                        {
                            // drive letter
                            segName = segName.Replace(":", "");
                        }
                        else if (segSubType == 1)
                        {
                            // directory
                        }
                        else if (segSubType == 2)
                        {
                            // file
                            haveFilePath = true;
                        }
                        if (Name.Length > 0) { Name += "\\"; }
                        Name += segName;
                    }
                }

                if (!haveFilePath)
                    IsDirectory = true;

                Valid = true;
            }
        }

        private class BlockHeader
        {
            public string magic;
            public int streamIndex;
            public long Size;
            public string Name;

            public long long1;
            public long offset;

            public bool Valid = false;

            public BlockHeader(Stream stream)
            {
                byte[] bytes = new byte[0x1000];
                stream.Read(bytes, 0, 0x20);

                int bytePtr = 0;

                magic = Utils.CleanString(Encoding.ASCII.GetString(bytes, bytePtr, 4)).Trim(); bytePtr += 4;
                if (magic != Magic)
                {
                    Console.WriteLine("Warning: invalid block header.");
                    return;
                }

                streamIndex = (int)Utils.LittleEndian(BitConverter.ToUInt32(bytes, bytePtr)); bytePtr += 4;

                Size = Utils.LittleEndian(BitConverter.ToUInt32(bytes, bytePtr)); bytePtr += 4;
                Name = Encoding.ASCII.GetString(bytes, bytePtr, 4); bytePtr += 4;

                long1 = Utils.LittleEndian(BitConverter.ToUInt32(bytes, bytePtr)); bytePtr += 4;
                offset = Utils.LittleEndian(BitConverter.ToUInt32(bytes, bytePtr)); bytePtr += 4;

                // unknown
                bytePtr += 0x8;

                Valid = true;
            }
        }


    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;

/// <summary>
/// 
/// Decoder for tape images written using the SAVLIB command on AS/400 systems.
/// 
/// </summary>
namespace xenixold
{
    class Program
    {



        static void Main(string[] args)
        {
            string inFileName = "0.bin";
            string outFileName = "out.bin";
            string baseDirectory = "out";
            long initialOffset = 0;
            var tempBytes = new byte[0x10000];

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                else if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                else if (args[i] == "-o") { outFileName = args[i + 1]; }
                else if (args[i] == "--offset") { initialOffset = Convert.ToInt64(args[i + 1]); }
            }

            try
            {
                using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                {
                    if (initialOffset > 0)
                    {
                        stream.Seek(initialOffset, SeekOrigin.Begin);
                    }

                    var partition = new XenixPartition(stream, baseDirectory);

                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }


        class XenixPartition
        {
            public const int partitionStart = 0xD04400; // 0x2400; // <-- considered zone 0
            public const int zoneSize = 0x400;

            //public List<int> inodeTableOffsets = new List<int>() { 0x3000, 0x303000, 0x603000 };
            //public List<int> inodeTableCounts = new List<int>() { 0x5e0, 0x5e0, 0x5e0 };

            public List<int> inodeTableOffsets = new List<int>() { 0xD05000, 0x102E800, 0x1358000, 0x1681800, 0x19AB000, 0x1CD4800, 0x1FFE000 };
            public List<int> inodeTableCounts = new List<int>() { 0x630, 0x630, 0x630, 0x630, 0x630, 0x630, 0x620 };

            public Dictionary<int, INode> inodeCache = new Dictionary<int, INode>();

            private Stream stream;
            private string baseDirectory;


            public XenixPartition(Stream stream, string baseDirectory)
            {
                this.stream = stream;
                this.baseDirectory = baseDirectory;

                var rootNode = ReadINode(stream, 2);

                FillChildren(stream, rootNode, 0);

                UnpackFiles(rootNode, baseDirectory);

            }

            void UnpackFiles(INode parentNode, string curPath)
            {
                foreach (var node in parentNode.Children)
                {
                    if (node.IsDirectory)
                    {
                        UnpackFiles(node, Path.Combine(curPath, node.Name));
                        continue;
                    }


                    //c215g.c

                    //byte[] bytes = ReadContents(stream, node);

                    //if (node.Name == "c215g.c")
                    {
                        Directory.CreateDirectory(curPath);

                        string filePath = Path.Combine(curPath, node.Name);

                        while (File.Exists(filePath))
                        {
                            Console.WriteLine("Warning: file already exists (amending name): " + filePath);
                            filePath += "_";
                        }

                        Console.WriteLine(stream.Position.ToString("X") + ": " + filePath + " - "
                        + node.Size.ToString() + " bytes - " + node.ModifyTime.ToShortDateString());

                        using (var f = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                        {
                            byte[] bytes = ReadContents(stream, node);
                            f.Write(bytes);
                            f.Flush();
                        }

                        File.SetCreationTime(filePath, node.CreateTime);
                        File.SetLastWriteTime(filePath, node.ModifyTime);
                    }
                }
            }



            INode ReadINode(Stream stream, int num)
            {
                if (inodeCache.ContainsKey(num)) return inodeCache[num];

                long offset = 0;

                int actualNum = num;

                for (int i = 0; i < inodeTableCounts.Count; i++)
                {
                    if (actualNum < inodeTableCounts[i])
                    {
                        offset = inodeTableOffsets[i];
                        break;
                    }
                    actualNum -= inodeTableCounts[i];
                }

                if (offset == 0)
                {
                    Console.WriteLine(">> Warning: inode not found within tables.");
                }

                offset = offset + ((actualNum - 1) * INode.StructLength);
                stream.Seek(offset, SeekOrigin.Begin);
                var inode = new INode(num, stream, partitionStart);
                inodeCache[num] = inode;
                return inode;
            }

            void FillChildren(Stream stream, INode parentNode, int level)
            {
                if (level > 64)
                {
                    throw new ApplicationException("descending too far, possibly circular reference.");
                }
                var contents = ReadContents(stream, parentNode);

                int numDirEntries = (int)contents.Length / 0x10;

                for (int i = 0; i < numDirEntries; i++)
                {
                    var iNodeNum = BitConverter.ToUInt16(contents, i * 0x10);

                    if (iNodeNum == 0)
                        continue; // free inode.

                    var name = QicUtils.Utils.ReplaceInvalidChars(QicUtils.Utils.CleanString(Encoding.ASCII.GetString(contents, i * 0x10 + 2, 0x10 - 2)));
                    if (name == "." || name == "..")
                        continue;

                    var inode = ReadINode(stream, iNodeNum);
                    inode.Name = name;

                    parentNode.Children.Add(inode);

                    Console.WriteLine(">> " + inode.Mode.ToString("X4") + "\t\t" + inode.Name);

                    if (inode.IsDirectory)
                    {
                        FillChildren(stream, inode, level + 1);
                    }
                }
            }

            byte[] ReadContents(Stream stream, INode inode)
            {
                if (inode.Size > 0x10000000)
                {
                    throw new ApplicationException("inode size seems a bit too large.");
                }

                byte[] bytes = new byte[inode.Size];
                int bytesRead = 0;

                for (int z = 0; z < inode.Zones.Count; z++)
                {
                    long offset = partitionStart + (inode.Zones[z]) * zoneSize;

                    stream.Seek(offset, SeekOrigin.Begin);

                    int bytesToRead = zoneSize;
                    int bytesLeft = (int)inode.Size - bytesRead;
                    if (bytesToRead > bytesLeft)
                    {
                        bytesToRead = bytesLeft;
                    }

                    stream.Read(bytes, bytesRead, bytesToRead);
                    bytesRead += bytesToRead;

                    if (bytesLeft <= 0)
                        break;
                }

                return bytes;
            }
        }



        private class INode
        {
            public const int StructLength = 0x40;

            public int id;
            public int Mode;
            public int nLink;
            public int uId;
            public int gId;
            public long Size { get; }
            public List<int> Zones = new List<int>();
            public DateTime AccessTime { get; }
            public DateTime ModifyTime { get; }
            public DateTime CreateTime { get; }

            public bool IsDirectory { get { return (Mode & 0x4000) != 0; } }

            // Gets filled in lazily when directories are parsed:
            public string Name = "";
            public List<INode> Children = new List<INode>();

            public INode(int id, Stream stream, long partitionStart)
            {
                this.id = id;
                byte[] bytes = new byte[StructLength];
                long initialPos = stream.Position;

                stream.Read(bytes, 0, bytes.Length);

                int bytePtr = 0;
                Mode = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                nLink = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                uId = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                gId = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                Size = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;

                // direct blocks
                int zone;
                for (int i = 0; i < 10; i++)
                {
                    zone = bytes[bytePtr++];
                    zone |= (bytes[bytePtr++] << 8);
                    zone |= (bytes[bytePtr++] << 16);
                    if (zone != 0)
                    {
                        Zones.Add(zone);
                    }
                }

                // indirect blocks
                int zonePtr = bytes[bytePtr++];
                zonePtr |= (bytes[bytePtr++] << 8);
                zonePtr |= (bytes[bytePtr++] << 16);
                if (zonePtr != 0)
                {
                    byte[] blockBytes = new byte[XenixPartition.zoneSize];
                    stream.Seek(partitionStart + (zonePtr * XenixPartition.zoneSize), SeekOrigin.Begin);
                    stream.Read(blockBytes, 0, blockBytes.Length);
                    for (int i = 0; i < blockBytes.Length / 4; i++)
                    {
                        zone = (int)BitConverter.ToUInt32(blockBytes, i * 4);
                        if (zone <= 0)
                            break;

                        Zones.Add(zone);
                    }
                }

                //TODO: handle double and triple indirect blocks?
                bytePtr += 3;
                bytePtr += 3;

                // align to 16 bits.
                bytePtr++;


                AccessTime = QicUtils.Utils.DateTimeFromTimeT(BitConverter.ToUInt32(bytes, bytePtr)); bytePtr += 4;
                ModifyTime = QicUtils.Utils.DateTimeFromTimeT(BitConverter.ToUInt32(bytes, bytePtr)); bytePtr += 4;
                CreateTime = QicUtils.Utils.DateTimeFromTimeT(BitConverter.ToUInt32(bytes, bytePtr)); bytePtr += 4;
            }
        }
    }
}

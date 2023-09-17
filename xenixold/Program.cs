using System;
using System.Collections.Generic;
using System.IO;
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
            string baseDirectory = "out";
            long initialOffset = 0;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                else if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                else if (args[i] == "--offset") { initialOffset = Convert.ToInt64(args[i + 1]); }
            }

            try
            {
                using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                {
                    var partition = new XenixPartition(stream, initialOffset, baseDirectory);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }


        class XenixPartition
        {
            private long partitionBaseOffset;
            public const int zoneSize = 0x400;

            public Dictionary<int, INode> inodeCache = new Dictionary<int, INode>();

            private Stream stream;
            private string baseDirectory;

            private SuperBlock superBlock;
            private List<CylinderGroup> cylinderGroups = new List<CylinderGroup>();

            private byte[] zoneBytes = new byte[zoneSize];


            public XenixPartition(Stream stream, long baseOffset, string baseDirectory)
            {
                this.stream = stream;
                this.partitionBaseOffset = baseOffset;
                this.baseDirectory = baseDirectory;

                ReadZone(1, zoneBytes);
                superBlock = new SuperBlock(zoneBytes);

                for (int i = 0; i < superBlock.numCylingerGroups; i++)
                {
                    ReadZone(2 + (i * superBlock.blocksPerCylinderGroup), zoneBytes);
                    var cg = new CylinderGroup(zoneBytes);
                    cylinderGroups.Add(cg);
                }



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


            void ReadZone(int num, byte[] bytes, int bytesOffset = 0, int count = zoneSize)
            {
                stream.Seek(partitionBaseOffset + (num * zoneSize), SeekOrigin.Begin);
                stream.Read(bytes, bytesOffset, count);
            }

            INode ReadINode(Stream stream, int num)
            {
                if (inodeCache.ContainsKey(num)) return inodeCache[num];

                int actualNum = num - 1;

                // in which cylinder group will this inode be found?
                int cg = actualNum / superBlock.inodesPerCylinderGroup;
                int modNum = actualNum % superBlock.inodesPerCylinderGroup;

                if (cg >= cylinderGroups.Count)
                {
                    Console.WriteLine(">> Warning: inode not found within groups.");
                }

                long offset = partitionBaseOffset + (cylinderGroups[cg].firstINodeBlockOffset * zoneSize) + (modNum * INode.StructLength);
                stream.Seek(offset, SeekOrigin.Begin);

                var inode = new INode(num, stream, partitionBaseOffset);
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

                    // Console.WriteLine(">> " + inode.Mode.ToString("X4") + "\t\t" + inode.Name);

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
                    int bytesToRead = zoneSize;
                    int bytesLeft = (int)inode.Size - bytesRead;
                    if (bytesToRead > bytesLeft)
                    {
                        bytesToRead = bytesLeft;
                    }

                    ReadZone(inode.Zones[z], bytes, bytesRead, bytesToRead);

                    bytesRead += bytesToRead;

                    if (bytesLeft <= 0)
                        break;
                }

                return bytes;
            }
        }


        private class SuperBlock
        {
            public string Name;
            public string PackName;
            public int totalDataBlocks;
            public int blocksPerCylinderGroup;
            public int maxBlock;
            public int inodesPerCylinderGroup;
            public int maxINumber;
            public DateTime lastModifiedTime;
            public int modifiedFlag;
            public int readOnlyFlag;
            public int cleanUnmount;
            public int typeAndVersion;
            public int containsFNewCg;
            public int containsSNewCg;
            public int numFreeDataBlocks;
            public int numFreeINodes;
            public int numDirectories;
            public int nativeExtentSize;
            public int numCylingerGroups;
            public int nextCylinderGroupToSearch;

            public SuperBlock(byte[] bytes)
            {
                int bytePtr = 0;
                Name = QicUtils.Utils.CleanString(Encoding.ASCII.GetString(bytes, bytePtr, 6)); bytePtr += 6;
                PackName = QicUtils.Utils.CleanString(Encoding.ASCII.GetString(bytes, bytePtr, 6)); bytePtr += 6;
                totalDataBlocks = (int)BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                blocksPerCylinderGroup = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                maxBlock = (int)BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                inodesPerCylinderGroup = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                maxINumber = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                lastModifiedTime = QicUtils.Utils.DateTimeFromTimeT(BitConverter.ToUInt32(bytes, bytePtr)); bytePtr += 4;
                modifiedFlag = bytes[bytePtr++];
                readOnlyFlag = bytes[bytePtr++];
                cleanUnmount = bytes[bytePtr++];
                typeAndVersion = bytes[bytePtr++];
                containsFNewCg = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                containsSNewCg = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                numFreeDataBlocks = (int)BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                numFreeINodes = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                numDirectories = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                nativeExtentSize = bytes[bytePtr++];
                numCylingerGroups = bytes[bytePtr++];
                nextCylinderGroupToSearch = bytes[bytePtr++];
                // ignore global policy information
            }
        }

        private class CylinderGroup
        {
            public int firstDataBlockOffset;
            public int firstINodeBlockOffset;
            public int totalDataBlocks;
            public int nextFreeINode;
            public int sequenceNum;
            public int curExtent;
            public int lowAt;
            public int highAt;
            public int nextBlockForAlloc;
            public int iLock;

            public CylinderGroup(byte[] bytes)
            {
                int bytePtr = 0;
                firstDataBlockOffset = (int)BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                firstINodeBlockOffset = (int)BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                totalDataBlocks = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                nextFreeINode = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                sequenceNum = bytes[bytePtr++];
                curExtent = bytes[bytePtr++];
                lowAt = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                highAt = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                nextBlockForAlloc = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                iLock = bytes[bytePtr++];

                // ignore allocation bitmap
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

            public INode(int id, Stream stream, long partitionBaseOffset)
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
                    stream.Seek(partitionBaseOffset + (zonePtr * XenixPartition.zoneSize), SeekOrigin.Begin);
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

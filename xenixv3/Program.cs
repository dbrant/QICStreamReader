using QicUtils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Decoder/extractor of extremely old Xenix filesystems.
/// 
/// 
/// Partition offsets observed so far:
/// 0x2B0000
/// 
/// Special notes:
/// In the version of a Xenix dump that I've seen in the wild (from an Altos system),
/// "long" integer values are stored in a weird byte order: 2 3 0 1.  In other words,
/// the system is little-endian with respect to 16-bit words, but big-endian (?) with
/// respect to 32-bit integers - the high word comes first, followed by the low word.
/// 
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
                    var partition = new XenixPartition(stream, initialOffset);
                    partition.UnpackFiles(baseDirectory);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }


        private class XenixPartition
        {
            public const int BLOCK_SIZE = 0x200;
            public const int SUPERBLOCK_BLOCK = 1;
            public const int INODES_BLOCK = 2;

            private Stream stream;
            private long partitionBaseOffset;
            private SuperBlock superBlock;
            private INode rootNode;

            private Dictionary<int, INode> inodeCache = new();

            public XenixPartition(Stream stream, long baseOffset)
            {
                this.stream = stream;
                this.partitionBaseOffset = baseOffset;

                byte[] blockBytes = new byte[BLOCK_SIZE];

                ReadBlock(SUPERBLOCK_BLOCK, blockBytes);
                superBlock = new SuperBlock(blockBytes);

                rootNode = ReadINode(stream, 2);
                FillChildren(stream, rootNode, 0);
            }

            public void UnpackFiles(string basePath)
            {
                UnpackFiles(rootNode, basePath);
            }

            private void UnpackFiles(INode parentNode, string curPath)
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
                        byte[] bytes = ReadContents(node);
                        f.Write(bytes);
                        f.Flush();
                    }

                    File.SetCreationTime(filePath, node.CreateTime);
                    File.SetLastWriteTime(filePath, node.ModifyTime);
                }
            }


            private void ReadBlock(int num, byte[] bytes, int bytesOffset = 0, int count = BLOCK_SIZE)
            {
                stream.Seek(partitionBaseOffset + (num * BLOCK_SIZE), SeekOrigin.Begin);
                stream.Read(bytes, bytesOffset, count);
            }

            private INode ReadINode(Stream stream, int num)
            {
                if (inodeCache.ContainsKey(num)) return inodeCache[num];

                int actualNum = num - 1;

                long offset = partitionBaseOffset + (INODES_BLOCK * BLOCK_SIZE) + (actualNum * INode.StructLength);
                stream.Seek(offset, SeekOrigin.Begin);

                var inode = new INode(num, stream, partitionBaseOffset);
                inodeCache[num] = inode;
                return inode;
            }

            private void FillChildren(Stream stream, INode parentNode, int level)
            {
                if (level > 64)
                {
                    throw new ApplicationException("descending too far, possibly circular reference.");
                }
                var contents = ReadContents(parentNode);

                int numDirEntries = (int)contents.Length / 0x10;

                for (int i = 0; i < numDirEntries; i++)
                {
                    var iNodeNum = BitConverter.ToUInt16(contents, i * 0x10);

                    if (iNodeNum == 0)
                        continue; // free inode.

                    var name = Utils.ReplaceInvalidChars(QicUtils.Utils.CleanString(Encoding.ASCII.GetString(contents, i * 0x10 + 2, 0x10 - 2)));
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

            private byte[] ReadContents(INode inode)
            {
                if (inode.Size > 0x10000000)
                {
                    throw new ApplicationException("inode size seems a bit too large.");
                }

                byte[] bytes = new byte[inode.Size];
                int bytesRead = 0;

                for (int z = 0; z < inode.Blocks.Count; z++)
                {
                    int bytesToRead = BLOCK_SIZE;
                    int bytesLeft = (int)inode.Size - bytesRead;
                    if (bytesToRead > bytesLeft)
                    {
                        bytesToRead = bytesLeft;
                    }

                    ReadBlock(inode.Blocks[z], bytes, bytesRead, bytesToRead);

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
                totalDataBlocks = (int)Utils.XenixLong(bytes, bytePtr); bytePtr += 4;
                blocksPerCylinderGroup = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                maxBlock = (int)Utils.XenixLong(bytes, bytePtr); bytePtr += 4;
                inodesPerCylinderGroup = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                maxINumber = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                lastModifiedTime = QicUtils.Utils.DateTimeFromTimeT(Utils.XenixLong(bytes, bytePtr)); bytePtr += 4;
                modifiedFlag = bytes[bytePtr++];
                readOnlyFlag = bytes[bytePtr++];
                cleanUnmount = bytes[bytePtr++];
                typeAndVersion = bytes[bytePtr++];
                containsFNewCg = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                containsSNewCg = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                numFreeDataBlocks = (int)Utils.XenixLong(bytes, bytePtr); bytePtr += 4;
                numFreeINodes = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                numDirectories = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                nativeExtentSize = bytes[bytePtr++];
                numCylingerGroups = bytes[bytePtr++];
                nextCylinderGroupToSearch = bytes[bytePtr++];
                // ignore global policy information
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
            public List<int> Blocks = new();
            public DateTime AccessTime { get; }
            public DateTime ModifyTime { get; }
            public DateTime CreateTime { get; }

            public bool IsDirectory { get { return (Mode & 0x4000) != 0; } }

            // Gets filled in lazily when directories are parsed:
            public string Name = "";
            public List<INode> Children = new();

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

                Size = Utils.XenixLong(bytes, bytePtr); bytePtr += 4;

                // direct blocks
                int block;
                for (int i = 0; i < 10; i++)
                {
                    block = (bytes[bytePtr++] << 16);
                    block |= bytes[bytePtr++];
                    block |= (bytes[bytePtr++] << 8);
                    if (block != 0)
                    {
                        Blocks.Add(block);
                    }
                }

                // indirect blocks
                int blockPtr = (bytes[bytePtr++] << 16);
                blockPtr |= bytes[bytePtr++];
                blockPtr |= (bytes[bytePtr++] << 8);
                if (blockPtr != 0)
                {
                    byte[] blockBytes = new byte[XenixPartition.BLOCK_SIZE];
                    stream.Seek(partitionBaseOffset + (blockPtr * XenixPartition.BLOCK_SIZE), SeekOrigin.Begin);
                    stream.Read(blockBytes, 0, blockBytes.Length);
                    for (int i = 0; i < blockBytes.Length / 4; i++)
                    {
                        block = (int)Utils.XenixLong(blockBytes, i * 4);
                        if (block <= 0)
                            break;

                        Blocks.Add(block);
                    }
                }

                blockPtr = (bytes[bytePtr++] << 16);
                blockPtr |= bytes[bytePtr++];
                blockPtr |= (bytes[bytePtr++] << 8);
                if (blockPtr != 0)
                {
                    byte[] iBlockBytes = new byte[XenixPartition.BLOCK_SIZE];
                    stream.Seek(partitionBaseOffset + (blockPtr * XenixPartition.BLOCK_SIZE), SeekOrigin.Begin);
                    stream.Read(iBlockBytes, 0, iBlockBytes.Length);
                    for (int b = 0; b < iBlockBytes.Length / 4; b++)
                    {
                        var iblock = (int)Utils.XenixLong(iBlockBytes, b * 4);
                        if (iblock <= 0)
                            break;

                        byte[] blockBytes = new byte[XenixPartition.BLOCK_SIZE];
                        stream.Seek(partitionBaseOffset + (iblock * XenixPartition.BLOCK_SIZE), SeekOrigin.Begin);
                        stream.Read(blockBytes, 0, blockBytes.Length);
                        for (int i = 0; i < blockBytes.Length / 4; i++)
                        {
                            block = (int)Utils.XenixLong(blockBytes, i * 4);
                            if (block <= 0)
                                break;

                            Blocks.Add(block);
                        }
                    }
                }

                blockPtr = (bytes[bytePtr++] << 16);
                blockPtr |= bytes[bytePtr++];
                blockPtr |= (bytes[bytePtr++] << 8);
                if (blockPtr != 0)
                {
                    Console.WriteLine("FIXME: Add support for triple indirect blocks.");
                }

                int sizeBasedOnBlocks = Blocks.Count * XenixPartition.BLOCK_SIZE;
                if (sizeBasedOnBlocks > (Size + XenixPartition.BLOCK_SIZE))
                {
                    Console.WriteLine("Warning: Total size of blocks seems very different from inode file size.");
                }

                // align to 16 bits.
                if (bytePtr % 2 != 0) bytePtr++;

                AccessTime = QicUtils.Utils.DateTimeFromTimeT(Utils.XenixLong(bytes, bytePtr)); bytePtr += 4;
                ModifyTime = QicUtils.Utils.DateTimeFromTimeT(Utils.XenixLong(bytes, bytePtr)); bytePtr += 4;
                CreateTime = QicUtils.Utils.DateTimeFromTimeT(Utils.XenixLong(bytes, bytePtr)); bytePtr += 4;

            }
        }
    }
}

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
namespace xenixv3
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
                    var partition = new XenixPartition(stream, initialOffset, PartitionEndianness.Little);
                    partition.UnpackFiles(baseDirectory);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }


        private enum PartitionEndianness
        {
            Little, Big, Pdp11
        }

        private static ushort GetUInt16(byte[] bytes, int offset, PartitionEndianness endianness)
        {
            if (endianness == PartitionEndianness.Big)
            {
                return Utils.BigEndian(BitConverter.ToUInt16(bytes, offset));
            }
            return Utils.LittleEndian(BitConverter.ToUInt16(bytes, offset));
        }

        private static uint GetUInt32(byte[] bytes, int offset, PartitionEndianness endianness)
        {
            if (endianness == PartitionEndianness.Big)
            {
                return Utils.BigEndian(BitConverter.ToUInt32(bytes, offset));
            }
            else if (endianness == PartitionEndianness.Pdp11)
            {
                return Utils.Pdp11EndianInt(bytes, offset);
            }
            return Utils.LittleEndian(BitConverter.ToUInt32(bytes, offset));
        }

        private static int Get3ByteInt(byte[] bytes, int offset, PartitionEndianness endianness)
        {
            int n;
            if (endianness == PartitionEndianness.Big)
            {
                n = (bytes[offset++] << 16);
                n |= (bytes[offset++] << 8);
                n |= bytes[offset++];
            }
            else if (endianness == PartitionEndianness.Pdp11)
            {
                n = (bytes[offset++] << 16);
                n |= bytes[offset++];
                n |= (bytes[offset++] << 8);
            }
            else
            {
                n = bytes[offset++];
                n |= (bytes[offset++] << 8);
                n |= (bytes[offset++] << 16);
            }
            return n;
        }

        private class XenixPartition
        {
            public const int BLOCK_SIZE_DEFAULT = 0x400;
            public const int SUPERBLOCK_BLOCK = 1;
            public const int INODES_BLOCK = 2;
            
            public PartitionEndianness endianness;
            private Stream stream;
            public long partitionBaseOffset;
            public SuperBlock superBlock;
            private INode rootNode;

            private Dictionary<int, INode> inodeCache = new();

            public XenixPartition(Stream stream, long baseOffset, PartitionEndianness endianness)
            {
                this.stream = stream;
                this.partitionBaseOffset = baseOffset;
                this.endianness = endianness;

                byte[] blockBytes = new byte[BLOCK_SIZE_DEFAULT];

                ReadBlock(SUPERBLOCK_BLOCK, blockBytes, 0, blockBytes.Length);
                superBlock = new SuperBlock(blockBytes, this);

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


            private void ReadBlock(int num, byte[] bytes, int bytesOffset, int count)
            {
                int blockSize = superBlock != null ? superBlock.BlockSize : BLOCK_SIZE_DEFAULT;
                stream.Seek(partitionBaseOffset + (num * blockSize), SeekOrigin.Begin);
                stream.Read(bytes, bytesOffset, count);
            }

            private INode ReadINode(Stream stream, int num)
            {
                if (inodeCache.ContainsKey(num)) return inodeCache[num];

                int actualNum = num - 1;

                long offset = partitionBaseOffset + (INODES_BLOCK * superBlock.BlockSize) + (actualNum * INode.StructLength);
                stream.Seek(offset, SeekOrigin.Begin);

                var inode = new INode(num, stream, this);
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
                    var iNodeNum = GetUInt16(contents, i * 0x10, endianness);

                    if (iNodeNum == 0)
                        continue; // free inode.

                    var name = Utils.ReplaceInvalidChars(Utils.CleanString(Encoding.ASCII.GetString(contents, i * 0x10 + 2, 0x10 - 2)));
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
                    int bytesToRead = superBlock.BlockSize;
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
            public int BlockSize = XenixPartition.BLOCK_SIZE_DEFAULT;

            public string Name;
            public string PackName;

            public int totalINodeBlocks;
            public int totalVolumeBlocks;
            public DateTime lastModifiedTime;
            public int numFreeDataBlocks;
            public int numFreeINodes;

            public int clean;
            public int magic;
            public int fsType;

            public SuperBlock(byte[] bytes, XenixPartition partition)
            {
                int bytePtr = 0;

                totalINodeBlocks = GetUInt16(bytes, bytePtr, partition.endianness); bytePtr += 2;
                totalVolumeBlocks = (int)GetUInt32(bytes, bytePtr, partition.endianness); bytePtr += 4;

                // ...free block list...
                // ...free inode list...

                bytePtr = 0x19E;
                lastModifiedTime = Utils.DateTimeFromTimeT(GetUInt32(bytes, bytePtr, partition.endianness)); bytePtr += 4;
                numFreeDataBlocks = (int)GetUInt32(bytes, bytePtr, partition.endianness); bytePtr += 4;
                numFreeINodes = GetUInt16(bytes, bytePtr, partition.endianness); bytePtr += 2;

                // ...device information...

                bytePtr = 0x1B0;
                Name = Utils.CleanString(Encoding.ASCII.GetString(bytes, bytePtr, 6)); bytePtr += 6;
                PackName = Utils.CleanString(Encoding.ASCII.GetString(bytes, bytePtr, 6)); bytePtr += 6;
                clean = bytes[bytePtr++];

                bytePtr = bytes.Length - 8;
                magic = (int)GetUInt32(bytes, bytePtr, partition.endianness); bytePtr += 4;
                fsType = (int)GetUInt32(bytes, bytePtr, partition.endianness); bytePtr += 4;

                if (fsType == 1)
                    BlockSize = 0x200;
                else if (fsType == 2)
                    BlockSize = 0x400;
                else
                {
                    Console.WriteLine("Warning: Unrecognized s_type. Using default block size.");
                }
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

            public INode(int id, Stream stream, XenixPartition partition)
            {
                this.id = id;
                byte[] bytes = new byte[StructLength];

                stream.Read(bytes, 0, bytes.Length);

                int bytePtr = 0;
                Mode = GetUInt16(bytes, bytePtr, partition.endianness); bytePtr += 2;
                nLink = GetUInt16(bytes, bytePtr, partition.endianness); bytePtr += 2;
                uId = GetUInt16(bytes, bytePtr, partition.endianness); bytePtr += 2;
                gId = GetUInt16(bytes, bytePtr, partition.endianness); bytePtr += 2;

                Size = GetUInt32(bytes, bytePtr, partition.endianness); bytePtr += 4;

                // direct blocks
                int block;
                for (int i = 0; i < 10; i++)
                {
                    block = Get3ByteInt(bytes, bytePtr, partition.endianness); bytePtr += 3;
                    if (block != 0)
                    {
                        Blocks.Add(block);
                    }
                }

                // indirect blocks
                int blockPtr = Get3ByteInt(bytes, bytePtr, partition.endianness); bytePtr += 3;
                if (blockPtr != 0)
                {
                    byte[] blockBytes = new byte[partition.superBlock.BlockSize];
                    stream.Seek(partition.partitionBaseOffset + (blockPtr * partition.superBlock.BlockSize), SeekOrigin.Begin);
                    stream.Read(blockBytes, 0, blockBytes.Length);
                    for (int i = 0; i < blockBytes.Length / 4; i++)
                    {
                        block = (int)GetUInt32(blockBytes, i * 4, partition.endianness);
                        if (block <= 0)
                            break;

                        Blocks.Add(block);
                    }
                }

                // double-indirect blocks
                blockPtr = Get3ByteInt(bytes, bytePtr, partition.endianness); bytePtr += 3;
                if (blockPtr != 0)
                {
                    byte[] iBlockBytes = new byte[partition.superBlock.BlockSize];
                    stream.Seek(partition.partitionBaseOffset + (blockPtr * partition.superBlock.BlockSize), SeekOrigin.Begin);
                    stream.Read(iBlockBytes, 0, iBlockBytes.Length);
                    for (int b = 0; b < iBlockBytes.Length / 4; b++)
                    {
                        var iblock = (int)GetUInt32(iBlockBytes, b * 4, partition.endianness);
                        if (iblock <= 0)
                            break;

                        byte[] blockBytes = new byte[partition.superBlock.BlockSize];
                        stream.Seek(partition.partitionBaseOffset + (iblock * partition.superBlock.BlockSize), SeekOrigin.Begin);
                        stream.Read(blockBytes, 0, blockBytes.Length);
                        for (int i = 0; i < blockBytes.Length / 4; i++)
                        {
                            block = (int)GetUInt32(blockBytes, i * 4, partition.endianness);
                            if (block <= 0)
                                break;

                            Blocks.Add(block);
                        }
                    }
                }

                blockPtr = Get3ByteInt(bytes, bytePtr, partition.endianness); bytePtr += 3;
                if (blockPtr != 0)
                {
                    Console.WriteLine("FIXME: Add support for triple indirect blocks.");
                }

                int sizeBasedOnBlocks = Blocks.Count * partition.superBlock.BlockSize;
                if (sizeBasedOnBlocks > (Size + partition.superBlock.BlockSize))
                {
                    Console.WriteLine("Warning: Total size of blocks seems very different from inode file size.");
                }

                // align to 16 bits.
                if (bytePtr % 2 != 0) bytePtr++;

                AccessTime = Utils.DateTimeFromTimeT(GetUInt32(bytes, bytePtr, partition.endianness)); bytePtr += 4;
                ModifyTime = Utils.DateTimeFromTimeT(GetUInt32(bytes, bytePtr, partition.endianness)); bytePtr += 4;
                CreateTime = Utils.DateTimeFromTimeT(GetUInt32(bytes, bytePtr, partition.endianness)); bytePtr += 4;

            }
        }
    }
}

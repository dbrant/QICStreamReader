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
/// Apparently this is a real thing called "middle-endian" or "PDP-11-endian":
/// https://en.wikipedia.org/wiki/Endianness#Middle-endian
/// 
/// 
/// </summary>
namespace xenixv3
{
    class Program
    {
        static void Main(string[] args)
        {
            var argMap = new ArgMap(args);

            var inFileName = argMap.Get("-f");
            var baseDirectory = argMap.Get("-d", "out");
            long initialOffset = Utils.StringOrHexToLong(argMap.Get("--offset", "0"));
            Endianness endianness = argMap.Get("--endianness", "little") switch
            {
                "little" => Endianness.Little,
                "big" => Endianness.Big,
                "pdp11" => Endianness.Pdp11,
                _ => throw new DecodeException("Unrecognized endianness."),
            };

            using var stream = new FileStream(inFileName!, FileMode.Open, FileAccess.Read);
            var partition = new XenixPartition(stream, initialOffset, endianness);
            partition.UnpackFiles(baseDirectory);
        }

        private class XenixPartition
        {
            public const int BLOCK_SIZE_DEFAULT = 0x400;
            public const int SUPERBLOCK_BLOCK = 1;
            public const int INODES_BLOCK = 2;
            
            public Endianness endianness;
            private Stream stream;
            public long partitionBaseOffset;
            public SuperBlock superBlock;
            private INode rootNode;

            private Dictionary<int, INode> inodeCache = new();

            public XenixPartition(Stream stream, long baseOffset, Endianness endianness)
            {
                this.stream = stream;
                this.partitionBaseOffset = baseOffset;
                this.endianness = endianness;

                byte[] blockBytes = new byte[BLOCK_SIZE_DEFAULT];

                ReadBlock(SUPERBLOCK_BLOCK, blockBytes, 0, blockBytes.Length);
                superBlock = new SuperBlock(blockBytes, this);

                rootNode = ReadINode(stream, 2);
                //FillChildren(stream, rootNode, 0, new List<int>());



                SearchDirectoriesHeuristically();

            }





            private void SearchDirectoriesHeuristically()
            {
                int blockSize = superBlock != null ? superBlock.BlockSize : BLOCK_SIZE_DEFAULT;
                int totalBlocks = (int)((stream.Length - partitionBaseOffset) / blockSize);
                byte[] blockBytes = new byte[blockSize];
                int unknownCount = 0;

                var orphans = new List<INode>() { rootNode };


                for (int b = 0; b < totalBlocks; b++)
                {
                    ReadBlock(b, blockBytes, 0, blockBytes.Length);

                    if (blockBytes[2] == 0x2E && blockBytes[3] == 0 && blockBytes[4] == 0 && blockBytes[5] == 0 && blockBytes[6] == 0 && blockBytes[7] == 0 && blockBytes[8] == 0 && blockBytes[9] == 0 &&
                        blockBytes[0x12] == 0x2E && blockBytes[0x13] == 0x2E && blockBytes[0x14] == 0 && blockBytes[0x15] == 0 && blockBytes[0x16] == 0 && blockBytes[0x17] == 0 && blockBytes[0x18] == 0 && blockBytes[0x19] == 0)
                    {
                    }
                    else
                    {
                        continue;
                    }

                    int selfInode = Utils.GetUInt16(blockBytes, 0, endianness);
                    int parentInode = Utils.GetUInt16(blockBytes, 0x10, endianness);

                    if (selfInode == 0 || parentInode == 0)
                        continue;

                    var selfNode = ReadINode(stream, selfInode);
                    selfNode.parentId = parentInode;
                    if (selfNode.id == 2)
                        selfNode = rootNode;
                    else
                    {

                        var foundSelf = FindInodeInTree(orphans, selfNode.id);
                        if (foundSelf != null)
                        {
                            selfNode = foundSelf;
                        }
                        else
                        {
                            selfNode.Name = "unknown" + unknownCount++;

                            var parent = FindInodeInTree(orphans, selfNode.parentId);
                            if (parent != null)
                            {
                                parent.Children.Add(selfNode);
                            }
                            else
                            {
                                orphans.Add(selfNode);
                            }
                        }
                    }

                    int bytePtr = 0x20;
                    while (bytePtr < blockBytes.Length)
                    {
                        int inodeNum = Utils.GetUInt16(blockBytes, bytePtr, endianness); bytePtr += 2;
                        string name = Utils.CleanString(Encoding.ASCII.GetString(blockBytes, bytePtr, 0x10 - 2));
                        bytePtr += 0x10 - 2;

                        if (name.Length == 0)
                            break;

                        var inode = ReadINode(stream, inodeNum);
                        inode.Name = name;

                        if (inodeNum > 0)
                        {
                            var found = FindInodeInTree(orphans, inodeNum);
                            if (found != null && found.Name.StartsWith("unknown"))
                            {
                                found.Name = name;
                            }
                        }

                        selfNode.Children.Add(inode);
                    }
                }


                // distribute any remaining orphans.
                for (int i = 0; i < orphans.Count; i++)
                {
                    if (orphans.Count == 1)
                        break;
                    var orphan = orphans[i];
                    if (orphan == rootNode)
                        continue;

                    var parent = FindInodeInTree(orphans, orphan.parentId);
                    if (parent != null)
                    {
                        parent.Children.Add(orphan);
                        orphans.RemoveAt(i);
                        i = 0;
                    }
                }

            }


            private INode? FindInodeInTree(List<INode> nodes, int inodeNum)
            {
                foreach (var child in nodes)
                {
                    if (child.id == inodeNum)
                        return child;
                    var found = FindInodeInTree(child.Children, inodeNum);
                    if (found != null)
                        return found;
                }
                return null;
            }




            public void UnpackFiles(string basePath)
            {
                UnpackFiles(rootNode, basePath, new List<int>());
            }

            private void UnpackFiles(INode parentNode, string curPath, List<int> inodesVisited)
            {
                inodesVisited.Add(parentNode.id);
                try
                {

                    foreach (var node in parentNode.Children)
                    {
                        if (/* node.IsDirectory */ node.Children.Count > 0)
                        {
                            if (inodesVisited.Contains(node.id))
                            {
                                Console.WriteLine("Warning: circular reference detected.");
                                continue;
                            }
                            UnpackFiles(node, Path.Combine(curPath, node.Name), inodesVisited);
                            continue;
                        }

                        Directory.CreateDirectory(curPath);

                        string filePath = Path.Combine(curPath, node.Name);

                        if (File.Exists(filePath))
                        {
                            Console.WriteLine("Warning: file already exists (amending name): " + filePath);
                            continue;
                        }

                        Console.WriteLine(stream.Position.ToString("X") + ": " + filePath + " - "
                        + node.Size.ToString() + " bytes - " + node.ModifyTime.ToShortDateString());

                        try
                        {
                            using (var f = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                            {
                                byte[] bytes = ReadContents(node);
                                f.Write(bytes);
                                f.Flush();
                            }

                            File.SetCreationTime(filePath, node.CreateTime);
                            File.SetLastWriteTime(filePath, node.ModifyTime);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Error writing file: " + e.Message);
                        }
                    }

                }
                finally
                {
                    inodesVisited.Remove(parentNode.id);
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

            private void FillChildren(Stream stream, INode parentNode, int level, List<int> inodesVisited)
            {
                if (level > 10)
                {
                    //throw new DecodeException("descending too far, possibly circular reference.");
                    Console.WriteLine("descending too far...");
                    return;
                }
                inodesVisited.Add(parentNode.id);
                try
                {

                    byte[]? contents = null;
                    try
                    {
                        contents = ReadContents(parentNode);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Error reading contents of inode " + parentNode.id + ": " + e.Message);
                        return;
                    }

                    int numDirEntries = (int)contents.Length / 0x10;

                    if (numDirEntries > 1000)
                    {
                        Console.WriteLine("Warning: too many directory entries.");
                        numDirEntries = 1000;
                    }

                    for (int i = 0; i < numDirEntries; i++)
                    {
                        var iNodeNum = Utils.GetUInt16(contents, i * 0x10, endianness);

                        if (iNodeNum == 0)
                            continue; // free inode.

                        var name = Utils.ReplaceInvalidChars(Utils.CleanString(Encoding.ASCII.GetString(contents, i * 0x10 + 2, 0x10 - 2)));
                        if (name == "." || name == "..")
                            continue;

                        if (name.Length == 0)
                            break;

                        INode? inode = null;
                        try
                        {
                            inode = ReadINode(stream, iNodeNum);
                            inode.Name = name;

                            parentNode.Children.Add(inode);
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine("Error reading inode " + iNodeNum + ": " + e.Message);
                        }

                        if (inode != null && inode.IsDirectory)
                        {
                            if (inodesVisited.Contains(iNodeNum))
                            {
                                Console.WriteLine("Warning: circular reference detected.");
                                continue;
                            }
                            FillChildren(stream, inode, level + 1, inodesVisited);
                        }
                    }

                }
                finally
                {
                    inodesVisited.Remove(parentNode.id);
                }
            }

            private byte[] ReadContents(INode inode)
            {
                if (inode.id == 0)
                {
                    Console.WriteLine("Warning: inode 0 is not a valid inode.");
                    return new byte[0];
                }

                if (inode.Size > 0x10000000)
                {
                    throw new DecodeException("inode size seems a bit too large.");
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

                totalINodeBlocks = Utils.GetUInt16(bytes, bytePtr, partition.endianness); bytePtr += 2;
                totalVolumeBlocks = (int)Utils.GetUInt32(bytes, bytePtr, partition.endianness); bytePtr += 4;

                // ...free block list...
                // ...free inode list...

                bytePtr = 0x19E;
                lastModifiedTime = Utils.DateTimeFromTimeT(Utils.GetUInt32(bytes, bytePtr, partition.endianness)); bytePtr += 4;
                numFreeDataBlocks = (int)Utils.GetUInt32(bytes, bytePtr, partition.endianness); bytePtr += 4;
                numFreeINodes = Utils.GetUInt16(bytes, bytePtr, partition.endianness); bytePtr += 2;

                // ...device information...

                bytePtr = 0x1B0;
                Name = Utils.CleanString(Encoding.ASCII.GetString(bytes, bytePtr, 6)); bytePtr += 6;
                PackName = Utils.CleanString(Encoding.ASCII.GetString(bytes, bytePtr, 6)); bytePtr += 6;
                clean = bytes[bytePtr++];

                bytePtr = bytes.Length - 8;
                magic = (int)Utils.GetUInt32(bytes, bytePtr, partition.endianness); bytePtr += 4;
                fsType = (int)Utils.GetUInt32(bytes, bytePtr, partition.endianness); bytePtr += 4;

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
            public int parentId;
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
                Mode = Utils.GetUInt16(bytes, bytePtr, partition.endianness); bytePtr += 2;
                nLink = Utils.GetUInt16(bytes, bytePtr, partition.endianness); bytePtr += 2;
                uId = Utils.GetUInt16(bytes, bytePtr, partition.endianness); bytePtr += 2;
                gId = Utils.GetUInt16(bytes, bytePtr, partition.endianness); bytePtr += 2;

                Size = Utils.GetUInt32(bytes, bytePtr, partition.endianness); bytePtr += 4;

                if (Size > 0x10000000)
                {
                    Console.WriteLine("Warning: inode size seems a bit too large.");
                    Size = 0x4000;
                }

                // direct blocks
                int block;
                for (int i = 0; i < 10; i++)
                {
                    block = Utils.Get3ByteInt(bytes, bytePtr, partition.endianness); bytePtr += 3;
                    if (block != 0)
                    {
                        Blocks.Add(block);
                    }
                }

                // indirect blocks
                int blockPtr = Utils.Get3ByteInt(bytes, bytePtr, partition.endianness); bytePtr += 3;
                if (blockPtr != 0)
                {
                    /*
                    byte[] blockBytes = new byte[partition.superBlock.BlockSize];
                    stream.Seek(partition.partitionBaseOffset + (blockPtr * partition.superBlock.BlockSize), SeekOrigin.Begin);
                    stream.Read(blockBytes, 0, blockBytes.Length);
                    for (int i = 0; i < blockBytes.Length / 4; i++)
                    {
                        block = (int)Utils.GetUInt32(blockBytes, i * 4, partition.endianness);
                        if (block <= 0)
                            break;

                        Blocks.Add(block);
                    }
                    */
                }

                // double-indirect blocks
                blockPtr = Utils.Get3ByteInt(bytes, bytePtr, partition.endianness); bytePtr += 3;
                if (blockPtr != 0)
                {
                    /*
                    byte[] iBlockBytes = new byte[partition.superBlock.BlockSize];
                    stream.Seek(partition.partitionBaseOffset + (blockPtr * partition.superBlock.BlockSize), SeekOrigin.Begin);
                    stream.Read(iBlockBytes, 0, iBlockBytes.Length);
                    for (int b = 0; b < iBlockBytes.Length / 4; b++)
                    {
                        var iblock = (int)Utils.GetUInt32(iBlockBytes, b * 4, partition.endianness);
                        if (iblock <= 0)
                            break;

                        byte[] blockBytes = new byte[partition.superBlock.BlockSize];
                        stream.Seek(partition.partitionBaseOffset + (iblock * partition.superBlock.BlockSize), SeekOrigin.Begin);
                        stream.Read(blockBytes, 0, blockBytes.Length);
                        for (int i = 0; i < blockBytes.Length / 4; i++)
                        {
                            block = (int)Utils.GetUInt32(blockBytes, i * 4, partition.endianness);
                            if (block <= 0)
                                break;

                            Blocks.Add(block);
                        }
                    }
                    */
                }

                blockPtr = Utils.Get3ByteInt(bytes, bytePtr, partition.endianness); bytePtr += 3;
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

                AccessTime = Utils.DateTimeFromTimeT(Utils.GetUInt32(bytes, bytePtr, partition.endianness)); bytePtr += 4;
                ModifyTime = Utils.DateTimeFromTimeT(Utils.GetUInt32(bytes, bytePtr, partition.endianness)); bytePtr += 4;
                CreateTime = Utils.DateTimeFromTimeT(Utils.GetUInt32(bytes, bytePtr, partition.endianness)); bytePtr += 4;

            }
        }
    }
}

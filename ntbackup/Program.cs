using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ntbackup
{
    class Program
    {
        static List<string> blockNames = new List<string> { "TAPE", "SSET", "SSES", "VOLB", "DIRB", "FILE", "CFIL", "ESPB", "ESET", "ESES", "EOTM", "SFMB", "CITN" };
        static List<string> catalogStreamNames = new List<string> { "TSMP", "TFDD", "MAP2", "FDD2" };

        static void Main(string[] args)
        {
            string inFileName = "";
            string baseDirectory = "out";

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                else if (args[i] == "-d") { baseDirectory = args[i + 1]; }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage: ntbackup -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[0x10000];

            try
            {
                using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                {
                    var tapeHeaderBlock = new TapeHeaderBlock(stream);
                    if (tapeHeaderBlock.type != "TAPE")
                    {
                        Console.WriteLine("Warning: TAPE block not found at start of stream.");
                        Console.WriteLine("Assuming default parameters...");
                    }
                    else
                    {
                        DescriptorBlock.DefaultBlockSize = tapeHeaderBlock.formatLogicalBlockSize;
                        stream.Seek(2 * DescriptorBlock.DefaultBlockSize, SeekOrigin.Current);
                    }

                    Console.WriteLine("Info: Block size = " + DescriptorBlock.DefaultBlockSize);

                    DirectoryDescriptorBlock currentDirectory = null;
                    FileDescriptorBlock currentFile = null;

                    while (stream.Position < stream.Length)
                    {
                        // pre-load the next block type string, to see what type to read.
                        stream.Read(bytes, 0, 4);
                        stream.Seek(-4, SeekOrigin.Current);
                        string blockType = Encoding.ASCII.GetString(bytes, 0, 4);

                        if (!IsValidBlockName(blockType))
                        {
                            Console.WriteLine(stream.Position.ToString("X") + ": Warning: skipping invalid block.");
                            stream.Seek(DescriptorBlock.DefaultBlockSize, SeekOrigin.Current);
                            continue;
                        }

                        if (IsValidBlockName(blockType))
                        {

                            DescriptorBlock block;

                            if (blockType == "SSET")
                            {
                                block = new StartOfDataSetBlock(stream);
                            }
                            else if (blockType == "VOLB")
                            {
                                block = new VolumeDescriptorBlock(stream);
                            }
                            else if (blockType == "DIRB")
                            {
                                currentDirectory = new DirectoryDescriptorBlock(stream);
                                block = currentDirectory;
                                currentFile = null;
                            }
                            else if (blockType == "FILE")
                            {
                                currentFile = new FileDescriptorBlock(stream);
                                block = currentFile;
                            }
                            else
                            {
                                block = new DescriptorBlock(stream);
                            }

                            Console.WriteLine(stream.Position.ToString("X") + ": " + block.ToString());

                            stream.Seek(block.firstEventOffset, SeekOrigin.Current);
                        }

                        //  traverse streams...

                        while (true)
                        {
                            if (stream.Position >= stream.Length)
                            {
                                Console.WriteLine(stream.Position.ToString("X") + ": Warning: unexpected end of file.");
                                break;
                            }

                            stream.Read(bytes, 0, 4);
                            stream.Seek(-4, SeekOrigin.Current);
                            string streamType = Encoding.ASCII.GetString(bytes, 0, 4);

                            if (blockNames.Contains(streamType) || !IsValidBlockName(streamType))
                            {
                                // there's actually no further stream, but rather a new block.
                                Console.Write("\n");
                                break;
                            }

                            var header = new StreamHeader(stream);
                            bool seekPastStream = true;

                            Console.Write(" -> " + header.id);

                            if (header.id == "STAN")
                            {
                                if (currentDirectory == null || currentFile == null)
                                {
                                    Console.WriteLine(stream.Position.ToString("X") + ": Warning: got STAN stream without valid file and directory.");
                                }
                                else
                                {
                                    //string filePath = Path.Combine(baseDirectory, currentDirectory.Name);
                                    string filePath = currentDirectory.Name != "\\"
                                        ? Path.Combine(baseDirectory, currentDirectory.Name)
                                        : baseDirectory;

                                    Directory.CreateDirectory(filePath);
                                    filePath = Path.Combine(filePath, QicUtils.Utils.ReplaceInvalidChars(currentFile.Name));

                                    while (File.Exists(filePath))
                                    {
                                        Console.WriteLine("Warning: file already exists (amending name): " + filePath);
                                        filePath += "_";
                                    }

                                    using (var f = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                                    {
                                        long bytesLeft = header.length;
                                        while (bytesLeft > 0)
                                        {
                                            int bytesToRead = bytes.Length;
                                            if (bytesToRead > bytesLeft) { bytesToRead = (int)bytesLeft; }
                                            stream.Read(bytes, 0, bytesToRead);
                                            f.Write(bytes, 0, bytesToRead);

                                            if (bytesLeft == header.length)
                                            {
                                                if (!QicUtils.Utils.VerifyFileFormat(currentFile.Name, bytes))
                                                {
                                                    Console.WriteLine(stream.Position.ToString("X") + ": Warning: file format doesn't match: " + filePath);
                                                    Console.ReadKey();
                                                }
                                            }

                                            bytesLeft -= bytesToRead;
                                        }
                                    }

                                    seekPastStream = false;

                                    try
                                    {
                                        File.SetCreationTime(filePath, currentFile.createDate);
                                        File.SetLastWriteTime(filePath, currentFile.modifyDate);
                                        File.SetLastAccessTime(filePath, currentFile.accessDate);
                                        File.SetAttributes(filePath, currentFile.fileAttributes);
                                    }
                                    catch { }
                                }
                            }

                            if (seekPastStream)
                            {
                                stream.Seek(header.length, SeekOrigin.Current);
                            }

                            // align to 4 bytes
                            if (stream.Position % 4 != 0)
                            {
                                stream.Seek(4 - (stream.Position % 4), SeekOrigin.Current);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
        }



        private class FileDescriptorBlock : DescriptorBlock
        {
            public FileAttributes fileAttributes;
            public DateTime modifyDate;
            public DateTime createDate;
            public DateTime backupDate;
            public DateTime accessDate;
            public uint dirId;
            public uint fileId;
            public string Name;

            public FileDescriptorBlock(Stream stream) : base(stream)
            {
                int bytePtr = DescriptorHeaderSize;
                uint attr = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                fileAttributes = (FileAttributes)((attr >> 8) & 0x7);
                modifyDate = GetDateTime(bytes, bytePtr); bytePtr += 5;
                createDate = GetDateTime(bytes, bytePtr); bytePtr += 5;
                backupDate = GetDateTime(bytes, bytePtr); bytePtr += 5;
                accessDate = GetDateTime(bytes, bytePtr); bytePtr += 5;
                dirId = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                fileId = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                Name = GetString(new TapeAddress(bytes, bytePtr)); bytePtr += 4;
            }

            public override string ToString()
            {
                return base.ToString() + ": \"" + Name + "\", " + createDate;
            }
        }

        private class DirectoryDescriptorBlock : DescriptorBlock
        {
            public uint dirbAttributes;
            public DateTime modifyDate;
            public DateTime createDate;
            public DateTime backupDate;
            public DateTime accessDate;
            public uint dirId;
            public string Name;
            
            public DirectoryDescriptorBlock(Stream stream) : base(stream)
            {
                int bytePtr = DescriptorHeaderSize;
                dirbAttributes = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                modifyDate = GetDateTime(bytes, bytePtr); bytePtr += 5;
                createDate = GetDateTime(bytes, bytePtr); bytePtr += 5;
                backupDate = GetDateTime(bytes, bytePtr); bytePtr += 5;
                accessDate = GetDateTime(bytes, bytePtr); bytePtr += 5;
                dirId = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                Name = GetString(new TapeAddress(bytes, bytePtr)).Replace("\0", "\\"); bytePtr += 4;
            }

            public override string ToString()
            {
                return base.ToString() + ": \"" + Name + "\", " + createDate;
            }
        }

        private class VolumeDescriptorBlock : DescriptorBlock
        {
            public uint volbAttributes;
            public string deviceName;
            public string volumeName;
            public string machineName;
            public DateTime writeDate;

            public VolumeDescriptorBlock(Stream stream) : base(stream)
            {
                int bytePtr = DescriptorHeaderSize;
                volbAttributes = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                deviceName = GetString(new TapeAddress(bytes, bytePtr)); bytePtr += 4;
                volumeName = GetString(new TapeAddress(bytes, bytePtr)); bytePtr += 4;
                machineName = GetString(new TapeAddress(bytes, bytePtr)); bytePtr += 4;
                writeDate = GetDateTime(bytes, bytePtr); bytePtr += 5;
            }

            public override string ToString()
            {
                return base.ToString() + ": \"" + deviceName + "\", \"" + volumeName + "\", \"" + machineName + "\", " + writeDate;
            }
        }

        private class StartOfDataSetBlock : DescriptorBlock
        {
            public uint ssetAttributes;
            public int passwordEncryptionAlgo;
            public int softwareCompressionAlgo;
            public int softwareVendorId;
            public int dataSetNum;
            public string dataSetName;
            public string dataSetDescription;
            public string dataSetPassword;
            public string userName;
            public long physicalBlockAddress;
            public DateTime writeDate;
            public int softwareMajorVersion;
            public int softwareMinorVersion;
            public int timeZone;
            public int mftMinorVersion;
            public int mediaCatalogVersion;

            public StartOfDataSetBlock(Stream stream) : base(stream)
            {
                int bytePtr = DescriptorHeaderSize;
                ssetAttributes = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                passwordEncryptionAlgo = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                softwareCompressionAlgo = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                softwareVendorId = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                dataSetNum = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                dataSetName = GetString(new TapeAddress(bytes, bytePtr)); bytePtr += 4;
                dataSetDescription = GetString(new TapeAddress(bytes, bytePtr)); bytePtr += 4;
                dataSetPassword = GetString(new TapeAddress(bytes, bytePtr)); bytePtr += 4;
                userName = GetString(new TapeAddress(bytes, bytePtr)); bytePtr += 4;
                physicalBlockAddress = (long)BitConverter.ToUInt64(bytes, bytePtr); bytePtr += 8;
                writeDate = GetDateTime(bytes, bytePtr); bytePtr += 5;
                softwareMajorVersion = bytes[bytePtr++];
                softwareMinorVersion = bytes[bytePtr++];
                timeZone = bytes[bytePtr++];
                mftMinorVersion = bytes[bytePtr++];
                mediaCatalogVersion = bytes[bytePtr++];
            }

            public override string ToString()
            {
                return base.ToString() + ": \"" + dataSetName + "\", \"" + dataSetDescription + "\", \"" + dataSetPassword + "\", \"" + userName + "\"";
            }
        }

        private class TapeHeaderBlock : DescriptorBlock
        {
            public uint mediaFamilyId;
            public uint tapeAttributes;
            public int mediaSequenceNum;
            public int passwordEncryptionAlgo;
            public int softFileMarkBlockSize;
            public int mediaBasedCatalogType;
            public string mediaName;
            public string mediaDescription;
            public string mediaPassword;
            public string softwareName;
            public int formatLogicalBlockSize;
            public int softwareVendorId;
            public DateTime date;
            public int mtfMajorVersion;

            public TapeHeaderBlock(Stream stream) : base(stream)
            {
                int bytePtr = DescriptorHeaderSize;
                mediaFamilyId = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                tapeAttributes = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                mediaSequenceNum = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                passwordEncryptionAlgo = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                softFileMarkBlockSize = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                mediaBasedCatalogType = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                mediaName = GetString(new TapeAddress(bytes, bytePtr)); bytePtr += 4;
                mediaDescription = GetString(new TapeAddress(bytes, bytePtr)); bytePtr += 4;
                mediaPassword = GetString(new TapeAddress(bytes, bytePtr)); bytePtr += 4;
                softwareName = GetString(new TapeAddress(bytes, bytePtr)); bytePtr += 4;
                formatLogicalBlockSize = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                softwareVendorId = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                date = GetDateTime(bytes, bytePtr); bytePtr += 5;
                mtfMajorVersion = bytes[bytePtr++];
            }

            public override string ToString()
            {
                return base.ToString() + ": \"" + mediaName + "\", \"" + mediaDescription + "\", \"" + mediaPassword + "\", \"" + softwareName + "\"";
            }
        }

        private class DescriptorBlock
        {
            public static int DefaultBlockSize = 0x400;
            public const int DescriptorHeaderSize = 0x34;

            public string type;
            public uint attributes;
            public int firstEventOffset;
            public int osId;
            public int osVersion;
            public ulong displayableSize;
            public ulong formatLogicalAddress;
            public uint controlBlockId;
            public TapeAddress osSpecificData;
            public int stringType;

            protected byte[] bytes;

            public DescriptorBlock(Stream stream)
            {
                bytes = new byte[DefaultBlockSize];
                stream.Read(bytes, 0, bytes.Length);
                stream.Seek(-DefaultBlockSize, SeekOrigin.Current);

                int bytePtr = 0;
                type = Encoding.ASCII.GetString(bytes, bytePtr, 4); bytePtr += 4;
                attributes = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                firstEventOffset = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                osId = bytes[bytePtr++];
                osVersion = bytes[bytePtr++];
                displayableSize = BitConverter.ToUInt64(bytes, bytePtr); bytePtr += 8;
                formatLogicalAddress = BitConverter.ToUInt64(bytes, bytePtr); bytePtr += 8;
                bytePtr += 8; // reserved
                controlBlockId = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                bytePtr += 4; // reserved
                osSpecificData = new TapeAddress(bytes, bytePtr); bytePtr += 4;
                stringType = bytes[bytePtr++];
                bytePtr++; // reserved
                int checksum = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                // TODO: verify checksum?
            }

            protected string GetString(TapeAddress addr)
            {
                if (addr.size == 0)
                {
                    return "";
                }
                if (stringType == 2)
                {
                    return Encoding.Unicode.GetString(bytes, addr.offset, addr.size);
                }
                else
                {
                    return Encoding.ASCII.GetString(bytes, addr.offset, addr.size);
                }
            }

            public override string ToString()
            {
                return type;
            }
        }

        private class StreamHeader
        {
            public const int StreamHeaderSize = 0x16;

            public string id;
            public int fileAttributes;
            public int mediaFormatAttributes;
            public long length;
            public int dataEncryptionAlgo;
            public int dataCompressionAlgo;

            public StreamHeader(Stream stream)
            {
                byte[] bytes = new byte[StreamHeaderSize];
                stream.Read(bytes, 0, bytes.Length);

                int bytePtr = 0;
                id = Encoding.ASCII.GetString(bytes, bytePtr, 4); bytePtr += 4;
                fileAttributes = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                mediaFormatAttributes = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                length = (long)BitConverter.ToUInt64(bytes, bytePtr); bytePtr += 8;
                dataEncryptionAlgo = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                dataCompressionAlgo = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
                int checksum = BitConverter.ToUInt16(bytes, bytePtr); bytePtr += 2;
            }
        }

        private class TapeAddress
        {
            public int size;
            public int offset;

            public TapeAddress(byte[] bytes, int bytePtr)
            {
                size = BitConverter.ToUInt16(bytes, bytePtr);
                offset = BitConverter.ToUInt16(bytes, bytePtr + 2);
            }
        }

        private static DateTime GetDateTime(byte[] bytes, int offset)
        {
            int year = (bytes[offset] << 6) | (bytes[offset + 1] >> 2);
            int month = ((bytes[offset + 1] & 0x3) << 2) | (bytes[offset + 2] >> 6);
            int day = (bytes[offset + 2] >> 1) & 0x1F;
            int hour = ((bytes[offset + 2] & 0x1) << 4) | (bytes[offset + 3] >> 4);
            int minute = ((bytes[offset + 3] & 0xF) << 2) | (bytes[offset + 4] >> 6);
            int second = bytes[offset + 4] & 0x3F;
            DateTime date;
            try { date = new DateTime(year, month, day, hour, minute, second); }
            catch { date = DateTime.Now; }
            return date;
        }

        private static bool IsValidBlockName(string name)
        {
            for (int i = 0; i < name.Length; i++)
            {
                if (!((name[i] >= 'A' && name[i] <= 'Z') || (name[i] >= '0' && name[i] <= '9'))) { return false; }
            }
            return true;
        }

    }
}

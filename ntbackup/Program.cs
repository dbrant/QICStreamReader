using System;
using System.IO;
using System.Text;

namespace ntbackup
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
                        Console.WriteLine("Error: TAPE block not found at start of stream.");
                        return;
                    }

                    DescriptorBlock.DefaultBlockSize = tapeHeaderBlock.formatLogicalBlockSize;

                    stream.Seek(DescriptorBlock.DefaultBlockSize, SeekOrigin.Current);

                    DirectoryDescriptorBlock currentDirectory = null;
                    FileDescriptorBlock currentFile = null;

                    while (stream.Position < stream.Length)
                    {
                        // pre-load the next block type string, to see what type to read.
                        stream.Read(bytes, 0, 4);
                        stream.Seek(-4, SeekOrigin.Current);
                        string blockType = Encoding.ASCII.GetString(bytes, 0, 4);

                        if (blockType == "\0\0\0\0")
                        {
                            // forgive instances of null blocks
                            stream.Seek(DescriptorBlock.DefaultBlockSize, SeekOrigin.Current);
                            continue;
                        }

                        int nextStreamOffset = 0;

                        if (blockType == "SSET")
                        {
                            var block = new StartOfDataSetBlock(stream);
                            stream.Seek(DescriptorBlock.DefaultBlockSize, SeekOrigin.Current);
                            continue;
                        }
                        else if (blockType == "VOLB")
                        {
                            var block = new VolumeDescriptorBlock(stream);
                            stream.Seek(DescriptorBlock.DefaultBlockSize, SeekOrigin.Current);
                            continue;
                        }
                        else if (blockType == "DIRB")
                        {
                            currentDirectory = new DirectoryDescriptorBlock(stream);
                            nextStreamOffset = currentDirectory.firstEventOffset;
                        }
                        else if (blockType == "FILE")
                        {
                            currentFile = new FileDescriptorBlock(stream);
                            nextStreamOffset = currentFile.firstEventOffset;
                        }

                        //  traverse streams...

                        stream.Seek(nextStreamOffset, SeekOrigin.Current);

                        while (true)
                        {

                            var header = new StreamHeader(stream);

                            if (header.id == "STAN")
                            {

                                Console.WriteLine("Got data for " + currentDirectory.Name + "\\" + currentFile.Name);

                            }


                            stream.Seek(header.length, SeekOrigin.Current);

                            // align to 4 bytes
                            if (stream.Position % 4 != 0)
                            {
                                stream.Seek(4 - (stream.Position % 4), SeekOrigin.Current);
                            }

                            if (header.id == "SPAD")
                            {
                                break;
                            }
                        }

                        /*
                        if (header.IsDirectory || header.Name.Trim() == "")
                        {
                            if (header.Size > 0)
                            {
                                stream.Seek(header.Size, SeekOrigin.Current);
                            }
                            continue;
                        }

                        string filePath = baseDirectory;
                        if (header.Subdirectory.Length > 0)
                        {
                            string[] dirArray = header.Subdirectory.Split('\0');
                            for (int i = 0; i < dirArray.Length; i++)
                            {
                                filePath = Path.Combine(filePath, dirArray[i]);
                            }
                        }

                        Directory.CreateDirectory(filePath);
                        filePath = Path.Combine(filePath, header.Name);

                        while (File.Exists(filePath))
                        {
                            Console.WriteLine("Warning: file already exists (amending name): " + filePath);
                            filePath += "_";
                        }

                        Console.WriteLine(stream.Position.ToString("X") + ": " + filePath + " - " + header.Size.ToString() + " bytes - " + header.DateTime.ToShortDateString());

                        using (var f = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                        {
                            long bytesLeft = header.Size;
                            while (bytesLeft > 0)
                            {
                                int bytesToRead = bytes.Length;
                                if (bytesToRead > bytesLeft) { bytesToRead = (int)bytesLeft; }
                                stream.Read(bytes, 0, bytesToRead);
                                f.Write(bytes, 0, bytesToRead);

                                if (bytesLeft == header.Size)
                                {
                                    if (!VerifyFileFormat(header.Name, bytes))
                                    {
                                        Console.WriteLine(stream.Position.ToString("X") + " -- Warning: file format doesn't match: " + filePath);
                                        Console.ReadKey();
                                    }
                                }

                                bytesLeft -= bytesToRead;
                            }
                        }

                        try
                        {
                            File.SetCreationTime(filePath, header.DateTime);
                            File.SetLastWriteTime(filePath, header.DateTime);
                            File.SetAttributes(filePath, header.Attributes);
                        }
                        catch { }
                        */
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
            public uint fileAttributes;
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
                fileAttributes = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                modifyDate = GetDateTime(bytes, bytePtr); bytePtr += 5;
                createDate = GetDateTime(bytes, bytePtr); bytePtr += 5;
                backupDate = GetDateTime(bytes, bytePtr); bytePtr += 5;
                accessDate = GetDateTime(bytes, bytePtr); bytePtr += 5;
                dirId = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                fileId = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
                Name = GetString(new TapeAddress(bytes, bytePtr)); bytePtr += 4;
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

                /*
                ushort testSum = 0;
                for (int i = 0; i < 32; i += 2)
                {
                    testSum ^= BitConverter.ToUInt16(bytes, i);
                }
                if (testSum != checksum)
                {
                    Console.WriteLine("Warning: header checksum mismatch.");
                }
                */
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
        }

        private class StreamHeader
        {
            public const int DescriptorHeaderSize = 0x16;

            public string id;
            public int fileAttributes;
            public int mediaFormatAttributes;
            public long length;
            public int dataEncryptionAlgo;
            public int dataCompressionAlgo;

            public StreamHeader(Stream stream)
            {
                byte[] bytes = new byte[DescriptorHeaderSize];
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



        private class FileHeader
        {
            public long Size { get; }
            public string Name { get; }
            public string DosName { get; }
            public DateTime DateTime { get; }
            public FileAttributes Attributes { get; }
            public string Subdirectory { get; }
            public bool Valid { get; }
            public bool IsDirectory { get; }
            public bool IsLastEntry { get; }
            public bool IsFinalEntry { get; }

            public FileHeader(Stream stream, bool isCatalog, long dataPos, long nextHeaderPos)
            {
                Subdirectory = "";
                byte[] bytes = new byte[1024];
                long initialPos = stream.Position;

                stream.Read(bytes, 0, 0x45);

                DateTime = new DateTime(1970, 1, 1).AddSeconds(BitConverter.ToUInt32(bytes, 0x2D));

                stream.Read(bytes, 0, 2);
                int nameLength = BitConverter.ToUInt16(bytes, 0);
                if (nameLength > 0)
                {
                    stream.Read(bytes, 0, nameLength);
                    Name = Encoding.Unicode.GetString(bytes, 0, nameLength);
                }
                else
                {
                    Name = "";
                }

                stream.Read(bytes, 0, 0x15);

                stream.Read(bytes, 0, 2);
                nameLength = BitConverter.ToUInt16(bytes, 0);
                if (nameLength > 0)
                {
                    stream.Read(bytes, 0, nameLength);
                    DosName = Encoding.Unicode.GetString(bytes, 0, nameLength);
                }
                else
                {
                    DosName = "";
                }


                int dirLen = (int)(dataPos - stream.Position);

                stream.Read(bytes, 0, dirLen);

                Subdirectory = Encoding.Unicode.GetString(bytes, 2, dirLen - 2);

                Size = nextHeaderPos - dataPos - 6;


                Valid = true;
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
            try
            {
                date = new DateTime(year, month, day, hour, minute, second);
            }
            catch { date = DateTime.Now; }
            return date;
        }

        private static string ReplaceInvalidChars(string filename)
        {
            string str = string.Join("_", filename.Split(Path.GetInvalidFileNameChars()));
            str = string.Join("_", str.Split(Path.GetInvalidPathChars()));
            return str;
        }

        private static bool VerifyFileFormat(string fileName, byte[] bytes)
        {
            string nameLower = fileName.ToLower();

            if (nameLower.EndsWith(".exe") && (bytes[0] != 'M' || bytes[1] != 'Z')) { return false; }
            if (nameLower.EndsWith(".zip") && (bytes[0] != 'P' || bytes[1] != 'K')) { return false; }
            if (nameLower.EndsWith(".dwg") && (bytes[0] != 'A' || bytes[1] != 'C')) { return false; }

            return true;
        }
    }
}

using System;
using System.IO;
using System.Text;

namespace QicUtils
{
    public class FileDescriptorBlock : DescriptorBlock
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
            modifyDate = Utils.GetMtfDateTime(bytes, bytePtr); bytePtr += 5;
            createDate = Utils.GetMtfDateTime(bytes, bytePtr); bytePtr += 5;
            backupDate = Utils.GetMtfDateTime(bytes, bytePtr); bytePtr += 5;
            accessDate = Utils.GetMtfDateTime(bytes, bytePtr); bytePtr += 5;
            dirId = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
            fileId = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
            Name = GetString(new TapeAddress(bytes, bytePtr)); bytePtr += 4;
        }

        public override string ToString()
        {
            return base.ToString() + ": \"" + Name + "\", " + createDate;
        }
    }

    public class DirectoryDescriptorBlock : DescriptorBlock
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
            modifyDate = Utils.GetMtfDateTime(bytes, bytePtr); bytePtr += 5;
            createDate = Utils.GetMtfDateTime(bytes, bytePtr); bytePtr += 5;
            backupDate = Utils.GetMtfDateTime(bytes, bytePtr); bytePtr += 5;
            accessDate = Utils.GetMtfDateTime(bytes, bytePtr); bytePtr += 5;
            dirId = BitConverter.ToUInt32(bytes, bytePtr); bytePtr += 4;
            Name = GetString(new TapeAddress(bytes, bytePtr)).Replace("\0", "\\"); bytePtr += 4;
        }

        public override string ToString()
        {
            return base.ToString() + ": \"" + Name + "\", " + createDate;
        }
    }

    public class VolumeDescriptorBlock : DescriptorBlock
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
            writeDate = Utils.GetMtfDateTime(bytes, bytePtr); bytePtr += 5;
        }

        public override string ToString()
        {
            return base.ToString() + ": \"" + deviceName + "\", \"" + volumeName + "\", \"" + machineName + "\", " + writeDate;
        }
    }

    public class StartOfDataSetBlock : DescriptorBlock
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
            writeDate = Utils.GetMtfDateTime(bytes, bytePtr); bytePtr += 5;
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

    public class TapeHeaderBlock : DescriptorBlock
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
            date = Utils.GetMtfDateTime(bytes, bytePtr); bytePtr += 5;
            mtfMajorVersion = bytes[bytePtr++];
        }

        public override string ToString()
        {
            return base.ToString() + ": \"" + mediaName + "\", \"" + mediaDescription + "\", \"" + mediaPassword + "\", \"" + softwareName + "\"";
        }
    }

    public class DescriptorBlock
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
            if (addr.offset + addr.size >= bytes.Length)
            {
                Console.WriteLine("Warning: bad TapeAddress offset/size.");
                return "";
            }
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

    public class StreamHeader
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

    public class TapeAddress
    {
        public int size;
        public int offset;

        public TapeAddress(byte[] bytes, int bytePtr)
        {
            size = BitConverter.ToUInt16(bytes, bytePtr);
            offset = BitConverter.ToUInt16(bytes, bytePtr + 2);
        }
    }
}

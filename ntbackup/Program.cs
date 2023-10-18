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
                using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);
                var tapeHeaderBlock = new QicUtils.Mtf.TapeHeaderBlock(stream);
                if (tapeHeaderBlock.type != "TAPE")
                {
                    Console.WriteLine("Warning: TAPE block not found at start of stream.");
                    Console.WriteLine("Assuming default parameters...");
                }
                else
                {
                    QicUtils.Mtf.DescriptorBlock.DefaultBlockSize = tapeHeaderBlock.formatLogicalBlockSize;
                    stream.Seek(2 * QicUtils.Mtf.DescriptorBlock.DefaultBlockSize, SeekOrigin.Current);
                }

                Console.WriteLine("Info: Block size = " + QicUtils.Mtf.DescriptorBlock.DefaultBlockSize);

                QicUtils.Mtf.DirectoryDescriptorBlock currentDirectory = null;
                QicUtils.Mtf.FileDescriptorBlock currentFile = null;

                while (stream.Position < stream.Length)
                {
                    // pre-load the next block type string, to see what type to read.
                    stream.Read(bytes, 0, 4);
                    stream.Seek(-4, SeekOrigin.Current);
                    string blockType = Encoding.ASCII.GetString(bytes, 0, 4);

                    if (!IsValidBlockName(blockType))
                    {
                        Console.WriteLine(stream.Position.ToString("X") + ": Warning: skipping invalid block.");
                        stream.Seek(QicUtils.Mtf.DescriptorBlock.DefaultBlockSize, SeekOrigin.Current);
                        continue;
                    }

                    if (IsValidBlockName(blockType))
                    {

                        QicUtils.Mtf.DescriptorBlock block;

                        if (blockType == "SSET")
                        {
                            block = new QicUtils.Mtf.StartOfDataSetBlock(stream);
                        }
                        else if (blockType == "VOLB")
                        {
                            block = new QicUtils.Mtf.VolumeDescriptorBlock(stream);
                        }
                        else if (blockType == "DIRB")
                        {
                            currentDirectory = new QicUtils.Mtf.DirectoryDescriptorBlock(stream);
                            block = currentDirectory;
                            currentFile = null;
                        }
                        else if (blockType == "FILE")
                        {
                            currentFile = new QicUtils.Mtf.FileDescriptorBlock(stream);
                            block = currentFile;
                        }
                        else
                        {
                            block = new QicUtils.Mtf.DescriptorBlock(stream);
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

                        var header = new QicUtils.Mtf.StreamHeader(stream);
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
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
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

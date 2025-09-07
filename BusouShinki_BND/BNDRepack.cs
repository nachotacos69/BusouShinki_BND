// --- START OF FILE BNDRepack.cs ---

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BusouShinki_BND
{
    public class BNDRepack
    {

        private int magic;
        private int version;
        private int infoOffset;
        private int tableOffset;
        private int entryInfoArea;
        private int chunks; // Start of the data area
        private int totalFiles;
        private int totalEntries;
        private List<Entry> entries = new List<Entry>();
        private Crc32 crc = new Crc32(); 


        public void Repack(string originalBndPath, string inputFolderPath)
        {
            Console.WriteLine("--- BND Repack Initialized ---");

            // 1. Index the original BND file to get its structure
            IndexBndFile(originalBndPath);
            Console.WriteLine($"[INDEX] Indexed {entries.Count} entries from '{Path.GetFileName(originalBndPath)}'.");

            // 2. Build the new BND file
            BuildNewBnd(originalBndPath, inputFolderPath);

            Console.WriteLine("--- BND Repack Completed ---");
        }


        // Reads the structure of an existing BND file into memory.
        // This is similar to BNDUnpack.Load but for repacking side.
        private void IndexBndFile(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                // Read header
                magic = br.ReadInt32();
                if (magic != 0x00444E42) throw new Exception("Invalid BND magic value.");
                version = br.ReadInt32();
                infoOffset = br.ReadInt32();
                tableOffset = br.ReadInt32();

                // Read info section
                fs.Position = infoOffset;
                entryInfoArea = br.ReadInt32();
                chunks = br.ReadInt32();

                // Read table of contents (TOC)
                fs.Position = tableOffset;
                totalFiles = br.ReadInt32();
                totalEntries = br.ReadInt32();

                // Read each entry in TOC
                for (int i = 0; i < totalEntries; i++)
                {
                    entries.Add(new Entry
                    {
                        TablePosition = fs.Position,
                        Hash = br.ReadUInt32(),
                        EntryInfoOffset = br.ReadInt32(),
                        FileOffset = br.ReadInt32(),
                        FileSize = br.ReadInt32()
                    });
                }

                // Parse entry info for each entry to get names
                foreach (var e in entries)
                {
                    fs.Position = e.EntryInfoOffset;
                    e.Unknown3 = br.ReadBytes(3);
                    e.EntryOffsetBack = br.ReadInt32();
                    List<byte> nameBytes = new List<byte>();
                    byte b;
                    while ((b = br.ReadByte()) != 0)
                    {
                        nameBytes.Add(b);
                    }
                    e.Name = Encoding.UTF8.GetString(nameBytes.ToArray());
                }
            }
        }


        // Constructs the new BND file by writing data and updating the TOC.
        private void BuildNewBnd(string originalBndPath, string inputFolderPath)
        {
            string outputBndPath = Path.Combine(
                Path.GetDirectoryName(originalBndPath),
                Path.GetFileNameWithoutExtension(originalBndPath) + "_new.bnd"
            );

            // Create a temporary copy of the original BND. We will modify this file.
            File.Copy(originalBndPath, outputBndPath, true);
            Console.WriteLine($"[INIT] Created temporary file '{Path.GetFileName(outputBndPath)}'.");

            using (var fs = new FileStream(outputBndPath, FileMode.Open, FileAccess.ReadWrite))
            using (var bw = new BinaryWriter(fs))
            {
                // Sorting the logical write order entries by their original offset.
                var fileEntries = entries.Where(e => e.FileSize > 0).OrderBy(e => e.FileOffset).ToList();

                // We use the 'chunks' value from the header as the starting point for our new data.
                long currentOffset = chunks;

                // Find all files in the input directory to build a lookup map
                var diskFiles = Directory.GetFiles(inputFolderPath, "*", SearchOption.AllDirectories)
                    .ToDictionary(p => p.Substring(inputFolderPath.Length + 1).Replace('\\', '/'), p => p);

                Console.WriteLine($"[REPACK] Found {diskFiles.Count} files in input folder for repacking.");

                // Repack each file entry
                foreach (var entry in fileEntries)
                {
                    byte[] data;
                    string relativePath = entry.Name;

                    // Directories might have a different strings in the BND (e.g., "models/")
                    // We will need to find to path by checking against directory entries.
                    if (!diskFiles.ContainsKey(relativePath))
                    {
                        foreach (var dirEntry in entries.Where(e => e.FileSize == 0))
                        {
                            string testPath = dirEntry.Name + entry.Name;
                            byte[] testBytes = Encoding.UTF8.GetBytes(testPath);
                            if (crc.Compute(testBytes) == entry.Hash) //CRC32 Check
                            {
                                relativePath = testPath;
                                break;
                            }
                        }
                    }

                    if (diskFiles.TryGetValue(relativePath, out string diskFilePath))
                    {
                        Console.WriteLine($"[MATCH] >> {relativePath} = 0x{entry.Hash:X8} -> [File Found]");
                        data = File.ReadAllBytes(diskFilePath);
                    }
                    else
                    {
                        // If file not found in input folder, re-use original data
                        Console.WriteLine($"[NO MATCH] >> {relativePath} = 0x{entry.Hash:X8} -> [Using Original Data]");
                        using (var originalFs = new FileStream(originalBndPath, FileMode.Open, FileAccess.Read))
                        using (var originalBr = new BinaryReader(originalFs))
                        {
                            originalFs.Position = entry.FileOffset;
                            data = originalBr.ReadBytes(entry.FileSize);
                        }
                    }

                    // Align the current position to 16 bytes before writing
                    long alignedOffset = (currentOffset + 15) & ~15;
                    if (alignedOffset > fs.Length)
                    {
                        fs.SetLength(alignedOffset); // Ensure stream is long enough for padding
                    }

                    fs.Position = currentOffset;
                    int padding = (int)(alignedOffset - currentOffset);
                    if (padding > 0)
                    {
                        bw.Write(new byte[padding]);
                    }
                    currentOffset = alignedOffset;

                    // Update entry info in our memory list
                    entry.FileOffset = (int)currentOffset;
                    entry.FileSize = data.Length;

                    // Write the file data to the new BND
                    fs.Position = currentOffset;
                    bw.Write(data);
                    Console.WriteLine($"[WRITE] Wrote {entry.Name} ({entry.FileSize} bytes) at offset 0x{entry.FileOffset:X}");

                    // Update offset for the next file
                    currentOffset += data.Length;
                }

                // Remove old junk data
                Console.WriteLine($"Cleaning Up...");
                fs.SetLength(currentOffset);

                // After all file data is written, update the Table of Contents
                Console.WriteLine("[UPDATE] Writing updated Table of Contents...");
                foreach (var entry in entries)
                {
                    // For directory entries, FileOffset and FileSize should be 0
                    if (entry.FileSize == 0)
                    {
                        entry.FileOffset = 0;
                    }

                    bw.BaseStream.Position = entry.TablePosition;
                    bw.Write(entry.Hash);
                    bw.Write(entry.EntryInfoOffset);
                    bw.Write(entry.FileOffset); // New offset
                    bw.Write(entry.FileSize);   // New size
                }
            }
            Console.WriteLine($"[SUCCESS] Repacked BND saved to '{Path.GetFileName(outputBndPath)}'.");
        }
    }
}
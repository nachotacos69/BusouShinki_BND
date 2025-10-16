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

        // Cache for directory path combinations
        private Dictionary<uint, string> hashToPathCache = new Dictionary<uint, string>();

        public void Repack(string originalBndPath, string inputFolderPath)
        {
            Console.WriteLine("--- BND Repack Initialized ---");

            IndexBndFile(originalBndPath);
            Console.WriteLine($"[INDEX] Indexed {entries.Count} entries from '{Path.GetFileName(originalBndPath)}'.");

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
                // header
                magic = br.ReadInt32();
                if (magic != 0x00444E42) throw new Exception("Invalid BND magic value.");
                version = br.ReadInt32();
                infoOffset = br.ReadInt32();
                tableOffset = br.ReadInt32();

                // Info Section
                fs.Position = infoOffset;
                entryInfoArea = br.ReadInt32();
                chunks = br.ReadInt32();

                // Read TOC
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

            // Pre-compution for hash-to-path mappings
            PrecomputeHashMaps();
        }

        private void PrecomputeHashMaps()
        {
            var dirEntries = entries.Where(e => e.FileSize == 0).ToList();
            var fileEntries = entries.Where(e => e.FileSize > 0).ToList();

            foreach (var file in fileEntries)
            {
                // Try direct match first
                if (!hashToPathCache.ContainsKey(file.Hash))
                {
                    hashToPathCache[file.Hash] = file.Name;
                }

                // Try all directory combinations
                foreach (var dir in dirEntries)
                {
                    string testPath1 = dir.Name + file.Name;
                    byte[] testBytes1 = Encoding.UTF8.GetBytes(testPath1);
                    uint computed1 = crc.Compute(testBytes1);
                    if (computed1 == file.Hash)
                    {
                        hashToPathCache[file.Hash] = testPath1;
                        break;
                    }

                    string testPath2 = dir.Name + "/" + file.Name;
                    byte[] testBytes2 = Encoding.UTF8.GetBytes(testPath2);
                    uint computed2 = crc.Compute(testBytes2);
                    if (computed2 == file.Hash)
                    {
                        hashToPathCache[file.Hash] = testPath2;
                        break;
                    }
                }
            }
        }

        private void BuildNewBnd(string originalBndPath, string inputFolderPath)
        {
            string outputBndPath = Path.Combine(
                Path.GetDirectoryName(originalBndPath),
                Path.GetFileNameWithoutExtension(originalBndPath) + "_new.bnd"
            );

            File.Copy(originalBndPath, outputBndPath, true);
            Console.WriteLine($"[INIT] Created temporary file '{Path.GetFileName(outputBndPath)}'.");

            // Normalize disk file paths once
            var diskFiles = Directory.GetFiles(inputFolderPath, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    p => p.Substring(inputFolderPath.Length + 1).Replace('\\', '/'),
                    p => p,
                    StringComparer.OrdinalIgnoreCase
                );

            Console.WriteLine($"[REPACK] Found {diskFiles.Count} files in input folder for repacking.");

            var fileEntries = entries.Where(e => e.FileSize > 0).OrderBy(e => e.FileOffset).ToList();

            // Pre-load original BND data into memory for missing files
            byte[] originalBndData = File.ReadAllBytes(originalBndPath);

            using (var fs = new FileStream(outputBndPath, FileMode.Open, FileAccess.ReadWrite))
            using (var bw = new BinaryWriter(fs))
            {
                long currentOffset = chunks;

                foreach (var entry in fileEntries)
                {
                    byte[] data;
                    string relativePath = hashToPathCache.ContainsKey(entry.Hash)
                        ? hashToPathCache[entry.Hash]
                        : entry.Name;

                    if (diskFiles.TryGetValue(relativePath, out string diskFilePath))
                    {
                        Console.WriteLine($"[MATCH] >> {relativePath} = 0x{entry.Hash:X8} -> [File Found]");
                        data = File.ReadAllBytes(diskFilePath);
                    }
                    else
                    {
                        // If file not found in input folder, re-use original data
                        Console.WriteLine($"[NO MATCH] >> {relativePath} = 0x{entry.Hash:X8} -> [Using Original Data]");
                        data = new byte[entry.FileSize];
                        Buffer.BlockCopy(originalBndData, entry.FileOffset, data, 0, entry.FileSize);
                    }

                    // Align each EOF data to 16-byte boundary 
                    long alignedOffset = (currentOffset + 15) & ~15;
                    long padding = alignedOffset - currentOffset;

                    if (padding > 0)
                    {
                        fs.Position = currentOffset;
                        fs.Write(new byte[padding], 0, (int)padding);
                    }

                    // Update entry info
                    entry.FileOffset = (int)alignedOffset;
                    entry.FileSize = data.Length;

                    fs.Position = alignedOffset;
                    fs.Write(data, 0, data.Length);
                    Console.WriteLine($"[WRITE] Wrote {entry.Name} ({entry.FileSize} bytes) at offset 0x{entry.FileOffset:X}");

                    currentOffset = alignedOffset + data.Length;
                }

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

                    fs.Position = entry.TablePosition;
                    bw.Write(entry.Hash);
                    bw.Write(entry.EntryInfoOffset);
                    bw.Write(entry.FileOffset); // New offset being written
                    bw.Write(entry.FileSize);   // New size being written
                }
            }
            Console.WriteLine($"[SUCCESS] Repacked BND saved to '{Path.GetFileName(outputBndPath)}'.");
        }
    }
}
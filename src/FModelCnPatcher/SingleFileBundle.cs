using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FModelCnPatcher;

public sealed class BundleEntry
{
    public long Offset { get; init; }
    public long Size { get; init; }
    public long CompressedSize { get; init; }
    public byte Type { get; init; }
    public string RelativePath { get; init; } = string.Empty;
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

public sealed class SingleFileBundle
{
    // SHA-256(".net core bundle")
    private static readonly byte[] BundleSignature =
    [
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    ];

    public const string PatchMarkerEntryName = "FModel.CnPatch.marker";

    public byte[] StubBytes { get; private set; } = Array.Empty<byte>();
    public int HeaderOffsetFieldPos { get; private set; }
    public uint MajorVersion { get; private set; }
    public uint MinorVersion { get; private set; }
    public string BundleId { get; set; } = string.Empty;
    public ulong Flags { get; private set; }
    public List<BundleEntry> Entries { get; } = [];

    public bool HasPatchMarker => Entries.Any(e => string.Equals(e.RelativePath, PatchMarkerEntryName, StringComparison.OrdinalIgnoreCase));

    public static bool QuickHasPatchMarker(string exePath)
    {
        try
        {
            using var fs = File.OpenRead(exePath);
            var sigPos = FindSignatureOffset(fs);
            if (sigPos < 8) return false;
            fs.Seek(sigPos - 8, SeekOrigin.Begin);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            long headerOffset = br.ReadInt64();
            if (headerOffset <= 0 || headerOffset >= fs.Length) return false;

            fs.Seek(headerOffset, SeekOrigin.Begin);
            uint major = br.ReadUInt32();
            _ = br.ReadUInt32(); // minor
            int count = br.ReadInt32();
            _ = br.ReadString(); // bundleId
            if (major >= 2)
            {
                br.ReadInt64(); br.ReadInt64();
                br.ReadInt64(); br.ReadInt64();
                br.ReadUInt64();
            }
            for (int i = 0; i < count; i++)
            {
                br.ReadInt64();
                br.ReadInt64();
                if (major >= 6) br.ReadInt64();
                br.ReadByte();
                string rel = br.ReadString();
                if (string.Equals(rel, PatchMarkerEntryName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static SingleFileBundle Load(string exePath)
    {
        var bundle = new SingleFileBundle();
        using var fs = File.OpenRead(exePath);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        int sigPos = FindSignatureOffset(fs);
        if (sigPos < 8)
            throw new InvalidDataException($"无法在文件中找到 .NET Single-File Bundle 签名: {exePath}。请确认这是 FModel.exe 可执行文件。");

        bundle.HeaderOffsetFieldPos = sigPos - 8;
        fs.Seek(bundle.HeaderOffsetFieldPos, SeekOrigin.Begin);
        long headerOffset = br.ReadInt64();
        if (headerOffset <= 0 || headerOffset >= fs.Length)
            throw new InvalidDataException($"Bundle Header 偏移无效: {headerOffset}");

        fs.Seek(headerOffset, SeekOrigin.Begin);
        bundle.MajorVersion = br.ReadUInt32();
        bundle.MinorVersion = br.ReadUInt32();
        int numFiles = br.ReadInt32();
        bundle.BundleId = br.ReadString();

        if (bundle.MajorVersion >= 2)
        {
            br.ReadInt64(); // depsOffset
            br.ReadInt64(); // depsSize
            br.ReadInt64(); // runtimeConfigOffset
            br.ReadInt64(); // runtimeConfigSize
            bundle.Flags = br.ReadUInt64();
        }

        var rawMeta = new List<(long off, long sz, long csz, byte tp, string path)>(numFiles);
        long minPayloadOffset = headerOffset;
        for (int i = 0; i < numFiles; i++)
        {
            long off = br.ReadInt64();
            long sz = br.ReadInt64();
            long csz = bundle.MajorVersion >= 6 ? br.ReadInt64() : 0;
            byte tp = br.ReadByte();
            string rel = br.ReadString();
            rawMeta.Add((off, sz, csz, tp, rel));
            if (off > 0 && off < minPayloadOffset)
                minPayloadOffset = off;
        }

        // Preserve PE + apphost stub up to the start of the first embedded payload
        bundle.StubBytes = new byte[minPayloadOffset];
        fs.Seek(0, SeekOrigin.Begin);
        ReadExactly(fs, bundle.StubBytes);

        foreach (var m in rawMeta)
        {
            fs.Seek(m.off, SeekOrigin.Begin);
            byte[] payload;
            if (m.csz > 0)
            {
                var compressed = new byte[m.csz];
                ReadExactly(fs, compressed);
                using var msIn = new MemoryStream(compressed);
                using var deflate = new DeflateStream(msIn, CompressionMode.Decompress);
                payload = new byte[m.sz];
                ReadExactly(deflate, payload);
            }
            else
            {
                payload = new byte[m.sz];
                ReadExactly(fs, payload);
            }

            bundle.Entries.Add(new BundleEntry
            {
                Offset = m.off,
                Size = m.sz,
                CompressedSize = m.csz,
                Type = m.tp,
                RelativePath = m.path,
                Data = payload
            });
        }

        return bundle;
    }

    public void SetOrAddEntry(string relativePath, byte type, byte[] data)
    {
        var existing = Entries.FirstOrDefault(e => string.Equals(e.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Data = data;
        }
        else
        {
            Entries.Add(new BundleEntry
            {
                Type = type,
                RelativePath = relativePath,
                Data = data
            });
        }
    }

    public void Save(string outputExePath)
    {
        // Generate deterministic-looking short bundle ID so native extraction dir stays isolated from original
        using var sha = SHA256.Create();
        var hashInput = new MemoryStream();
        foreach (var e in Entries)
        {
            var p = Encoding.UTF8.GetBytes(e.RelativePath);
            hashInput.Write(p);
            hashInput.Write(BitConverter.GetBytes(e.Data.Length));
        }
        var digest = Convert.ToBase64String(sha.ComputeHash(hashInput.ToArray()))
            .Replace('+', 'A').Replace('/', 'B').Replace('=', 'C');
        BundleId = "Cn" + digest[..Math.Min(10, digest.Length)];

        var tempPath = outputExePath + ".tmp";
        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
        {
            fs.Write(StubBytes, 0, StubBytes.Length);

            long depsOff = 0, depsSz = 0, rcOff = 0, rcSz = 0;
            var writtenOffsets = new List<(long off, long sz, byte tp, string path)>(Entries.Count);

            foreach (var entry in Entries)
            {
                long align = entry.Type == 1 ? 4096 : 16;
                long cur = fs.Position;
                long pad = (align - (cur % align)) % align;
                if (pad > 0)
                    fs.Write(new byte[pad], 0, (int)pad);

                long entryOff = fs.Position;
                fs.Write(entry.Data, 0, entry.Data.Length);
                long entrySz = entry.Data.Length;

                writtenOffsets.Add((entryOff, entrySz, entry.Type, entry.RelativePath));

                if (entry.RelativePath.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
                {
                    depsOff = entryOff;
                    depsSz = entrySz;
                }
                else if (entry.RelativePath.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                {
                    rcOff = entryOff;
                    rcSz = entrySz;
                }
            }

            long headerOffset = fs.Position;
            bw.Write(MajorVersion);
            bw.Write(MinorVersion);
            bw.Write(writtenOffsets.Count);
            bw.Write(BundleId);

            if (MajorVersion >= 2)
            {
                bw.Write(depsOff);
                bw.Write(depsSz);
                bw.Write(rcOff);
                bw.Write(rcSz);
                bw.Write(Flags);
            }

            foreach (var w in writtenOffsets)
            {
                bw.Write(w.off);
                bw.Write(w.sz);
                if (MajorVersion >= 6)
                    bw.Write(0L); // Uncompressed
                bw.Write(w.tp);
                bw.Write(w.path);
            }

            bw.Flush();
            fs.Seek(HeaderOffsetFieldPos, SeekOrigin.Begin);
            bw.Write(headerOffset);
            bw.Flush();
        }

        File.Move(tempPath, outputExePath, overwrite: true);
    }

    private static int FindSignatureOffset(FileStream fs)
    {
        int scanLen = (int)Math.Min(fs.Length, 2 * 1024 * 1024);
        var buf = new byte[scanLen];
        fs.Seek(0, SeekOrigin.Begin);
        ReadExactly(fs, buf);

        for (int i = 8; i <= buf.Length - BundleSignature.Length; i++)
        {
            if (buf[i] == BundleSignature[0] && buf.AsSpan(i, BundleSignature.Length).SequenceEqual(BundleSignature))
                return i;
        }
        return -1;
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0)
                throw new EndOfStreamException("读取单文件包数据时提前到达流末尾。");
            total += n;
        }
    }
}

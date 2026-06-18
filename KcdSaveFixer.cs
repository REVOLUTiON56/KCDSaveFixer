// KCD Save Fixer -- single-file C# program.
// Kingdom Come: Deliverance (2018) .whs save de-bloater.
//
// Everything (container parsing, mailbox detection, the fix, and the CLI) is in
// this one file. No project references, no extra .cs files.
//
// Build a self-contained single .exe (no .NET needed to run it):
//   dotnet publish -c Release -r win-x64 -o publish
//   (swap win-x64 for linux-x64 / osx-arm64 etc. as needed)
//
// Or just run it directly with the SDK:
//   dotnet run -- analyze "C:\path\to\save.whs"
//   dotnet run -- fix     "C:\path\to\save.whs"

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace KcdSaveFixer;

// ===========================================================================
//  .whs container  (decompress / repack)
// ===========================================================================
// A .whs file is a bare sequence of zlib chunks followed by a 64-byte footer.
// There is NO plaintext file header.
//   Each chunk:  uint32 compLen | uint32 uncompLen | zlib stream (starts 0x78)
//   Footer (64 bytes): magic "0XBP" + 16-byte hash + padding.
//   The hash is NOT validated on load, so we preserve the footer verbatim.
internal static class Whs
{
    public const int BlockSize = 0x8000;
    public const byte ZlibMagic = 0x78;
    public static readonly byte[] FooterMagic = { 0x30, 0x58, 0x42, 0x50 }; // "0XBP"

    public sealed class Decoded
    {
        public byte[] Payload = Array.Empty<byte>();
        public byte[] Footer = Array.Empty<byte>();
        public int ChunkCount;
        public bool FooterMagicOk;
    }

    public static Decoded Decompress(byte[] raw)
    {
        var outBuf = new MemoryStream();
        int pos = 0, chunks = 0, n = raw.Length;

        while (pos + 8 <= n)
        {
            uint compLen = ReadU32(raw, pos);
            uint uncompLen = ReadU32(raw, pos + 4);
            int z = pos + 8;

            if (compLen < 6 || z + compLen > n || raw[z] != ZlibMagic
                || uncompLen == 0 || uncompLen > 0x100000)
                break; // reached the footer

            byte[] block;
            try { block = Inflate(raw, z, (int)compLen); }
            catch (InvalidDataException) { break; }

            if (block.Length != uncompLen) break;

            outBuf.Write(block, 0, block.Length);
            pos = z + (int)compLen;
            chunks++;
        }

        if (chunks == 0)
            throw new InvalidDataException("Not a valid .whs container (no zlib chunks found).");

        byte[] footer = new byte[n - pos];
        Array.Copy(raw, pos, footer, 0, footer.Length);

        bool magicOk = footer.Length >= 4
            && footer[0] == FooterMagic[0] && footer[1] == FooterMagic[1]
            && footer[2] == FooterMagic[2] && footer[3] == FooterMagic[3];

        return new Decoded
        {
            Payload = outBuf.ToArray(),
            Footer = footer,
            ChunkCount = chunks,
            FooterMagicOk = magicOk,
        };
    }

    public static byte[] Repack(byte[] payload, byte[] footer)
    {
        var outBuf = new MemoryStream();
        Span<byte> hdr = stackalloc byte[8];
        int p = 0, n = payload.Length;
        while (p < n)
        {
            int len = Math.Min(BlockSize, n - p);
            byte[] comp = Deflate(payload, p, len);
            WriteU32(hdr, 0, (uint)comp.Length);
            WriteU32(hdr, 4, (uint)len);
            outBuf.Write(hdr);
            outBuf.Write(comp, 0, comp.Length);
            p += len;
        }
        outBuf.Write(footer, 0, footer.Length);
        return outBuf.ToArray();
    }

    private static byte[] Inflate(byte[] data, int offset, int count)
    {
        using var input = new MemoryStream(data, offset, count);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] data, int offset, int count)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data, offset, count);
        return output.ToArray();
    }

    public static uint ReadU32(byte[] b, int o) =>
        (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    public static void WriteU32(byte[] b, int o, uint v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
        b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }

    private static void WriteU32(Span<byte> b, int o, uint v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
        b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }
}

// ===========================================================================
//  Mailbox detection + the fix
// ===========================================================================
// Mailbox layout (verified):
//   ASCII name ... "Mailbox" then a 0x00 byte. Let Nul = offset of that 0x00.
//   Count1 = uint32 at Nul+1, Count2 = uint32 at Nul+21 (a duplicate of Count1).
//   Valid if Count1 == Count2. The 8-byte entry index begins at Nul+29.
internal sealed class Mailbox
{
    public string Name = "";
    public int Nul;
    public uint Count;
    public int Count1Off => Nul + 1;
    public int Count2Off => Nul + 21;
}

internal sealed record MailboxSummary(string Name, int Occurrences, uint MaxCount);

internal static class SaveFixer
{
    // Healthy saves observed: largest normal QuestMailbox ~511, HorseMailbox ~52.
    // A broken save had a HorseMailbox of 125,635. 5000 sits well above every
    // healthy value and far below the breakage.
    public const uint DefaultThreshold = 5000;
    private const int CountSpread = 21;
    private const uint MaxSaneCount = 10_000_000;

    public static List<Mailbox> FindMailboxes(byte[] data)
    {
        var result = new List<Mailbox>();
        byte[] needle = Encoding.ASCII.GetBytes("Mailbox\0");
        int n = data.Length, from = 0;

        while (true)
        {
            int hit = IndexOf(data, needle, from);
            if (hit < 0) break;
            from = hit + 1;

            int nul = hit + 7;                 // the terminating 0x00
            int start = hit;                   // walk back over the name letters
            while (start > 0 && IsLetter(data[start - 1])) start--;

            int c1 = nul + 1, c2 = nul + CountSpread;
            if (c2 + 4 > n) continue;

            uint v1 = Whs.ReadU32(data, c1), v2 = Whs.ReadU32(data, c2);
            if (v1 != v2 || v1 == 0 || v1 >= MaxSaneCount) continue;

            result.Add(new Mailbox
            {
                Name = Encoding.ASCII.GetString(data, start, nul - start),
                Nul = nul,
                Count = v1,
            });
        }
        return result;
    }

    public static List<MailboxSummary> Summarize(IEnumerable<Mailbox> mb) =>
        mb.GroupBy(m => m.Name)
          .Select(g => new MailboxSummary(g.Key, g.Count(), g.Max(m => m.Count)))
          .OrderByDescending(s => s.MaxCount)
          .ToList();

    /// <summary>
    /// Identify the NPC ("actor") responsible for the bloat. The stuck commands
    /// each carry their owner's actor id, an ASCII token like "ska_matus". When a
    /// queue has exploded, that token's occurrence count in the payload roughly
    /// equals the bloated entry count, so the most frequent "ska_*" actor whose
    /// count is in the same ballpark as <paramref name="bloatedCount"/> is the
    /// culprit. Returns (name, occurrences) or null if nothing convincing is found.
    /// </summary>
    public static (string Name, int Count)? FindDominantActor(byte[] data, uint bloatedCount)
    {
        // Count every "ska_<letters/digits/underscore>" token.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        byte[] prefix = Encoding.ASCII.GetBytes("ska_");
        int n = data.Length, from = 0;

        while (true)
        {
            int hit = IndexOf(data, prefix, from);
            if (hit < 0) break;
            from = hit + 1;

            int end = hit + prefix.Length;
            while (end < n && IsActorChar(data[end])) end++;
            if (end - hit < 6) continue; // "ska_" + at least 2 chars

            string name = Encoding.ASCII.GetString(data, hit, end - hit);
            counts[name] = counts.TryGetValue(name, out int c) ? c + 1 : 1;
        }

        if (counts.Count == 0) return null;

        var top = counts.OrderByDescending(kv => kv.Value).First();

        // Only trust it if its frequency is in the same order of magnitude as the
        // bloated queue (guards against naming an innocent, common actor).
        if (top.Value >= bloatedCount / 2)
            return (top.Key, top.Value);

        return null;
    }

    private static bool IsActorChar(byte b) =>
        (b >= (byte)'a' && b <= (byte)'z') ||
        (b >= (byte)'0' && b <= (byte)'9') || b == (byte)'_';

    private static bool IsLetter(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z');

    private static int IndexOf(byte[] hay, byte[] needle, int start)
    {
        int limit = hay.Length - needle.Length;
        for (int i = start; i <= limit; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}

// ===========================================================================
//  Console colors (ANSI). Red = bad, green = good.
// ===========================================================================
internal static class Col
{
    private const string Reset = "\x1b[0m";
    private const string RedSeq = "\x1b[31m";
    private const string GreenSeq = "\x1b[32m";
    private const string YellowSeq = "\x1b[33m";

    public static bool Enabled { get; private set; } = true;

    /// <summary>
    /// Decide whether to emit ANSI codes and, on Windows, try to enable the
    /// virtual-terminal mode so the codes render instead of printing literally.
    /// Honors --no-color and the NO_COLOR convention; disables when output is
    /// redirected to a file/pipe.
    /// </summary>
    public static void Init(string[] args)
    {
        bool noColorFlag = args.Any(a => a == "--no-color");
        bool noColorEnv = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        if (noColorFlag || noColorEnv || Console.IsOutputRedirected)
        {
            Enabled = false;
            return;
        }
        // On Windows 10+ the console needs VT processing turned on.
        if (OperatingSystem.IsWindows())
            Enabled = NativeVt.TryEnable();
    }

    public static string Red(string s) => Enabled ? RedSeq + s + Reset : s;
    public static string Green(string s) => Enabled ? GreenSeq + s + Reset : s;
    public static string Yellow(string s) => Enabled ? YellowSeq + s + Reset : s;
}

// Minimal P/Invoke to enable ANSI on the Windows console. Trim/AOT safe.
internal static class NativeVt
{
    public static bool TryEnable()
    {
        try
        {
            const int STD_OUTPUT_HANDLE = -11;
            const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
            nint handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (!GetConsoleMode(handle, out uint mode)) return false;
            return SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);
}

// ===========================================================================
//  CLI
// ===========================================================================
internal static class Program
{
    private static int Main(string[] args)
    {
        Col.Init(args);
        if (args.Length == 0) { Usage(); return 1; }

        string mode = args[0].ToLowerInvariant();
        try
        {
            return mode switch
            {
                "analyze" => Analyze(args),
                "fix" => Fix(args),
                "-h" or "--help" or "help" => OkUsage(),
                _ => Unknown(mode),
            };
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(Col.Red("ERROR: file not found: " + ex.FileName));
            return 2;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine(Col.Red("ERROR: " + ex.Message));
            Console.Error.WriteLine(Col.Red("This does not appear to be a valid KCD .whs save file."));
            return 2;
        }
    }

    private static int Analyze(string[] args)
    {
        if (!SavePath(args, out string path)) { Console.Error.WriteLine(Col.Red("ERROR: no save file given.")); Usage(); return 1; }
        uint threshold = Threshold(args);
        byte[] raw = Read(path);

        var dec = Whs.Decompress(raw);
        var mbs = SaveFixer.FindMailboxes(dec.Payload);
        var summary = SaveFixer.Summarize(mbs);
        var bloated = mbs.Where(m => m.Count > threshold).ToList();

        string bar = new('=', 64);
        Console.WriteLine(bar);
        Console.WriteLine("KCD Save Fixer -- ANALYZE");
        Console.WriteLine(bar);
        Console.WriteLine($"File              : {path}");
        Console.WriteLine($"On-disk size      : {raw.Length:N0} bytes ({raw.Length / 1e6:F2} MB)");
        Console.WriteLine($"Decompressed size : {dec.Payload.Length:N0} bytes ({dec.Payload.Length / 1e6:F2} MB)");
        Console.WriteLine($"zlib chunks       : {dec.ChunkCount}");
        Console.WriteLine($"Footer            : magic={(dec.FooterMagicOk ? "OK" : Col.Red("MISSING"))}");
        Console.WriteLine();
        Console.WriteLine($"Mailbox structures found: {mbs.Count} ({summary.Count} distinct names)");
        Console.WriteLine();
        Console.WriteLine($"{"MAILBOX NAME",-26}{"OCCURRENCES",12}{"MAX ENTRIES",14}");
        Console.WriteLine(new string('-', 52));
        foreach (var s in summary)
        {
            string row = $"{s.Name,-26}{s.Occurrences,12}{s.MaxCount,14}";
            if (s.MaxCount > threshold)
                Console.WriteLine(Col.Red(row + "  <-- ABNORMAL"));
            else
                Console.WriteLine(row);
        }
        Console.WriteLine();

        if (bloated.Count > 0)
        {
            Console.WriteLine(Col.Red($"VERDICT: BLOATED. {bloated.Count} mailbox(es) exceed the threshold of {threshold:N0} entries:"));
            foreach (var mb in bloated)
            {
                Console.WriteLine(Col.Red($"  - {mb.Name} @ 0x{mb.Nul:X}  count = {mb.Count:N0}"));
                var actor = SaveFixer.FindDominantActor(dec.Payload, mb.Count);
                if (actor is { } a)
                    Console.WriteLine(Col.Red($"      likely culprit NPC: {a.Name}  (appears {a.Count:N0} times)"));
            }
            Console.WriteLine();
            Console.WriteLine("This save matches the runaway-command-queue signature.");
            Console.WriteLine("The 'likely culprit NPC' is the actor whose AI got stuck. Its id (e.g.");
            Console.WriteLine("'ska_matus' = the NPC Matus / \"Matthew\") tells you who to un-stick");
            Console.WriteLine("in-game after fixing -- see the README.");
            Console.WriteLine($"Run:  kcdsavefixer fix \"{path}\"");
        }
        else
        {
            Console.WriteLine(Col.Green("VERDICT: HEALTHY (no mailbox exceeds the threshold)."));
            Console.WriteLine("This save does NOT match the runaway-command-queue signature.");
            Console.WriteLine("If it still won't load or crashes, the cause is something this tool");
            Console.WriteLine("does not address -- see README.");
        }
        Console.WriteLine(bar);
        return 0;
    }

    private static int Fix(string[] args)
    {
        if (!SavePath(args, out string path)) { Console.Error.WriteLine(Col.Red("ERROR: no save file given.")); Usage(); return 1; }
        uint threshold = Threshold(args);
        string? outPath = Option(args, "-o") ?? Option(args, "--out");
        byte[] raw = Read(path);

        string bar = new('=', 64);
        Console.WriteLine(bar);
        Console.WriteLine("KCD Save Fixer -- FIX");
        Console.WriteLine(bar);
        Console.WriteLine($"File              : {path}");

        var dec = Whs.Decompress(raw);
        byte[] data = dec.Payload;
        var bloated = SaveFixer.FindMailboxes(data).Where(m => m.Count > threshold).ToList();

        if (bloated.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine($"No mailbox exceeds the threshold of {threshold:N0} entries.");
            Console.WriteLine("Nothing to fix. This save does not match the bloat signature.");
            Console.WriteLine("No output file was written.");
            Console.WriteLine(bar);
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine(Col.Red($"Found {bloated.Count} bloated mailbox(es). Zeroing their counters"));
        Console.WriteLine(Col.Red("in place (no bytes are moved or deleted):"));
        foreach (var mb in bloated)
        {
            Console.WriteLine(Col.Red($"  - {mb.Name} @ 0x{mb.Nul:X}: {mb.Count:N0} -> 0"));
            Whs.WriteU32(data, mb.Count1Off, 0);
            Whs.WriteU32(data, mb.Count2Off, 0);
        }
        Console.WriteLine();

        // Safety: re-scan; nothing should still exceed the threshold.
        if (SaveFixer.FindMailboxes(data).Any(m => m.Count > threshold))
        {
            Console.Error.WriteLine(Col.Red("ERROR: counters did not zero correctly. No file written."));
            return 1;
        }

        outPath ??= Path.Combine(
            Path.GetDirectoryName(path) ?? "",
            Path.GetFileNameWithoutExtension(path) + ".fixed" +
            (string.IsNullOrEmpty(Path.GetExtension(path)) ? ".whs" : Path.GetExtension(path)));

        byte[] output = Whs.Repack(data, dec.Footer);

        // Safety: the repacked container must decompress cleanly.
        try { _ = Whs.Decompress(output); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Col.Red("ERROR: repacked file failed self-check: " + ex.Message));
            return 1;
        }

        File.WriteAllBytes(outPath, output);

        Console.WriteLine(Col.Green($"Fixed save written to: {outPath}"));
        Console.WriteLine($"  On-disk size: {output.Length:N0} bytes");
        Console.WriteLine();
        Console.WriteLine("IMPORTANT -- this is step 1 of 2. The file has NOT shrunk yet.");
        Console.WriteLine("  1. Copy the fixed file into your saves folder (back up first!).");
        Console.WriteLine("  2. Load it in-game -- it will load normally.");
        Console.WriteLine("  3. Fix the misbehaving NPC in-game if needed, then play briefly");
        Console.WriteLine("     and SAVE through the game's own menu.");
        Console.WriteLine("  4. The game re-writes the save WITHOUT the orphaned commands,");
        Console.WriteLine("     roughly halving the decompressed size.");
        Console.WriteLine("Run 'analyze' on that new in-game save to confirm it is healthy.");
        Console.WriteLine(bar);
        return 0;
    }

    // --- arg helpers --------------------------------------------------------
    private static bool SavePath(string[] args, out string path)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                // Skip the value only for flags that actually take one.
                if (TakesValue(args[i])) i++;
                continue;
            }
            path = args[i];
            return true;
        }
        path = "";
        return false;
    }

    // Options that consume the following token as their value.
    private static bool TakesValue(string flag) =>
        flag is "-o" or "--out" or "--threshold";

    private static uint Threshold(string[] args)
    {
        string? v = Option(args, "--threshold");
        return v != null && uint.TryParse(v, out uint t) ? t : SaveFixer.DefaultThreshold;
    }

    private static string? Option(string[] args, string name)
    {
        for (int i = 1; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static byte[] Read(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("file not found", path);
        return File.ReadAllBytes(path);
    }

    private static int Unknown(string mode)
    {
        Console.Error.WriteLine(Col.Red($"ERROR: unknown mode '{mode}'."));
        Usage();
        return 1;
    }

    private static int OkUsage() { Usage(); return 0; }

    private static void Usage()
    {
        Console.WriteLine("KCD Save Fixer -- diagnose & repair KCD1 .whs saves bloated by NPC command spam.");
        Console.WriteLine();
        Console.WriteLine("USAGE:");
        Console.WriteLine("  kcdsavefixer analyze <save.whs> [--threshold N]");
        Console.WriteLine("  kcdsavefixer fix     <save.whs> [-o out.whs] [--threshold N]");
        Console.WriteLine();
        Console.WriteLine("  analyze   Read-only. Prints mailbox frequencies and a verdict.");
        Console.WriteLine("  fix       Zeroes bloated mailbox counters, writes a NEW .whs file.");
        Console.WriteLine($"            Default threshold: {SaveFixer.DefaultThreshold}.");
        Console.WriteLine();
        Console.WriteLine("Add --no-color to disable colored output (also honors the NO_COLOR env var).");
        Console.WriteLine();
        Console.WriteLine("Always back up your saves first.");
    }
}

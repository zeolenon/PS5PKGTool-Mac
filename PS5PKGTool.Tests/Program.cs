using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using PS5PKGTool.Ffpfsc;

// Dependency-free regression runner; throws (nonzero exit) on any failed check.
if (args is ["--gui-smoke"])
{
    Environment.ExitCode = NativeGuiChecks.Run();
    return;
}
if (args.Length == 2)
{
    VerifyContents(args[0], args[1]);
    Console.WriteLine("PASS: copied file paths, sizes and SHA-256 contents match the source; any regenerated AMPR index was checked separately.");
    return;
}
if (args.Length != 0) throw new ArgumentException("Usage: PS5PKGTool.Tests [source-directory image.exfat]");

string root = Path.Combine(Path.GetTempPath(), "ps5pkgtool-tests-" + Guid.NewGuid().ToString("N"));
try
{
    string source = Path.Combine(root, "test game 日本語");
    Directory.CreateDirectory(Path.Combine(source, "sce_sys"));
    Directory.CreateDirectory(Path.Combine(source, "data", "nested"));
    Directory.CreateDirectory(Path.Combine(source, "empty directory"));
    File.WriteAllBytes(Path.Combine(source, "eboot.bin"), [0, 1, 2, 255]);
    File.WriteAllText(Path.Combine(source, "sce_sys", "param.json"), "{\"titleId\":\"PPSA00000\"}");
    File.WriteAllBytes(Path.Combine(source, "data", "nested", "日本語.dat"), RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 17));
    File.WriteAllBytes(Path.Combine(source, "zero.bin"), []);
    string output = Path.Combine(root, "game.exfat");
    Check(await CliCommand.RunAsync(["build-exfat", source, output]) == 0, "CLI build failed");
    VerifyContents(source, output);
    byte[] originalHash = SHA256.HashData(File.ReadAllBytes(output));
    Check(await CliCommand.RunAsync(["build-exfat", source, output]) == 1, "Existing output was accepted");
    Check(originalHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(output))), "Existing output changed");
    Check(await CliCommand.RunAsync(["build-exfat", source, Path.Combine(source, "inside.exfat")]) == 1, "Output inside source was accepted");
    Check(await CliCommand.RunAsync(["build-exfat", Path.Combine(root, "missing"), Path.Combine(root, "missing.exfat")]) == 1, "Missing source was accepted");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    string cancelledOutput = Path.Combine(root, "cancelled.exfat");
    Check(await CliCommand.RunAsync(["build-exfat", source, cancelledOutput], cancellation.Token) == 130, "Cancellation was ignored");
    Check(!File.Exists(cancelledOutput), "Cancelled command published an image");
    string secondOutput = Path.Combine(root, "second.exfat");
    await ExfatImage.WriteDirectoryAsync(source, secondOutput);
    Check(originalHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(secondOutput))), "Non-AMPR output is not deterministic");
    Console.WriteLine("PASS: nested/Unicode/empty files and directories, streamed contents, deterministic output, overwrite protection, missing source, cancellation.");
    Directory.CreateDirectory(Path.Combine(source, "fakelib"));
    File.WriteAllBytes(Path.Combine(source, "fakelib", "libSceAmpr.sprx"), [1, 2, 3]);
    File.WriteAllText(Path.Combine(source, "ampr_emu.index"), "stale fixture index");
    string amprOutput = Path.Combine(root, "ampr.exfat");
    Check(await CliCommand.RunAsync(["build-exfat", source, amprOutput]) == 0, "AMPR image build failed");
    VerifyContents(source, amprOutput);
    Console.WriteLine("PASS: AMPR index regenerated; every indexed path/size and all copied contents match.");
    await PackageConversionChecks.RunAsync(root);
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static void VerifyContents(string source, string image)
{
    using var volume = new ExfatVolume(File.OpenRead(image));
    string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
    bool hasAmpr = File.Exists(Path.Combine(source, "fakelib", "libSceAmpr.sprx"));
    if (hasAmpr)
        files = files.Where(file => Path.GetRelativePath(source, file) is not ("ampr_emu.index" or "ampr_emu.index.tmp")).ToArray();
    Check(volume.Entries.Count(entry => !entry.IsDirectory) == files.Length + (hasAmpr ? 1 : 0), "File count mismatch");
    foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        Check(volume.Find(Path.GetRelativePath(source, directory).Replace(Path.DirectorySeparatorChar, '/'))?.IsDirectory == true, "Missing directory: " + directory);
    foreach (string file in files)
    {
        string relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
        Check(volume.Find(relative)?.Size == new FileInfo(file).Length, "Size mismatch: " + relative);
        using Stream actual = volume.OpenFile(relative);
        using Stream expected = File.OpenRead(file);
        Check(SHA256.HashData(actual).AsSpan().SequenceEqual(SHA256.HashData(expected)), "Content mismatch: " + relative);
    }
    if (hasAmpr)
    {
        using Stream indexStream = volume.OpenFile("ampr_emu.index");
        using var buffer = new MemoryStream();
        indexStream.CopyTo(buffer);
        byte[] index = buffer.ToArray();
        AmprIndexInfo info = AmprIndex.Inspect(index);
        Check(info.RecordCount == files.Length, "AMPR record count mismatch");
        var expected = files.ToDictionary(file => "/app0/" + Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/'),
            file => new FileInfo(file).Length, StringComparer.OrdinalIgnoreCase);
        int pathBase = checked(48 + info.RecordCount * 24);
        for (int row = 0; row < info.RecordCount; row++)
        {
            ReadOnlySpan<byte> record = index.AsSpan(48 + row * 24, 24);
            int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record));
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record[4..]));
            string path = Encoding.UTF8.GetString(index, pathBase + offset, length);
            long size = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(record[8..]));
            Check(expected.Remove(path, out long expectedSize) && expectedSize == size, "AMPR path/size mismatch: " + path);
        }
        Check(expected.Count == 0, "AMPR index omitted files");
        Console.WriteLine($"PASS: {files.Length} copied files hashed; {info.RecordCount} AMPR records validated.");
    }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

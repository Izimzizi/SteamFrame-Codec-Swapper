using System.Security.Cryptography;
using System.Text.Json;
using System.Buffers.Binary;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Tests;

// Former console harness; xUnit runs it through a [Fact] so it shows in Test Explorer and dotnet test.
internal static class PatcherHarness
{
    public static async Task RunAsync(string[] args)
    {
        var source = args.Length is 1 or 2 ? args[0] : throw new Exception("Pass the legacy original fixture and optionally the current original fixture.");
        var passed = new List<string>();
        var fixture = await File.ReadAllBytesAsync(source);
        Check(Hash(fixture) == BitratePatcher.SupportedOriginalSha256, "fixture hash");
        var manifest = PatchManifest.LoadSupported();
        var root = Path.Combine(Path.GetTempPath(), "SteamLinkVRSettings-patcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var patcher = new BitratePatcher(manifest, () => false);
            var path = await Copy("roundtrip");
            var status = await patcher.InspectAsync(path);
            Check(status.State == BitratePatchState.Original && status.CanApply && !status.CanRestore, "original inspection");
            Check((await patcher.InspectAsync(Path.Combine(root, "missing.dll"))).State == BitratePatchState.Missing, "missing inspection");
            status = await patcher.ApplyAsync(path);
            Check(status.IsPatched && status.CanRestore && !status.CanApply, "apply status");
            Check(Hash(await File.ReadAllBytesAsync(path)) == BitratePatcher.SupportedPatchedSha256, "exact patched output hash");
            Check((await File.ReadAllBytesAsync(status.BackupPath)).SequenceEqual(fixture), "exact original backup");
            var before = File.GetLastWriteTimeUtc(path);
            await patcher.ApplyAsync(path);
            Check(File.GetLastWriteTimeUtc(path) == before, "apply idempotency");
            status = await patcher.RestoreAsync(path);
            Check(status.State == BitratePatchState.Original && (await File.ReadAllBytesAsync(path)).SequenceEqual(fixture), "restore exact original");
            before = File.GetLastWriteTimeUtc(path);
            await patcher.RestoreAsync(path);
            Check(File.GetLastWriteTimeUtc(path) == before, "restore idempotency");
            await patcher.ApplyAsync(path);
            Check((await patcher.InspectAsync(path)).IsPatched, "reapply with verified backup");

            path = await Copy("unknown");
            var unknown = (byte[])fixture.Clone(); unknown[manifest.Changes[0].FileOffset] ^= 1;
            await File.WriteAllBytesAsync(path, unknown);
            Check((await patcher.InspectAsync(path)).State == BitratePatchState.Unsupported, "unknown inspection");
            await Refuses(() => patcher.ApplyAsync(path), "unknown apply refusal");
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(unknown) && !File.Exists(BitratePatcher.GetBackupPath(path)), "unknown untouched");

            path = await Copy("bad-backup");
            await File.WriteAllBytesAsync(BitratePatcher.GetBackupPath(path), unknown);
            Check((await patcher.InspectAsync(path)).BackupState == BitrateBackupState.Mismatch, "mismatch backup inspection");
            await Refuses(() => patcher.ApplyAsync(path), "mismatch backup apply refusal");
            await Refuses(() => patcher.RestoreAsync(path), "mismatch backup restore refusal");
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(fixture) &&
                  (await File.ReadAllBytesAsync(BitratePatcher.GetBackupPath(path))).SequenceEqual(unknown), "mismatch backup preserved");

            path = await Copy("no-backup");
            await patcher.ApplyAsync(path);
            File.Delete(BitratePatcher.GetBackupPath(path));
            Check(!(await patcher.InspectAsync(path)).CanRestore, "missing backup disables restore");
            await Refuses(() => patcher.RestoreAsync(path), "missing backup restore refusal");

            path = await Copy("running");
            var running = new BitratePatcher(manifest, () => true);
            await Refuses(() => running.ApplyAsync(path), "running SteamVR refusal");
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(fixture) && !File.Exists(BitratePatcher.GetBackupPath(path)), "running refusal untouched");

            path = await Copy("start-at-commit");
            var processChecks = 0;
            var startsAtCommit = new BitratePatcher(manifest, () => ++processChecks >= 3);
            await Refuses(() => startsAtCommit.ApplyAsync(path), "SteamVR starts before atomic replace refusal");
            Check(processChecks == 3 && (await File.ReadAllBytesAsync(path)).SequenceEqual(fixture), "actual replacement guard left target unchanged");

            path = await Copy("preimage");
            var modifiedManifest = new PatchManifest
            {
                SchemaVersion = manifest.SchemaVersion, FileLength = manifest.FileLength,
                CapMbps = manifest.CapMbps, HevcLevel = manifest.HevcLevel,
                OriginalSha256 = manifest.OriginalSha256, PatchedSha256 = manifest.PatchedSha256,
                Changes = manifest.Changes.Select((segment, index) => new PatchSegment
                {
                    FileOffset = segment.FileOffset, OriginalHex = index == 0 ? "01006843" : segment.OriginalHex,
                    PatchedHex = segment.PatchedHex, Reason = segment.Reason
                }).ToArray()
            };
            await Refuses(() => new BitratePatcher(modifiedManifest, () => false).ApplyAsync(path), "segment preimage refusal");
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(fixture) && !File.Exists(BitratePatcher.GetBackupPath(path)), "preimage refusal untouched");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            path = await Copy("cancelled");
            await Refuses(() => patcher.ApplyAsync(path, cancellation.Token), "cancelled operation refusal");

            var discovered = AdaptivePatchScanner.Discover(fixture);
            Check(discovered.DescribesSamePatch(manifest), "adaptive discovery reproduces legacy exact patch");
            var shiftedRaw = ShiftRawSections(fixture);
            var shifted = AdaptivePatchScanner.Discover(shiftedRaw);
            Check(shifted.FileLength == fixture.Length + 512 && shifted.Changes.Any(x => x.FileOffset == 0xc3ef0c + 512), "adaptive discovery follows shifted raw sections");
            path = Path.Combine(root, "shifted-raw.dll");
            await File.WriteAllBytesAsync(path, shiftedRaw);
            status = await patcher.ApplyAsync(path);
            Check(status.IsPatched && Hash(await File.ReadAllBytesAsync(path)) == shifted.PatchedSha256, "adaptive shifted apply exact output");
            Check(File.Exists(status.ReceiptPath) && Hash(await File.ReadAllBytesAsync(status.BackupPath)) == shifted.OriginalSha256, "adaptive backup and receipt saved");
            Check((await new BitratePatcher(manifest, () => false).InspectAsync(path)).IsPatched, "fresh instance recognizes adaptive patched receipt");
            await patcher.RestoreAsync(path);
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(shiftedRaw), "adaptive restore preserves exact shifted original");
            await patcher.ApplyAsync(path);
            var goodReceipt = await File.ReadAllTextAsync(status.ReceiptPath);
            await File.WriteAllTextAsync(status.ReceiptPath, goodReceipt.Replace(shifted.PatchedSha256, new string('a', 64)));
            await Refuses(() => patcher.RestoreAsync(path), "tampered adaptive receipt refusal");
            Check(Hash(await File.ReadAllBytesAsync(path)) == shifted.PatchedSha256, "tampered receipt left driver untouched");
            await File.WriteAllTextAsync(status.ReceiptPath, goodReceipt);
            await File.WriteAllBytesAsync(status.BackupPath, fixture);
            await Refuses(() => patcher.RestoreAsync(path), "wrong-build adaptive backup refusal");
            Check(Hash(await File.ReadAllBytesAsync(path)) == shifted.PatchedSha256, "wrong-build backup left driver untouched");

            var shiftedRvas = ShiftRvas(fixture);
            var relocatedPlan = AdaptivePatchScanner.Discover(shiftedRvas);
            Check(relocatedPlan.OriginalSha256 == Hash(shiftedRvas) && relocatedPlan.Changes[4].FileOffset == 0xa21b8d,
                  "adaptive discovery resolves moved RVAs imports and function table");
            path = Path.Combine(root, "shifted-rvas.dll");
            await File.WriteAllBytesAsync(path, shiftedRvas);
            status = await patcher.ApplyAsync(path);
            Check(Hash(await File.ReadAllBytesAsync(path)) == relocatedPlan.PatchedSha256, "relocated RVA apply exact output");
            await patcher.RestoreAsync(path);
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(shiftedRvas), "relocated RVA exact restore");

            var ambiguous = (byte[])fixture.Clone();
            fixture.AsSpan(0xa1d420 - 0xc00, 46).CopyTo(ambiguous.AsSpan(0x1400));
            path = Path.Combine(root, "ambiguous.dll");
            await File.WriteAllBytesAsync(path, ambiguous);
            await Refuses(() => patcher.ApplyAsync(path), "ambiguous instruction signature refused");
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(ambiguous) && !Directory.EnumerateFiles(root, "ambiguous.dll*.bak").Any(), "ambiguity refusal untouched and no backup");
            var changedApi = (byte[])fixture.Clone(); changedApi[0xa21cb6 - 0xc00 + 2] = 3;
            path = Path.Combine(root, "changed-api.dll"); await File.WriteAllBytesAsync(path, changedApi);
            await Refuses(() => patcher.ApplyAsync(path), "changed NVENC API contract refused");
            var changedImport = (byte[])fixture.Clone(); var importedName = Find(changedImport, System.Text.Encoding.ASCII.GetBytes("memcpy\0"));
            Check(importedName > 0, "fixture memcpy import located"); changedImport[importedName + 5] = (byte)'z';
            path = Path.Combine(root, "changed-import.dll"); await File.WriteAllBytesAsync(path, changedImport);
            await Refuses(() => patcher.ApplyAsync(path), "non-memcpy import refused");
            var changedCrt = (byte[])fixture.Clone(); var crtName = Find(changedCrt, System.Text.Encoding.ASCII.GetBytes("VCRUNTIME140.dll\0"));
            Check(crtName > 0, "fixture CRT import located");
            System.Text.Encoding.ASCII.GetBytes("BAD").CopyTo(changedCrt, crtName + 9);
            path = Path.Combine(root, "non-crt-import.dll"); await File.WriteAllBytesAsync(path, changedCrt);
            await Refuses(() => patcher.ApplyAsync(path), "similarly named non-CRT DLL refused");

            if (args.Length == 2)
            {
                var current = await File.ReadAllBytesAsync(args[1]);
                Check(Hash(current) == BitratePatcher.SupportedCurrentOriginalSha256, "current fixture exact hash");
                var currentPlan = AdaptivePatchScanner.Discover(current);
                Check(currentPlan.PatchedSha256 == BitratePatcher.SupportedCurrentPatchedSha256, "adaptive discovery reproduces current pinned output");
                path = await Copy("simulated-update");
                await patcher.ApplyAsync(path); await patcher.RestoreAsync(path);
                var oldBackup = BitratePatcher.GetBackupPath(path); var oldReceipt = oldBackup + ".patch.json";
                var oldReceiptBytes = await File.ReadAllBytesAsync(oldReceipt);
                await File.WriteAllBytesAsync(path, current); // Simulate Steam updating only our temporary target.
                status = await patcher.InspectAsync(path);
                Check(status.State == BitratePatchState.Original && status.BackupPath != oldBackup && status.CanApply, "Steam update receives separate backup namespace");
                status = await patcher.ApplyAsync(path);
                Check(Hash(await File.ReadAllBytesAsync(path)) == BitratePatcher.SupportedCurrentPatchedSha256, "current build exact patched output");
                Check((await File.ReadAllBytesAsync(status.BackupPath)).SequenceEqual(current), "current build exact backup");
                Check((await File.ReadAllBytesAsync(oldBackup)).SequenceEqual(fixture) && (await File.ReadAllBytesAsync(oldReceipt)).SequenceEqual(oldReceiptBytes), "older build backup and receipt preserved through update");
                var currentPatchedTime = File.GetLastWriteTimeUtc(path); await patcher.ApplyAsync(path);
                Check(File.GetLastWriteTimeUtc(path) == currentPatchedTime, "current apply idempotency");
                await File.WriteAllBytesAsync(status.BackupPath, fixture);
                await Refuses(() => patcher.RestoreAsync(path), "mixed known-build backup refuses restore");
                Check(Hash(await File.ReadAllBytesAsync(path)) == BitratePatcher.SupportedCurrentPatchedSha256 && (await File.ReadAllBytesAsync(status.BackupPath)).SequenceEqual(fixture), "mixed known-build refusal preserves target and backup");
                await File.WriteAllBytesAsync(status.BackupPath, current);
                await patcher.RestoreAsync(path);
                Check((await File.ReadAllBytesAsync(path)).SequenceEqual(current), "current build restores exact current original");
            }
            Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "no staging leftovers");
            Console.WriteLine(JsonSerializer.Serialize(new { passed = passed.Count, tests = passed, temporaryOnly = true }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            // Verify the resolved cleanup target belongs to this explicit temporary namespace.
            var fullRoot = Path.GetFullPath(root);
            var prefix = Path.GetFullPath(Path.GetTempPath()) + "SteamLinkVRSettings-patcher-";
            if (fullRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(fullRoot, recursive: true);
        }

        async Task<string> Copy(string label)
        {
            var destination = Path.Combine(root, label + "-driver_vrlink.dll");
            await File.WriteAllBytesAsync(destination, fixture);
            return destination;
        }
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAILED: " + name);
            passed?.Add(name);
        }
        async Task Refuses(Func<Task<BitratePatchStatus>> action, string name)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or OperationCanceledException)
            { passed.Add(name); return; }
            throw new Exception("FAILED: " + name);
        }
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        static int Find(byte[] data, byte[] needle)
        {
            for (int i = 0; i <= data.Length - needle.Length; i++) if (data.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
            return -1;
        }
        static byte[] ShiftRawSections(byte[] original)
        {
            var pe = checked((int)Read(original, 0x3c)); var opt = pe + 24; var count = BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(pe + 6));
            var sectionTable = opt + BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(pe + 20)); var headers = checked((int)Read(original, opt + 60));
            var shifted = new byte[original.Length + 512]; original.AsSpan(0, headers).CopyTo(shifted); original.AsSpan(headers).CopyTo(shifted.AsSpan(headers + 512));
            Write(shifted, opt + 60, (uint)headers + 512);
            for (int i = 0; i < count; i++) { int field = sectionTable + i * 40 + 20; Write(shifted, field, Read(original, field) + 512); }
            var certificate = opt + 112 + 4 * 8; if (Read(original, certificate) != 0) Write(shifted, certificate, Read(original, certificate) + 512);
            return shifted;
        }
        static byte[] ShiftRvas(byte[] original)
        {
            const uint delta = 0x10000; var shifted = (byte[])original.Clone(); var pe = checked((int)Read(original, 0x3c)); var opt = pe + 24;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(pe + 6)); var sectionTable = opt + BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(pe + 20));
            for (int i = 0; i < count; i++) { int field = sectionTable + i * 40 + 12; Write(shifted, field, Read(original, field) + delta); }
            Write(shifted, opt + 56, Read(original, opt + 56) + delta); Write(shifted, opt + 16, Read(original, opt + 16) + delta);
            for (int i = 0; i < 16; i++) { int field = opt + 112 + i * 8; if (i != 4 && Read(original, field) != 0) Write(shifted, field, Read(original, field) + delta); }
            var exception = Offset(Read(original, opt + 112 + 3 * 8)); var exceptionSize = Read(original, opt + 116 + 3 * 8);
            for (int p = exception; p < exception + exceptionSize; p += 12) for (int j = 0; j < 3; j++) Write(shifted, p + j * 4, Read(original, p + j * 4) + delta);
            var imports = Offset(Read(original, opt + 112 + 8));
            for (int p = imports; Read(original, p + 12) != 0; p += 20)
            {
                foreach (int field in new[] { 0, 12, 16 }) Write(shifted, p + field, Read(original, p + field) + delta);
                foreach (int field in new[] { 0, 16 })
                {
                    var table = Offset(Read(original, p + field));
                    for (int entry = table; ; entry += 8)
                    {
                        var value = BinaryPrimitives.ReadUInt64LittleEndian(original.AsSpan(entry)); if (value == 0) break;
                        if ((value & (1ul << 63)) == 0) BinaryPrimitives.WriteUInt64LittleEndian(shifted.AsSpan(entry), value + delta);
                    }
                }
            }
            return shifted;
            int Offset(uint rva)
            {
                for (int i = 0; i < count; i++)
                {
                    var h = sectionTable + i * 40; var start = Read(original, h + 12); var size = Read(original, h + 16);
                    if (rva >= start && rva - start < size) return checked((int)(Read(original, h + 20) + rva - start));
                }
                throw new Exception("Synthetic relocation used invalid RVA.");
            }
        }
        static uint Read(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        static void Write(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    }
}

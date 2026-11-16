using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
#if !NATIVE_RENAMER_ONLY
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
#endif

namespace HomiliaMessenger.Build;

/// <summary>
/// Renames the libraries a native library depends on, in place, so a private copy of a shared dependency can be loaded
/// beside another copy of the same name. stable-diffusion.cpp and LLamaSharp each ship their own, incompatible
/// <c>ggml</c>, and the Windows and Linux loaders bind a dependency to an already loaded library of the same name, so
/// the second runtime loaded would otherwise use the first one's <c>ggml</c> and fail.
/// </summary>
/// <remarks>
/// Only dependency and library names are rewritten, never code: import and export directory names in a PE image, and
/// <c>DT_NEEDED</c> and <c>DT_SONAME</c> strings in a 64-bit ELF image. A new name must not be longer than the old one,
/// so nothing moves. Shared with the build (see <c>StableDiffusionRuntime.targets</c>), which uses it on the pinned
/// runtime release and fails when an expected name is missing, as it would after an unreviewed runtime upgrade.
/// </remarks>
public static class NativeLibraryRenamer
{
    /// <summary>
    /// Rewrites the names in <paramref name="image"/> that match a key of <paramref name="renames"/> (ignoring case on
    /// Windows) to the key's value.
    /// </summary>
    /// <returns>The names that were rewritten, as found in the image.</returns>
    /// <exception cref="InvalidDataException">The image is not a PE32+ or 64-bit little-endian ELF image.</exception>
    public static IReadOnlyList<string> Rename(byte[] image, IReadOnlyDictionary<string, string> renames)
    {
        if (image == null)
        {
            throw new ArgumentNullException(nameof(image));
        }
        if (renames == null)
        {
            throw new ArgumentNullException(nameof(renames));
        }

        foreach (KeyValuePair<string, string> rename in renames)
        {
            if (rename.Value.Length > rename.Key.Length)
            {
                throw new ArgumentException($"{rename.Value} is longer than {rename.Key}; names can only be rewritten in place.", nameof(renames));
            }
        }

        List<string> renamed = [];

        foreach (int offset in NameOffsets(image))
        {
            string name = ReadString(image, offset);

            foreach (KeyValuePair<string, string> rename in renames)
            {
                if (string.Equals(name, rename.Key, IsPe(image) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    byte[] replacement = Encoding.ASCII.GetBytes(rename.Value);
                    Array.Clear(image, offset, name.Length);
                    Array.Copy(replacement, 0, image, offset, replacement.Length);
                    renamed.Add(name);
                    break;
                }
            }
        }

        return renamed;
    }

    /// <summary>
    /// The names of the libraries <paramref name="image"/> depends on, and its own name when it records one.
    /// </summary>
    public static IReadOnlyList<string> Names(byte[] image)
    {
        if (image == null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        List<string> names = [];

        foreach (int offset in NameOffsets(image))
        {
            names.Add(ReadString(image, offset));
        }

        return names;
    }

    private static bool IsPe(byte[] image)
    {
        return image.Length > 0x40 && image[0] == (byte)'M' && image[1] == (byte)'Z';
    }

    private static bool IsElf(byte[] image)
    {
        return image.Length > 0x40 && image[0] == 0x7f && image[1] == (byte)'E' && image[2] == (byte)'L' && image[3] == (byte)'F';
    }

    private static List<int> NameOffsets(byte[] image)
    {
        if (IsPe(image))
        {
            return PeNameOffsets(image);
        }

        if (IsElf(image))
        {
            return ElfNameOffsets(image);
        }

        throw new InvalidDataException("Not a PE or ELF image.");
    }

    /// <summary>
    /// File offsets of the import, delay-import, and export directory names of a PE32+ image.
    /// </summary>
    private static List<int> PeNameOffsets(byte[] image)
    {
        int pe = ReadInt32(image, 0x3c);

        if (pe <= 0 || pe + 24 > image.Length || ReadInt32(image, pe) != 0x00004550)
        {
            throw new InvalidDataException("Not a PE image.");
        }

        int sections = ReadUInt16(image, pe + 6);
        int optionalSize = ReadUInt16(image, pe + 20);
        int optional = pe + 24;

        if (ReadUInt16(image, optional) != 0x20b)
        {
            throw new InvalidDataException("Only 64-bit (PE32+) images are supported.");
        }

        int directories = optional + 112;
        int sectionTable = optional + optionalSize;
        List<int> offsets = [];

        int Offset(uint rva)
        {
            for (int index = 0; index < sections; index++)
            {
                int header = sectionTable + (index * 40);
                uint virtualSize = ReadUInt32(image, header + 8);
                uint virtualAddress = ReadUInt32(image, header + 12);
                uint rawSize = ReadUInt32(image, header + 16);
                uint rawPointer = ReadUInt32(image, header + 20);

                if (rva >= virtualAddress && rva < virtualAddress + Math.Max(virtualSize, rawSize))
                {
                    return checked((int)(rva - virtualAddress + rawPointer));
                }
            }

            throw new InvalidDataException($"RVA 0x{rva:x} is outside every section.");
        }

        uint exportRva = ReadUInt32(image, directories);

        if (exportRva != 0)
        {
            offsets.Add(Offset(ReadUInt32(image, Offset(exportRva) + 12)));
        }

        uint importRva = ReadUInt32(image, directories + 8);

        if (importRva != 0)
        {
            for (int descriptor = Offset(importRva); ; descriptor += 20)
            {
                uint name = ReadUInt32(image, descriptor + 12);

                if (name == 0 && ReadUInt32(image, descriptor + 16) == 0)
                {
                    break;
                }

                offsets.Add(Offset(name));
            }
        }

        uint delayRva = ReadUInt32(image, directories + (13 * 8));

        if (delayRva != 0)
        {
            for (int descriptor = Offset(delayRva); ; descriptor += 32)
            {
                uint name = ReadUInt32(image, descriptor + 4);

                if (name == 0)
                {
                    break;
                }

                offsets.Add(Offset(name));
            }
        }

        return offsets;
    }

    /// <summary>
    /// File offsets of the <c>DT_NEEDED</c> and <c>DT_SONAME</c> strings of a 64-bit little-endian ELF image.
    /// </summary>
    private static List<int> ElfNameOffsets(byte[] image)
    {
        if (image[4] != 2 || image[5] != 1)
        {
            throw new InvalidDataException("Only 64-bit little-endian ELF images are supported.");
        }

        long programHeaders = ReadInt64(image, 0x20);
        int entrySize = ReadUInt16(image, 0x36);
        int count = ReadUInt16(image, 0x38);
        List<(long Address, long Offset, long Size)> loads = [];
        long dynamic = -1;
        long dynamicSize = 0;

        for (int index = 0; index < count; index++)
        {
            int header = checked((int)(programHeaders + (index * entrySize)));
            uint type = ReadUInt32(image, header);

            if (type == 1)
            {
                loads.Add((ReadInt64(image, header + 16), ReadInt64(image, header + 8), ReadInt64(image, header + 32)));
            }
            else if (type == 2)
            {
                dynamic = ReadInt64(image, header + 8);
                dynamicSize = ReadInt64(image, header + 32);
            }
        }

        if (dynamic < 0)
        {
            return [];
        }

        long strings = -1;
        List<long> names = [];

        for (long entry = dynamic; entry + 16 <= dynamic + dynamicSize; entry += 16)
        {
            long tag = ReadInt64(image, checked((int)entry));
            long value = ReadInt64(image, checked((int)entry + 8));

            if (tag == 0)
            {
                break;
            }

            if (tag == 5)
            {
                strings = value;
            }
            else if (tag is 1 or 14)
            {
                names.Add(value);
            }
        }

        if (strings < 0)
        {
            throw new InvalidDataException("The ELF image has no string table.");
        }

        foreach ((long address, long offset, long size) in loads)
        {
            if (strings >= address && strings < address + size)
            {
                long table = strings - address + offset;
                return names.ConvertAll(name => checked((int)(table + name)));
            }
        }

        throw new InvalidDataException("The ELF string table is outside every loaded segment.");
    }

    private static string ReadString(byte[] image, int offset)
    {
        int end = Array.IndexOf(image, (byte)0, offset);

        if (end < 0)
        {
            throw new InvalidDataException("A name is not terminated.");
        }

        return Encoding.ASCII.GetString(image, offset, end - offset);
    }

    private static int ReadUInt16(byte[] image, int offset)
    {
        return image[offset] | (image[offset + 1] << 8);
    }

    private static int ReadInt32(byte[] image, int offset)
    {
        return BitConverter.ToInt32(image, offset);
    }

    private static uint ReadUInt32(byte[] image, int offset)
    {
        return BitConverter.ToUInt32(image, offset);
    }

    private static long ReadInt64(byte[] image, int offset)
    {
        return BitConverter.ToInt64(image, offset);
    }
}

#if !NATIVE_RENAMER_ONLY
/// <summary>
/// Copies native libraries to <see cref="OutputDirectory"/>, renaming each file and the dependency names inside it, and
/// fails when a library does not depend on a renamed library or still names an old one afterward.
/// </summary>
public sealed class RenameNativeLibraries : Microsoft.Build.Utilities.Task
{
    /// <summary>
    /// The libraries to copy. An item's <c>TargetName</c> metadata is its new file name; without it, its name is kept.
    /// </summary>
    [Required]
    public ITaskItem[] Libraries { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>
    /// The dependency names to rewrite. Each item is an old name, with its new one in <c>NewName</c> metadata.
    /// </summary>
    [Required]
    public ITaskItem[] Renames { get; set; } = Array.Empty<ITaskItem>();

    [Required]
    public string OutputDirectory { get; set; } = string.Empty;

    public override bool Execute()
    {
        Dictionary<string, string> renames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (ITaskItem rename in Renames)
        {
            renames[rename.ItemSpec] = rename.GetMetadata("NewName");
        }

        Directory.CreateDirectory(OutputDirectory);

        foreach (ITaskItem library in Libraries)
        {
            byte[] image = File.ReadAllBytes(library.ItemSpec);
            IReadOnlyList<string> renamed = NativeLibraryRenamer.Rename(image, renames);
            string fileName = Path.GetFileName(library.ItemSpec);

            if (renamed.Count == 0)
            {
                Log.LogError("{0} names none of the libraries to rename. The image generation runtime may have changed; review it before updating the build.", fileName);
                continue;
            }

            foreach (string name in NativeLibraryRenamer.Names(image))
            {
                if (renames.ContainsKey(name) || renames.Keys.Any(old => string.Equals(old, name, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.LogError("{0} still names {1} after renaming.", fileName, name);
                }
            }

            string target = library.GetMetadata("TargetName");
            File.WriteAllBytes(Path.Combine(OutputDirectory, string.IsNullOrEmpty(target) ? fileName : target), image);
            Log.LogMessage(MessageImportance.Low, "{0}: renamed {1}.", fileName, string.Join(", ", renamed));
        }

        return !Log.HasLoggedErrors;
    }
}
#endif

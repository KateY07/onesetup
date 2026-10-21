using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OneSetup;

// Replaces the icon of an .exe (Windows resource update): used to give a packaged setup.exe the project's own icon.
static class IconResource
{
    const int RtIcon = 3, RtGroupIcon = 14, FirstImageId = 101;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr BeginUpdateResourceW(string fileName, [MarshalAs(UnmanagedType.Bool)] bool deleteExistingResources);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateResourceW(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[]? data, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool EndUpdateResourceW(IntPtr update, [MarshalAs(UnmanagedType.Bool)] bool discard);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryExW(string fileName, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll")]
    static extern IntPtr LockResource(IntPtr resource);

    [DllImport("kernel32.dll")]
    static extern uint SizeofResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr module);

    // Image ids of the icon group the executable has now. Deleting an id that does not exist makes the final save fail
    // (error 1359), so only these are removed.
    static List<ushort> ExistingImageIds(string exePath)
    {
        List<ushort> ids = [];
        IntPtr module = LoadLibraryExW(exePath, IntPtr.Zero, 0x2);   // LOAD_LIBRARY_AS_DATAFILE
        if (module == IntPtr.Zero) return ids;
        try
        {
            IntPtr resource = FindResourceW(module, 1, RtGroupIcon);
            if (resource == IntPtr.Zero) return ids;
            uint size = SizeofResource(module, resource);
            IntPtr pointer = LockResource(LoadResource(module, resource));
            if (pointer == IntPtr.Zero || size < 6) return ids;
            byte[] directory = new byte[size];
            Marshal.Copy(pointer, directory, 0, (int)size);
            int count = BitConverter.ToUInt16(directory, 4);
            for (int index = 0; index < count && 6 + 14 * (index + 1) <= size; index++)
                ids.Add(BitConverter.ToUInt16(directory, 6 + 14 * index + 12));
        }
        finally { FreeLibrary(module); }
        return ids;
    }

    public static void Apply(string exePath, string icoPath)
    {
        byte[] ico = File.ReadAllBytes(icoPath);
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 0) != 0 || BitConverter.ToUInt16(ico, 2) != 1)
            throw new ArgumentException("--icon must be a Windows .ico file.");
        int count = BitConverter.ToUInt16(ico, 4);
        if (count is < 1 or > 32 || ico.Length < 6 + 16 * count)
            throw new ArgumentException("--icon is not a valid .ico file (image count).");

        List<ushort> previousImages = ExistingImageIds(exePath);
        IntPtr update = BeginUpdateResourceW(exePath, false);
        if (update == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the executable to set its icon.");
        try
        {
            using MemoryStream group = new();
            using BinaryWriter writer = new(group);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)count);
            for (int index = 0; index < count; index++)
            {
                int entry = 6 + 16 * index;
                uint size = BitConverter.ToUInt32(ico, entry + 8), offset = BitConverter.ToUInt32(ico, entry + 12);
                if (size == 0 || (long)offset + size > ico.Length) throw new ArgumentException("--icon is not a valid .ico file (image data).");
                byte[] image = ico[(int)offset..(int)(offset + size)];
                ushort id = (ushort)(FirstImageId + index);
                if (!UpdateResourceW(update, RtIcon, id, 0, image, size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                writer.Write(ico[entry]);          // width
                writer.Write(ico[entry + 1]);      // height
                writer.Write(ico[entry + 2]);      // colour count
                writer.Write((byte)0);
                writer.Write(BitConverter.ToUInt16(ico, entry + 4));   // planes
                writer.Write(BitConverter.ToUInt16(ico, entry + 6));   // bit count
                writer.Write(size);
                writer.Write(id);
            }
            foreach (ushort id in previousImages.Where(id => id < FirstImageId))   // drop the previous images
                UpdateResourceW(update, RtIcon, id, 0, null, 0);
            byte[] directory = group.ToArray();
            if (!UpdateResourceW(update, RtGroupIcon, 1, 0, directory, (uint)directory.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            EndUpdateResourceW(update, true);
            throw;
        }
        if (!EndUpdateResourceW(update, false)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the icon into the executable.");
    }
}

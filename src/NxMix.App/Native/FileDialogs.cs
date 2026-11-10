using System.Runtime.InteropServices;

namespace NxMix.App.Native;

/// <summary>Windows-Dateidialoge (Speichern/Oeffnen) fuer Projekt-Export und -Import.</summary>
internal static unsafe partial class FileDialogs
{
    [StructLayout(LayoutKind.Sequential)]
    struct OPENFILENAMEW
    {
        public uint lStructSize;
        public nint hwndOwner, hInstance;
        public char* lpstrFilter, lpstrCustomFilter;
        public uint nMaxCustFilter, nFilterIndex;
        public char* lpstrFile;
        public uint nMaxFile;
        public char* lpstrFileTitle;
        public uint nMaxFileTitle;
        public char* lpstrInitialDir, lpstrTitle;
        public uint Flags;
        public ushort nFileOffset, nFileExtension;
        public char* lpstrDefExt;
        public nint lCustData, lpfnHook;
        public char* lpTemplateName;
        public nint pvReserved;
        public uint dwReserved, FlagsEx;
    }

    [LibraryImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOpenFileName(ref OPENFILENAMEW ofn);

    [LibraryImport("comdlg32.dll", EntryPoint = "GetSaveFileNameW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSaveFileName(ref OPENFILENAMEW ofn);

    const uint OFN_OVERWRITEPROMPT = 0x2, OFN_NOCHANGEDIR = 0x8, OFN_PATHMUSTEXIST = 0x800, OFN_FILEMUSTEXIST = 0x1000, OFN_EXPLORER = 0x80000;

    static string? Run(nint owner, string title, string filter, string initialName, bool save)
    {
        const int cap = 1024;
        char* file = stackalloc char[cap];
        for (int i = 0; i < cap; i++) file[i] = '\0';
        for (int i = 0; i < Math.Min(initialName.Length, cap - 1); i++) file[i] = initialName[i];
        string nativeFilter = filter.Replace('|', '\0') + "\0\0";
        fixed (char* pFilter = nativeFilter)
        fixed (char* pTitle = title)
        fixed (char* pExt = "json")
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW),
                hwndOwner = owner,
                lpstrFilter = pFilter,
                nFilterIndex = 1,
                lpstrFile = file,
                nMaxFile = cap,
                lpstrTitle = pTitle,
                lpstrDefExt = pExt,
                Flags = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_PATHMUSTEXIST | (save ? OFN_OVERWRITEPROMPT : OFN_FILEMUSTEXIST),
            };
            bool ok = save ? GetSaveFileName(ref ofn) : GetOpenFileName(ref ofn);
            return ok ? new string(file) : null;
        }
    }

    public static string? Save(nint owner, string title, string filter, string defaultName) => Run(owner, title, filter, defaultName, true);
    public static string? Open(nint owner, string title, string filter) => Run(owner, title, filter, "", false);
}

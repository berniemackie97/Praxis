using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Praxis.Domain.Common;

namespace Praxis.Infrastructure.FileSystem;

internal sealed class OriginalExportDestination : IDisposable
{
    private const int BufferLength = 64 * 1024;
    private const int ExistingFileError = 17;
    private const int MissingFileError = 2;
    private const int OwnerFileMode = 0x180; // 0600
    private const int MacDirectoryOpenFlags = 0x00100000 | 0x00000100 | 0x01000000;
    private const int LinuxDirectoryOpenFlags = 0x00010000 | 0x00020000 | 0x00080000;
    private const int MacCloseOnExec = 0x01000000;
    private const int MacVnodePathInfo = 2;
    private const int MacPathLength = 1024;
    private const int LinuxStageOpenFlags = 0x00000002 | 0x00000040 | 0x00000080 | 0x00020000 | 0x00080000;
    private const uint MacExclusiveRename = 0x4;
    private const uint LinuxExclusiveRename = 0x1;
    private readonly string folder;
    private readonly string name;
    private readonly string catalog;
    private readonly SafeFileHandle? directoryHandle;
    private string? stagedName;

    private OriginalExportDestination(string folder, string name, string catalog, SafeFileHandle? directoryHandle)
    {
        this.folder = folder;
        this.name = name;
        this.catalog = catalog;
        this.directoryHandle = directoryHandle;
    }

    public static OriginalExportDestination Open(string destinationPath, string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException("The export destination must be absolute.", nameof(destinationPath));
        }

        string requested = Path.GetFullPath(destinationPath);
        string requestedFolder = Path.GetDirectoryName(requested)!;
        if (!Directory.Exists(requestedFolder))
        {
            throw new DirectoryNotFoundException(requestedFolder);
        }

        string catalog = FileSystemPaths.ResolveExisting(dataDirectory);
        string folder = FileSystemPaths.ResolveExisting(requestedFolder);
        string name = Path.GetFileName(requested);
        if (IsInsideCatalog(catalog, Path.Combine(folder, name)))
        {
            throw new InvalidOperationException("An export cannot be written inside the private catalog.");
        }

        if (OperatingSystem.IsWindows())
        {
            return new OriginalExportDestination(folder, name, catalog, null);
        }

        int flags = OperatingSystem.IsMacOS() ? MacDirectoryOpenFlags : LinuxDirectoryOpenFlags;
        int fd = OpenDirectory(folder, flags);
        if (fd < 0)
        {
            throw Failure("The export directory could not be opened.");
        }

        SafeFileHandle handle = new((IntPtr)fd, ownsHandle: true);
        OriginalExportDestination destination = new(folder, name, catalog, handle);
        try
        {
            destination.ValidateDirectory();
            return destination;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public FileStream CreateStaging()
    {
        if (stagedName is not null)
        {
            throw new InvalidOperationException("The export already has a staging file.");
        }

        ValidateDirectory();
        string pending = ".praxis-export-" + Guid.NewGuid().ToString("N") + ".part";
        if (directoryHandle is null)
        {
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = BufferLength,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };
            FileStream stream = new(Path.Combine(folder, pending), options);
            stagedName = pending;
            return stream;
        }

        int fd;
        if (OperatingSystem.IsMacOS())
        {
            // mkostempsat_np is non-variadic and creates mode 0600 relative to the pinned directory.
            byte[] template = Encoding.UTF8.GetBytes(
                ".praxis-export-" + Guid.NewGuid().ToString("N") + "-XXXXXX.part\0");
            fd = CreateMacStage(DirectoryFd, template, ".part".Length, MacCloseOnExec);
            if (fd >= 0)
            {
                pending = Encoding.UTF8.GetString(template, 0, template.Length - 1);
            }
        }
        else
        {
            fd = OpenRelative(DirectoryFd, pending, LinuxStageOpenFlags, OwnerFileMode);
        }

        if (fd < 0)
        {
            throw Failure("The private export staging file could not be created.");
        }

        SafeFileHandle handle = new((IntPtr)fd, ownsHandle: true);
        try
        {
            FileStream stream = new(handle, FileAccess.ReadWrite, BufferLength, isAsync: false);
            stagedName = pending;
            return stream;
        }
        catch
        {
            handle.Dispose();
            if (UnlinkRelative(DirectoryFd, pending, 0) != 0)
            {
                throw Failure("The export staging file could not be removed.");
            }

            throw;
        }
    }

    public void Publish()
    {
        if (stagedName is null)
        {
            throw new InvalidOperationException("There is no verified staging file to publish.");
        }

        ValidateDirectory();
        if (directoryHandle is null)
        {
            File.Move(Path.Combine(folder, stagedName), Path.Combine(folder, name));
            return;
        }

        int result = OperatingSystem.IsMacOS()
            ? RenameExclusiveMac(DirectoryFd, stagedName, DirectoryFd, name, MacExclusiveRename)
            : RenameExclusiveLinux(DirectoryFd, stagedName, DirectoryFd, name, LinuxExclusiveRename);
        if (result != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw error == ExistingFileError
                ? new IOException("The export destination already exists and cannot be overwritten.")
                : Failure("The filesystem could not publish the export without replacing another file.", error);
        }
    }

    public void Dispose()
    {
        try
        {
            if (stagedName is { } pending)
            {
                if (directoryHandle is null)
                {
                    File.Delete(Path.Combine(folder, pending));
                }
                else if (UnlinkRelative(DirectoryFd, pending, 0) != 0 &&
                         Marshal.GetLastPInvokeError() != MissingFileError)
                {
                    throw Failure("The export staging file could not be removed.");
                }
            }
        }
        finally
        {
            directoryHandle?.Dispose();
        }
    }

    private int DirectoryFd => directoryHandle!.DangerousGetHandle().ToInt32();

    private void ValidateDirectory()
    {
        string actual;
        if (directoryHandle is null)
        {
            actual = FileSystemPaths.ResolveExisting(folder);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // proc_pidfdinfo has a fixed ABI. Its vnode_fdinfowithpath result ends
            // with vip_path[MAXPATHLEN], unlike variadic fcntl(F_GETPATH).
            byte[] descriptor = new byte[4096];
            int size = GetMacDescriptorInfo(Environment.ProcessId, DirectoryFd,
                MacVnodePathInfo, descriptor, descriptor.Length);
            if (size <= MacPathLength || size > descriptor.Length)
            {
                throw Failure("The export directory could not be verified.");
            }

            int start = size - MacPathLength;
            int end = Array.IndexOf(descriptor, (byte)0, start, MacPathLength);
            if (end <= start || descriptor[start] != (byte)'/')
            {
                throw new InvalidDataException("The export directory returned an invalid path.");
            }

            actual = FileSystemPaths.ResolveExisting(
                Encoding.UTF8.GetString(descriptor, start, end - start));
        }
        else
        {
            string descriptor = $"/proc/self/fd/{DirectoryFd}";
            string target = new FileInfo(descriptor).ResolveLinkTarget(true)?.FullName
                ?? throw new InvalidDataException("The export directory descriptor could not be resolved.");
            actual = FileSystemPaths.ResolveExisting(target);
        }

        if (IsInsideCatalog(catalog, Path.Combine(actual, name)))
        {
            throw new InvalidOperationException("An export cannot be written inside the private catalog.");
        }

        if (!string.Equals(folder, actual, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The export directory changed during export.");
        }
    }

    private static bool IsInsideCatalog(string root, string path) =>
        PathScope.Contains(root, path) ||
        (OperatingSystem.IsMacOS() &&
         PathScope.Contains(root.ToUpperInvariant(), path.ToUpperInvariant()));

    private static IOException Failure(string message, int? error = null)
        => new(message, new Win32Exception(error ?? Marshal.GetLastPInvokeError()));

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int OpenDirectory(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int OpenRelative(int directory, string name, int flags, int mode);

    [DllImport("libc", EntryPoint = "renameatx_np", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int RenameExclusiveMac(int sourceDirectory, string source, int targetDirectory, string target, uint flags);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int RenameExclusiveLinux(int sourceDirectory, string source, int targetDirectory, string target, uint flags);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int UnlinkRelative(int directory, string name, int flags);

    [DllImport("libproc", EntryPoint = "proc_pidfdinfo", SetLastError = true)]
    private static extern int GetMacDescriptorInfo(int pid, int fd, int flavor,
        [Out] byte[] buffer, int bufferSize);

    [DllImport("libc", EntryPoint = "mkostempsat_np", SetLastError = true)]
    private static extern int CreateMacStage(int directory, [In, Out] byte[] template,
        int suffixLength, int flags);
}

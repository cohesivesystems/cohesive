namespace Cohesive.Prelude;

/// <summary>
/// File-system helpers for <see cref="FilePath"/> values.
/// </summary>
public static class FilePathExtensions
{
    extension(FilePath)
    {
        /// <summary>
        /// Creates a file path in the temporary directory with the specified file name.
        /// </summary>
        /// <param name="fileName">The file name in the temporary directory.</param>
        /// <returns></returns>
        public static FilePath TempFilePath(string fileName) =>
            FilePath.File(Path.Combine(Path.GetTempPath(), fileName), Path.DirectorySeparatorChar);
    }
    
    extension(FilePath path)
    {
        /// <summary>
        /// Tests whether a file with this file path exists
        /// </summary>
        /// <returns>
        /// <c>true</c> if the caller has the required permissions and <paramref name="path"/> contains the name of an existing file; otherwise, false.<br />
        /// This method also returns false if <paramref name="path"/> is null, an invalid path, or a zero-length string.<br />
        /// If the caller does not have sufficient permissions to read the specified file, no exception is thrown and the method returns false regardless of the existence of <paramref name="path"/>.
        /// </returns>
        public bool FileExists()
        {
            EnsureFilePath(path);
            return File.Exists(path.ToNativePath());
        }
        
        /// <summary>
        /// Determines whether the file path has the specified extension.
        /// </summary>
        /// <param name="extension">The file extension, starting with '.'.</param>
        public bool HasExtension(string? extension) => 
            string.Equals(path.Extension, extension, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Opens the file for reading.
        /// </summary>
        /// <returns>A readable stream for the file represented by <paramref name="path"/>.</returns>
        /// <exception cref="InvalidOperationException">Thrown when <paramref name="path"/> does not represent a file.</exception>
        public FileStream OpenRead()
        {
            EnsureFilePath(path);
            return new(path: path.ToNativePath(), FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        /// <summary>
        /// Opens the file for writing and creates its parent directory when needed.
        /// </summary>
        /// <param name="mode">The file mode used to open or create the file.</param>
        /// <param name="access">The requested file access.</param>
        /// <param name="share">The requested file sharing behavior.</param>
        /// <returns>A writable stream for the file represented by <paramref name="path"/>.</returns>
        /// <exception cref="InvalidOperationException">Thrown when <paramref name="path"/> does not represent a file.</exception>
        public FileStream OpenWrite(FileMode mode = FileMode.Create, FileAccess access = FileAccess.Write, FileShare share = FileShare.None)
        {
            EnsureFilePath(path);

            var parentPath = path.Parent.ToNativePath();
            if (!string.IsNullOrWhiteSpace(parentPath))
                Directory.CreateDirectory(parentPath);

            return new(path: path.ToNativePath(), mode, access, share);
        }

        /// <summary>
        /// Ensures that the directory exists on disk.
        /// </summary>
        /// <returns>The created or existing <see cref="DirectoryInfo"/>.</returns>
        /// <exception cref="InvalidOperationException">Thrown when <paramref name="path"/> does not represent a directory.</exception>
        public DirectoryInfo EnsureDirectoryExists()
        {
            EnsureDirectoryPath(path);
            return Directory.CreateDirectory(path.ToNativePath());
        }

        /// <summary>
        /// Enumerates files beneath the directory.
        /// </summary>
        /// <param name="searchPattern">The search pattern used to match file names.</param>
        /// <param name="searchOption">Whether to search only the current directory or all descendants.</param>
        /// <returns>A sequence of file paths.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <paramref name="path"/> does not represent a directory.
        /// </exception>
        public IEnumerable<FilePath> EnumerateFiles(string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            EnsureDirectoryPath(path);
            return Directory.EnumerateFiles(path.ToNativePath(), searchPattern, searchOption)
                .Select(static path => FilePath.File(path, Path.DirectorySeparatorChar));
        }

        /// <summary>
        /// Enumerates child directories beneath the directory.
        /// </summary>
        /// <param name="searchPattern">The search pattern used to match directory names.</param>
        /// <param name="searchOption">Whether to search only the current directory or all descendants.</param>
        /// <returns>A sequence of directory paths.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <paramref name="path"/> does not represent a directory.
        /// </exception>
        public IEnumerable<FilePath> EnumerateDirectories(string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            EnsureDirectoryPath(path);
            return Directory.EnumerateDirectories(path.ToNativePath(), searchPattern, searchOption)
                .Select(static path => FilePath.Directory(path, Path.DirectorySeparatorChar));
        }
    }

    static void EnsureFilePath(FilePath path)
    {
        if (!path.IsFile)
            throw new InvalidOperationException($"Path '{path}' does not represent a file.");
    }

    static void EnsureDirectoryPath(FilePath path)
    {
        if (!path.IsDirectory)
            throw new InvalidOperationException($"Path '{path}' does not represent a directory.");
    }
}

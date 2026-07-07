using System.Text;

namespace Cohesive.Tests.Prelude;

public sealed class FilePathTests
{
    [Fact]
    public void FileAndDirectoryFactories_PreserveKindSegmentsAndRoot()
    {
        var relativeDirectory = FilePath.Directory("src/Cohesive");
        var absoluteFile = FilePath.File("/tmp/example.txt");

        Assert.True(relativeDirectory.IsRelative);
        Assert.True(relativeDirectory.IsDirectory);
        Assert.Equal(["src", "Cohesive"], relativeDirectory.Segments.ToArray());
        Assert.Equal("src/Cohesive", relativeDirectory.ToPath());

        Assert.True(absoluteFile.IsAbsolute);
        Assert.True(absoluteFile.IsFile);
        Assert.Equal("/", absoluteFile.Root);
        Assert.Equal("example.txt", absoluteFile.FileName);
        Assert.Equal("example", absoluteFile.FileNameWithoutExtension);
        Assert.Equal(".txt", absoluteFile.Extension);
        Assert.Equal(FilePath.Directory("/tmp"), absoluteFile.Parent);
    }

    [Fact]
    public void FromPath_InfersDirectoryForExtensionlessSegments_AndFileForExtensionedLeaf()
    {
        var directory = FilePath.FromPath("src/products");
        var file = FilePath.FromPath("src/products/README.md");

        Assert.True(directory.IsDirectory);
        Assert.True(file.IsFile);
    }

    [Fact]
    public void SlashOperator_CombinesRelativeAndAbsolutePaths()
    {
        var path = FilePath.Directory("/tmp")
                   / FilePath.Directory("datasets")
                   / FilePath.File("train.parquet");

        Assert.Equal("/tmp/datasets/train.parquet", path.ToPath());
        Assert.True(path.IsAbsolute);
        Assert.True(path.IsFile);
    }

    [Fact]
    public void CombineString_UsesDirectoryHeuristicForExtensionlessLeaf()
    {
        var path = FilePath.Directory("/tmp") / "nested" / "train.parquet";

        Assert.Equal("/tmp/nested/train.parquet", path.ToPath());
        Assert.True(path.IsFile);
        Assert.True((FilePath.Directory("/tmp") / "nested").IsDirectory);
    }

    [Fact]
    public void Combine_RejectsAbsoluteChildren_AndChildrenOnFiles()
    {
        Assert.Throws<ArgumentException>(() => FilePath.Directory("/tmp").Combine(FilePath.Directory("/etc")));
        Assert.Throws<InvalidOperationException>(() => FilePath.File("/tmp/input.txt").Combine(FilePath.Directory("child")));
    }

    [Fact]
    public void StartsWith_MatchesDirectoryPrefixes()
    {
        var file = FilePath.File("/tmp/datasets/train.parquet");

        Assert.True(file.StartsWith(FilePath.Directory("/tmp")));
        Assert.True(file.StartsWith(FilePath.Directory("/tmp/datasets")));
        Assert.True(file.StartsWith(FilePath.File("/tmp/datasets/train.parquet")));
        Assert.False(file.StartsWith(FilePath.Directory("/var")));
        Assert.False(file.StartsWith(FilePath.Directory("/tmp/datasets/train.parquet")));
    }

    [Fact]
    public void WindowsPaths_RoundTripAcrossSeparators()
    {
        var path = FilePath.File(@"C:\repo\data\train.parquet", Path.DirectorySeparatorChar);

        Assert.True(path.IsAbsolute);
        Assert.Equal("C:", path.Root);
        Assert.Equal("C:/repo/data/train.parquet", path.ToPath('/'));
    }

    [Fact]
    public void FileSystemExtensions_OpenFilesAndEnumerateDirectories()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cohesive-file-path-tests-{Guid.NewGuid():N}");

        try
        {
            var root = FilePath.Directory(tempRoot, Path.DirectorySeparatorChar);
            var nestedDirectory = root / FilePath.Directory("nested");
            var file = nestedDirectory / FilePath.File("example.txt");

            nestedDirectory.EnsureDirectoryExists();

            using (var stream = file.OpenWrite())
            using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write("hello");
            }

            using (var stream = file.OpenRead())
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false))
            {
                Assert.Equal("hello", reader.ReadToEnd());
            }

            var directories = root.EnumerateDirectories("*", SearchOption.AllDirectories).ToArray();
            var files = root.EnumerateFiles("*", SearchOption.AllDirectories).ToArray();

            Assert.Contains(nestedDirectory, directories);
            Assert.Contains(file, files);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}

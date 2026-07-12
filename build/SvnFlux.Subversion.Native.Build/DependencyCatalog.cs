namespace SvnFlux.Subversion.Native.Build;

internal static class DependencyCatalog {
    public const string SubversionVersion = "1.16.0-dev";
    public const string SubversionCommit = "6e6a9b0ddf0d745be7b56f6f1804fbc8216bd067";

    public static readonly SourceArchive Zlib = new("zlib-1.3.2", "https://github.com/madler/zlib/archive/refs/tags/v1.3.2.tar.gz", "zlib-1.3.2.tar.gz");
    public static readonly SourceArchive Sqlite = new("sqlite-amalgamation-3530300", "https://www.sqlite.org/2026/sqlite-amalgamation-3530300.zip", "sqlite-amalgamation-3530300.zip");
    public static readonly SourceArchive Apr = new("apr-1.7.6", "https://archive.apache.org/dist/apr/apr-1.7.6.tar.bz2", "apr-1.7.6.tar.bz2");
    public static readonly SourceArchive AprUtil = new("apr-util-1.6.3", "https://archive.apache.org/dist/apr/apr-util-1.6.3.tar.bz2", "apr-util-1.6.3.tar.bz2");
    public static readonly SourceArchive Serf = new("serf-1.3.10", "https://archive.apache.org/dist/serf/serf-1.3.10.tar.bz2", "serf-1.3.10.tar.bz2");
    public static readonly SourceArchive Expat = new("expat-2.8.2", "https://github.com/libexpat/libexpat/releases/download/R_2_8_2/expat-2.8.2.tar.gz", "expat-2.8.2.tar.gz");
    public static readonly SourceArchive Subversion = new($"subversion-{SubversionCommit}", $"https://github.com/apache/subversion/archive/{SubversionCommit}.tar.gz", $"subversion-{SubversionCommit}.tar.gz");

    public static SourceArchive[] All => [Zlib, Sqlite, Apr, AprUtil, Serf, Expat, Subversion];
}

internal sealed record SourceArchive(string DirectoryName, string Url, string FileName, bool ExtractIntoNamedDirectory = false);

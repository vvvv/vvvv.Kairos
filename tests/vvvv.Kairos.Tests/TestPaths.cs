namespace vvvv.Kairos.Tests;

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            // "version.txt" sits at the root of the repo, so if we find it, we're at the root
            if(File.Exists(Path.Combine(directory.FullName, "version.txt")))
                return directory.FullName;

            directory = directory.Parent;
        }
        
        throw new DirectoryNotFoundException($"Could locate version file above '{AppContext.BaseDirectory}'");
    }
}
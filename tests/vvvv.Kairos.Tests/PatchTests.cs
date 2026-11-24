using NuGet.Packaging.Core;
using NUnit.Framework;
using NUnit.Framework.Internal;
using VL.TestFramework;

namespace vvvv.Kairos.Tests;

public class PatchTests
{
    private TestEnvironment? _testEnvironment;
    
    private static readonly string VvvvDir = Environment.GetEnvironmentVariable("VVVV_DIR") ?? @"C:\Program Files\vvvv\vvvv_gamma_8.0-0369-g91b925904d-win-x64";
    
    private static IEnumerable<string> PackageDirectories() =>
        Directory.EnumerateDirectories(TestPaths.RepositoryRoot, "VL.*").Order();
    
    // Keep the setup non-async
    [OneTimeSetUp]
    public void Setup()
    {
        // The search path works like vvvv's --package-directories
        var searchPaths = new[]
        {
            TestPaths.RepositoryRoot
        };
        _testEnvironment = TestEnvironmentLoader.Load(Path.Combine(VvvvDir, "vvvv.exe"), searchPaths, preCompilePackages: true);
        PackageIdentity[] externalPackages = [
            new PackageIdentity("VL.ImGui.Skia", null), // Needed by help patches
            new PackageIdentity("VL.HDE", null), // Needed by extensions
        ];

        _testEnvironment.RestorePackagesAsync(externalPackages, includeFromSearchPaths: true).GetAwaiter().GetResult();
    }
    
    [OneTimeTearDown]
    public void TearDown()
    {
        _testEnvironment?.Dispose();
        _testEnvironment = null;
    }
    
    [Test]
    public async Task PackageDocumentsCompile()
    {
        await CompileAsync(PackageDirectories().SelectMany(package => Directory.EnumerateFiles(package, "*.vl")));
    }

    [Test]
    public async Task HelpPatchesCompile()
    {
        var allHelpPatches = PackageDirectories()
            .Select(package => Path.Combine(package, "help"))
            .Where(Directory.Exists)
            .SelectMany(helpDirectory => Directory.EnumerateFiles(helpDirectory, "*.vl", SearchOption.AllDirectories));

        await CompileAsync(allHelpPatches);
    }
    
    // Go through all provided documents
    private async Task CompileAsync(IEnumerable<string> documents)
    {
        var failures = new List<string>();
        var count = 0;

        foreach (var document in documents.Order())
        {
            count++;
            var name = Path.GetRelativePath(TestPaths.RepositoryRoot, document);
            try
            {
                // Keeps the failure of one document out of the report of the next ones
                using (new TestExecutionContext.IsolatedContext())
                    await _testEnvironment!.LoadAndTestAsync(document);

                await TestContext.Progress.WriteLineAsync($"OK      {name}");
            }
            catch (Exception e)
            {
                await TestContext.Progress.WriteLineAsync($"BROKEN  {name}");
                failures.Add($"{name}\n{e.Message.Trim()}");
            }
        }

        Assert.That(count, Is.GreaterThan(0), "No document found");
        if (failures.Count > 0)
            Assert.Fail($"{failures.Count} of {count} documents do not compile.\n\n{string.Join("\n\n", failures)}");
    }

}
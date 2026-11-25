using System;
using System.Linq;
using System.Xml.Linq;
using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Common.Tools.DotNet;
using static Fallout.Common.Tools.DotNet.DotNetTasks;
using static Fallout.Common.Tools.Git.GitTasks;
using System.IO;
using Fallout.Common.Tools.Git;
using System.Collections.Generic;
using Fallout.Common.Tools.NuGet;
using Serilog;
using Octokit;
using Fallout.Common.Tooling;

class Build : FalloutBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath VersionFile => RootDirectory / "version.txt";
    string Version => File.ReadAllText(VersionFile).Trim();

    [Parameter("Nuget feed URL")] readonly string NugetSource = "https://api.nuget.org/v3/index.json";

    [Parameter("NuGet API Key")] [Secret] private readonly string NuGetApiKey = string.Empty;

    [Parameter("GitHub token")] [Secret] private readonly string GitHubToken = string.Empty;

    [Parameter("Path to the vvvv installation used by the patch tests")] readonly string VvvvDir = string.Empty;
    
    // Package names
    private IReadOnlyList<string> Packages => new[]
    {
        "VL.AlchemX",
        "VL.Kairos",
        // "VL.Kairos.Fuse",
        "VL.Kairos.Runtime",
        // "VL.Kairos.Stride",
        "VL.LayerX",
        "VL.Touchy"
    };

    // AlchemX
    AbsolutePath AlchemXProject => RootDirectory / "VL.AlchemX" / "src" / "AlchemX" / "AlchemX.csproj";

    // Kairos.Runtime
    AbsolutePath KairosRuntimeProject => RootDirectory / "VL.Kairos.Runtime" / "src" / "VL.Kairos.Runtime.CSharp" /
                                         "VL.Kairos.Runtime.CSharp.csproj";

    // Touchy
    private AbsolutePath TouchyProject => RootDirectory / "VL.Touchy" / "src" / "VL.Touchy.csproj";

    // The package files of the current version, as Pack writes them
    IEnumerable<AbsolutePath> PackageFiles => Packages.Select(package => ArtifactsDirectory / $"{package}.{Version}.nupkg");

    AbsolutePath TestProject => RootDirectory / "tests" / "vvvv.Kairos.Tests" / "vvvv.Kairos.Tests.csproj";
    AbsolutePath TestResultsDirectory => ArtifactsDirectory / "test-results";
    AbsolutePath TestResultsFile => TestResultsDirectory / "results.trx";
    
    private Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            // Start with a fresh artifacts directory
            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            // Nope
        });

    // Build C# solutions
    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            // Build AlchemX
            DotNetBuild(s => s
                .SetProjectFile(AlchemXProject)
                .SetConfiguration("Release"));

            // Build Kairos.Runtime
            DotNetBuild(s => s
                .SetProjectFile(KairosRuntimeProject)
                .SetConfiguration("Release"));

            // Build Touchy
            DotNetBuild(s => s
                .SetProjectFile(TouchyProject)
                .SetConfiguration("Release"));
        });


    private Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            TestResultsDirectory.CreateOrCleanDirectory();
            try
            {
                DotNetTest(s => s
                    .SetProjectFile(TestProject)
                    .When(_ => !string.IsNullOrWhiteSpace(VvvvDir), s => s
                        .SetProcessEnvironmentVariable("VVVV_DIR", VvvvDir))
                    .SetVerbosity(DotNetVerbosity.normal)
                    .SetConfiguration("Release")
                    .SetResultsDirectory(TestResultsDirectory)
                    .AddLoggers($"trx;LogFileName={TestResultsFile.Name}"));
            }
            finally
            {
                // Tests run in their own process, so their logs never reach Fallout's summary.
                // Read the results back and log the failures from the build process instead.
                ReportTestFailures(TestResultsFile);
            }
        });


    static void ReportTestFailures(AbsolutePath trxFile)
    {
        if (!File.Exists(trxFile))
            return;

        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var failedResults = XDocument.Load(trxFile)
            .Descendants(ns + "UnitTestResult")
            .Where(result => (string?)result.Attribute("outcome") == "Failed");

        foreach (var result in failedResults)
        {
            var message = result.Descendants(ns + "Message").FirstOrDefault()?.Value.Trim();
            Log.Error("{Test} failed:\n{Message}", (string?)result.Attribute("testName"), message);
        }
    }
    
    // Create all packages with the same version
    private Target Pack => _ => _
        .DependsOn(Compile)
        .Requires(() => File.Exists(VersionFile))
        .Produces(ArtifactsDirectory / "*.nupkg")
        .Executes(() =>
        {
            Version.NotNullOrEmpty($"{VersionFile} empty or not found");

            foreach (var package in Packages)
            {
                Log.Information($"Attempting to pack {package}");
                try
                {
                    NuGetTasks.NuGetPack(p => p
                        .SetTargetPath(RootDirectory / package / $"deployment/{package}.nuspec")
                        .SetOutputDirectory(ArtifactsDirectory)
                        .SetVersion(Version));
                }
                catch (Exception e)
                {
                    Log.Error(e, $"Failed to pack {package}");
                    throw;
                }
            }
        });

    // Push every package of this version to the NuGet feed
    private Target PublishNuGet => _ => _
        .DependsOn(Pack)
        //.OnlyWhenStatic(() => IsServerBuild)
        .Requires(() => NuGetApiKey)
        .Executes(() =>
        {
            Log.Information("Publishing packages to {Source}", NugetSource);

            foreach (var package in PackageFiles)
            {
                try
                {
                    Assert.FileExists(package);

                    DotNetNuGetPush(p => p
                        .SetSource(NugetSource)
                        .SetApiKey(NuGetApiKey)
                        .SetTargetPath(package));
                }
                catch (Exception e)
                {
                    Log.Error(e, $"Failed to publish {package.Name}");
                    throw;
                }
            }
        });

    // Create a GitHub release for this version and attach its packages
    private Target PublishGitHub => _ => _
        .DependsOn(Pack)
        .After(PublishNuGet)
        .Requires(() => GitHubToken)
        .Executes(async () =>
        {
            var client = new GitHubClient(new ProductHeaderValue("kairos.fallout"))
            {
                Credentials = new Credentials(GitHubToken)
            };

            var release = await client.Repository.Release.Create("vvvv", "vvvv.Kairos", new NewRelease(Version)
            {
                Name = Version,
                Body = ""
            });

            foreach (var package in PackageFiles)
            {
                Assert.FileExists(package);
                Log.Information($"Uploading {package.Name}");

                using var stream = File.OpenRead(package);
                await client.Repository.Release.UploadAsset(release, new ReleaseAssetUpload
                {
                    FileName = package.Name,
                    RawData = stream,
                    ContentType = "application/octet-stream"
                });
            }
        });

    // Release to NuGet, then to GitHub
    private Target Publish => _ => _
        .DependsOn(PublishNuGet, PublishGitHub);
}
#:sdk Cake.Sdk

// Local build, run from the repository root: `dotnet build/cake.cs` (or `dotnet build/cake.cs -- --target=Test --configuration=Debug`).
// Script arguments go after `--`; otherwise `dotnet run` consumes options such as `--configuration` itself.

var target = Argument("target", "Test");
var configuration = Argument("configuration", "Release");

const string SOLUTION = "src/HomiliaMessenger.slnx";
const string TEST_PROJECT = "src/HomiliaMessenger.Tests/HomiliaMessenger.Tests.csproj";

Task("Restore")
    .Does(() => DotNetRestore(SOLUTION));

Task("Build")
    .IsDependentOn("Restore")
    .Does(() => DotNetBuild(SOLUTION, new DotNetBuildSettings
    {
        Configuration = configuration,
        NoRestore = true
    }));

Task("Test")
    .IsDependentOn("Build")
    .Does(() => DotNetTest(TEST_PROJECT, new DotNetTestSettings
    {
        Configuration = configuration,
        NoBuild = true
    }));

// Test results are configured to land in the test project's bin folder (see HomiliaMessenger.Tests.csproj), but tools that
// pass their own results directory can still leave empty TestResults folders behind; sweep those up.
Task("CleanTestResults")
    .Does(() => DeleteEmptyTestResultsDirectories(MakeAbsolute(Directory(".")).FullPath));

Teardown(_ => DeleteEmptyTestResultsDirectories(MakeAbsolute(Directory(".")).FullPath));

RunTarget(target);

void DeleteEmptyTestResultsDirectories(string root)
{
    foreach (string path in FindTestResultsDirectories(root).ToList())
    {
        if (TryDeleteEmptyTree(path))
        {
            Information("Deleted empty {0}", System.IO.Path.GetRelativePath(root, path));
        }
    }
}

// Walks the checkout without entering .git or any symbolic link/junction, so the sweep cannot leave the repository or loop.
IEnumerable<string> FindTestResultsDirectories(string directory)
{
    IEnumerable<DirectoryInfo> children;
    try
    {
        children = new DirectoryInfo(directory).EnumerateDirectories().ToList();
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        yield break;
    }

    foreach (DirectoryInfo child in children)
    {
        if (child.Name == ".git" || child.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            continue;
        }

        if (child.Name == "TestResults")
        {
            yield return child.FullName;
            continue;
        }

        foreach (string match in FindTestResultsDirectories(child.FullName))
        {
            yield return match;
        }
    }
}

// Deletes bottom-up with non-recursive deletes, which the OS refuses for any non-empty folder. A tool writing results
// concurrently therefore can never lose files: at worst its folder survives this sweep. Links are never entered or removed.
bool TryDeleteEmptyTree(string path)
{
    try
    {
        foreach (DirectoryInfo child in new DirectoryInfo(path).EnumerateDirectories().ToList())
        {
            if (!child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                TryDeleteEmptyTree(child.FullName);
            }
        }

        System.IO.Directory.Delete(path, recursive: false);
        return true;
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        return false;
    }
}

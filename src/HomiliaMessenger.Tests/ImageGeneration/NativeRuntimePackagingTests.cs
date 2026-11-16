using HomiliaMessenger.Build;
using HomiliaMessenger.Integrations.ImageGeneration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HomiliaMessenger.Tests.ImageGeneration;

/// <summary>
/// The stable-diffusion.cpp runtime the build includes: present, with its notices, and with its private ggml renamed so
/// it loads beside LLamaSharp's (see <c>build/native/StableDiffusionRuntime.targets</c>).
/// </summary>
[TestClass]
public sealed class NativeRuntimePackagingTests
{
    [TestMethod]
    public void Runtime_IsIncludedWithItsNotices()
    {
        string directory = RuntimeDirectory();

        foreach (string file in StableDiffusionNative.LibraryFiles())
        {
            Assert.IsTrue(File.Exists(Path.Combine(directory, file)), $"{file} is missing.");
        }

        Assert.IsTrue(File.Exists(Path.Combine(directory, "LICENSE.stable-diffusion.cpp.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "LICENSE.ggml.txt")));
        Assert.Contains("MIT License", File.ReadAllText(Path.Combine(directory, "LICENSE.stable-diffusion.cpp.txt")));
        Assert.IsNotEmpty(Directory.GetFiles(directory, OperatingSystem.IsWindows() ? "ggml-cpu-*.dll" : "libggml-cpu-*.so"));
    }

    [TestMethod]
    public void Runtime_NeverNamesTheGgmlConversationModelsUse()
    {
        string directory = RuntimeDirectory();
        string[] shared = OperatingSystem.IsWindows() ? ["ggml.dll", "ggml-base.dll"] : ["libggml.so.1", "libggml-base.so.1", "libggml.so", "libggml-base.so"];
        string[] libraries = Directory.GetFiles(directory, OperatingSystem.IsWindows() ? "*.dll" : "*.so*");

        foreach (string library in libraries)
        {
            IReadOnlyList<string> names = NativeLibraryRenamer.Names(File.ReadAllBytes(library));

            Assert.DoesNotContain(name => shared.Contains(name, StringComparer.OrdinalIgnoreCase), names, $"{Path.GetFileName(library)} still names the shared ggml.");
        }

        Assert.DoesNotContain(library => shared.Contains(Path.GetFileName(library), StringComparer.OrdinalIgnoreCase), libraries);

        IReadOnlyList<string> runtime = NativeLibraryRenamer.Names(File.ReadAllBytes(Path.Combine(directory, StableDiffusionNative.LibraryFiles()[^1])));
        Assert.Contains(StableDiffusionNative.LibraryFiles()[0], runtime);
        Assert.Contains(StableDiffusionNative.LibraryFiles()[1], runtime);
    }

    private static string RuntimeDirectory()
    {
        string? directory = StableDiffusionNative.RuntimeDirectory(AppContext.BaseDirectory);

        if (directory is null)
        {
            Assert.Inconclusive("Image generation is built for 64-bit Windows and Linux only.");
        }

        if (!Directory.Exists(directory))
        {
            Assert.Inconclusive("The image generation runtime was not included in this build, which happens when it could not be downloaded.");
        }

        return directory;
    }
}

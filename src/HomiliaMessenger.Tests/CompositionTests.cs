using HomiliaMessenger.Business.Modules.Credentials;
using HomiliaMessenger.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace HomiliaMessenger.Tests;

[TestClass]
public sealed class CompositionTests
{
    [TestMethod]
    public void Integrations_RegistersTheCredentialStoreForThisPlatform()
    {
        using ServiceProvider provider = new ServiceCollection().RegisterInternalIntegrationsServices(Path.GetTempPath()).BuildServiceProvider();

        ICredentialStore store = provider.GetRequiredService<ICredentialStore>();

        string expected = OperatingSystem.IsWindows() ? "WindowsCredentialStore"
            : OperatingSystem.IsLinux() ? "LinuxSecretServiceCredentialStore"
            : "UnsupportedCredentialStore";
        Assert.AreEqual(expected, store.GetType().Name);
    }
}

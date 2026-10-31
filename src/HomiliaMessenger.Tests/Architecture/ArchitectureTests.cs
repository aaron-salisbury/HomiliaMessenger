using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Reflection;

namespace HomiliaMessenger.Tests.Architecture;

[TestClass]
public sealed class ArchitectureTests
{
    private const string DATA = "HomiliaMessenger.Data";
    private const string INTEGRATIONS = "HomiliaMessenger.Integrations";
    private const string PRESENTATION = "HomiliaMessenger.Presentation.Desktop";

    [TestMethod]
    [DataRow(DATA)]
    [DataRow(INTEGRATIONS)]
    [DataRow(PRESENTATION)]
    public void Business_DoesNotReferenceOuterTiers(string assemblyName)
    {
        Assert.IsFalse(References(typeof(HomiliaMessenger.Business.DependencyInjection), assemblyName));
    }

    [TestMethod]
    public void Data_DoesNotReferenceIntegrations()
    {
        Assert.IsFalse(References(typeof(HomiliaMessenger.Data.DependencyInjection), INTEGRATIONS));
    }

    [TestMethod]
    public void Integrations_DoesNotReferenceData()
    {
        Assert.IsFalse(References(typeof(HomiliaMessenger.Integrations.DependencyInjection), DATA));
    }

    [TestMethod]
    [DataRow(DATA)]
    [DataRow(INTEGRATIONS)]
    public void DesktopPresentation_DoesNotReferenceImplementationTiers(string assemblyName)
    {
        Assert.IsFalse(References(typeof(HomiliaMessenger.Presentation.Desktop.App), assemblyName));
    }

    private static bool References(Type typeInAssembly, string assemblyName)
    {
        Assembly assembly = typeInAssembly.Assembly;

        return assembly.GetReferencedAssemblies().Any(reference => reference.Name == assemblyName);
    }
}

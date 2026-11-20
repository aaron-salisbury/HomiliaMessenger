using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Integrations.ImageGeneration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Text;

namespace HomiliaMessenger.Tests.ImageGeneration;

/// <summary>
/// Telling what a GGUF file holds from its header alone, rather than from its name.
/// </summary>
[TestClass]
public sealed class GgufImageInspectorTests
{
    [TestMethod]
    public void Inspect_ACompleteStableDiffusion1Checkpoint_IsSupported()
    {
        GgufInspection inspection = GgufImageInspector.Inspect(GgufTestFile.StableDiffusion1());

        Assert.AreEqual(ImageModelFamily.StableDiffusion1, inspection.Family);
        Assert.IsTrue(inspection.HasTextEncoder);
        Assert.IsTrue(inspection.HasVae);
        Assert.AreEqual("Q8_0", inspection.Quantization);
        Assert.IsNull(inspection.Problem);
        Assert.IsTrue(inspection.IsSupported);
    }

    [TestMethod]
    [DataRow(false, false, "text encoder or VAE")]
    [DataRow(false, true, "text encoder")]
    [DataRow(true, false, "no VAE")]
    public void Inspect_AStableDiffusion1FileMissingParts_IsRefusedWithWhatIsMissing(bool textEncoder, bool vae, string missing)
    {
        GgufInspection inspection = GgufImageInspector.Inspect(GgufTestFile.Write([], GgufTestFile.StableDiffusion(textEncoder: textEncoder, vae: vae)));

        Assert.AreEqual(ImageModelFamily.StableDiffusion1, inspection.Family);
        Assert.IsFalse(inspection.IsSupported);
        Assert.Contains(missing, inspection.Problem!);
    }

    [TestMethod]
    public void Inspect_OtherStableDiffusionFamilies_AreNamed_AndOnlyTheUnsupportedRefused()
    {
        GgufInspection sd2 = GgufImageInspector.Inspect(GgufTestFile.Write([], GgufTestFile.StableDiffusion(contextWidth: 1024)));
        GgufInspection sdxl = GgufImageInspector.Inspect(GgufTestFile.Write([], GgufTestFile.StableDiffusion(contextWidth: 2048, classEmbedding: true)));
        GgufInspection inpainting = GgufImageInspector.Inspect(GgufTestFile.Write([], GgufTestFile.StableDiffusion(inputChannels: 9)));

        Assert.AreEqual(ImageModelFamily.StableDiffusion2, sd2.Family);
        Assert.Contains("Stable Diffusion 2", sd2.Problem!);
        Assert.AreEqual(ImageModelFamily.StableDiffusionXL, sdxl.Family);
        Assert.IsNull(sdxl.Problem, "SDXL runs on a graphics card; the library refuses it for the processor.");
        Assert.AreEqual(ImageModelFamily.StableDiffusion1, inpainting.Family);
        Assert.Contains("inpainting", inpainting.Problem!);
    }

    [TestMethod]
    public void Inspect_ALanguageModel_IsNotAnImageModel()
    {
        GgufInspection inspection = GgufImageInspector.Inspect(GgufTestFile.LanguageModel());

        Assert.AreEqual(ImageModelFamily.LanguageModel, inspection.Family);
        Assert.AreEqual("llama", inspection.Architecture);
        Assert.Contains("Local Models", inspection.Problem!);
    }

    [TestMethod]
    public void Inspect_ALanguageModelWithoutArchitectureMetadata_IsRecognizedByItsTensors()
    {
        GgufInspection inspection = GgufImageInspector.Inspect(GgufTestFile.Write([], [("token_embd.weight", [512, 1000], GgufTestFile.Q8_0)]));

        Assert.AreEqual(ImageModelFamily.LanguageModel, inspection.Family);
        Assert.IsFalse(inspection.IsSupported);
    }

    [TestMethod]
    public void Inspect_AnotherImageArchitecture_IsRefused()
    {
        GgufInspection declared = GgufImageInspector.Inspect(GgufTestFile.Write([("general.architecture", "flux")], [("double_blocks.0.img_attn.qkv.weight", [3072, 9216], GgufTestFile.Q8_0)]));
        GgufInspection undeclared = GgufImageInspector.Inspect(GgufTestFile.Write([], [("model.diffusion_model.double_blocks.0.img_attn.qkv.weight", [3072, 9216], GgufTestFile.Q8_0)]));

        Assert.AreEqual(ImageModelFamily.OtherImageModel, declared.Family);
        Assert.AreEqual(ImageModelFamily.OtherImageModel, undeclared.Family);
        Assert.IsFalse(declared.IsSupported);
        Assert.IsFalse(undeclared.IsSupported);
    }

    [TestMethod]
    public void Inspect_AUNetOnlyConversionDeclaringSd1_IsIncomplete()
    {
        GgufInspection inspection = GgufImageInspector.Inspect(GgufTestFile.Write([("general.architecture", "sd1")],
            [("input_blocks.1.1.transformer_blocks.0.attn2.to_k.weight", [768, 320], GgufTestFile.Q8_0)]));

        Assert.AreEqual(ImageModelFamily.StableDiffusion1, inspection.Family);
        Assert.Contains("only part of the model", inspection.Problem!);
    }

    [TestMethod]
    public void Inspect_ACutOffHeader_AsksForMoreBytes()
    {
        byte[] header = GgufTestFile.StableDiffusion1();

        GgufInspection inspection = GgufImageInspector.Inspect(header.AsSpan(0, 80));

        Assert.IsGreaterThan(80L, inspection.NeedsBytes);
        Assert.IsFalse(inspection.IsSupported);
        Assert.IsTrue(GgufImageInspector.Inspect(header).IsSupported);
    }

    [TestMethod]
    public void Inspect_SomethingElse_IsUnrecognized()
    {
        GgufInspection safetensors = GgufImageInspector.Inspect(Encoding.UTF8.GetBytes("{\"__metadata__\":{}}........"));
        GgufInspection empty = GgufImageInspector.Inspect(GgufTestFile.Write([], []));

        Assert.AreEqual(ImageModelFamily.Unknown, safetensors.Family);
        Assert.Contains("not a GGUF file", safetensors.Problem!);
        Assert.AreEqual(ImageModelFamily.Unknown, empty.Family);
        Assert.IsFalse(empty.IsSupported);
    }
}

using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Integrations.ImageGeneration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.ImageGeneration;

/// <summary>
/// LoRAs: their base model read from their tensors and metadata, trigger words and strength from the file and the notes
/// beside it, and a folder of them whose settings are kept and applied only to models they fit.
/// </summary>
[TestClass]
public sealed class LoraTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Inspect_ReadsTheBaseFromTheTensors()
    {
        using TestFolder folder = new();

        LoraInspection sd1 = LoraInspector.Inspect(Write(folder, "sd1.safetensors", [("lora_unet_down_blocks_0_attentions_0_transformer_blocks_0_attn2_to_k.lora_down.weight", [8, 768])]));
        LoraInspection sd2 = LoraInspector.Inspect(Write(folder, "sd2.safetensors", [("lora_unet_down_blocks_0_attentions_0_transformer_blocks_0_attn2_to_k.lora_down.weight", [8, 1024])]));
        LoraInspection sdxl = LoraInspector.Inspect(Write(folder, "sdxl.safetensors", [("lora_unet_input_blocks_4_1_transformer_blocks_0_attn2_to_k.lora_down.weight", [16, 2048])]));
        LoraInspection sdxlTextOnly = LoraInspector.Inspect(Write(folder, "te.safetensors", [("lora_te1_text_model_encoder_layers_0_self_attn_k_proj.lora_down.weight", [4, 768]), ("lora_te2_text_model_encoder_layers_0_self_attn_k_proj.lora_down.weight", [4, 1280])]));
        LoraInspection diffusers = LoraInspector.Inspect(Write(folder, "diffusers.safetensors", [("unet.down_blocks.1.attentions.0.transformer_blocks.0.attn2.to_k.lora_A.weight", [4, 2048])]));
        LoraInspection klein = LoraInspector.Inspect(Write(folder, "klein.safetensors", [("diffusion_model.double_blocks.0.img_attn.qkv.lora_A.weight", [16, 3072]), ("diffusion_model.double_blocks.4.img_attn.qkv.lora_A.weight", [16, 3072]), ("diffusion_model.single_blocks.19.linear1.lora_A.weight", [16, 3072])]));
        LoraInspection partialFlux = LoraInspector.Inspect(Write(folder, "partial.safetensors", [("diffusion_model.double_blocks.0.img_attn.qkv.lora_A.weight", [16, 3072]), ("diffusion_model.single_blocks.3.linear1.lora_A.weight", [16, 3072])]));
        LoraInspection flux1 = LoraInspector.Inspect(Write(folder, "flux1.safetensors", [("lora_unet_double_blocks_18_img_attn_qkv.lora_down.weight", [16, 3072]), ("lora_unet_double_blocks_0_img_mod_lin.lora_down.weight", [16, 3072])]));
        LoraInspection unknown = LoraInspector.Inspect(Write(folder, "unknown.safetensors", [("something.weight", [4, 4])]));

        Assert.AreEqual(ImageModelFamily.StableDiffusion1, sd1.Base);
        Assert.AreEqual(ImageModelFamily.StableDiffusion2, sd2.Base);
        Assert.AreEqual(ImageModelFamily.StableDiffusionXL, sdxl.Base);
        Assert.AreEqual(ImageModelFamily.StableDiffusionXL, sdxlTextOnly.Base);
        Assert.AreEqual(ImageModelFamily.StableDiffusionXL, diffusers.Base);
        Assert.AreEqual(ImageModelFamily.Flux2Klein4B, klein.Base);
        Assert.AreEqual(ImageModelFamily.Unknown, partialFlux.Base, "A FLUX LoRA over a few early blocks, without metadata, could be FLUX.1.");
        Assert.AreEqual(ImageModelFamily.StableDiffusionXL, LoraInspector.Inspect(Write(folder, "sdxl-te1.safetensors", [("lora_te1_text_model_encoder_layers_0_self_attn_k_proj.lora_down.weight", [8, 768])])).Base,
            "An SDXL LoRA that trains only its first text encoder is still SDXL.");
        Assert.AreEqual(ImageModelFamily.StableDiffusion1, LoraInspector.Inspect(Write(folder, "sd1-te.safetensors", [("lora_te_text_model_encoder_layers_0_self_attn_k_proj.lora_down.weight", [8, 768])])).Base);
        Assert.AreEqual(ImageModelFamily.OtherImageModel, flux1.Base);
        Assert.AreEqual("FLUX.1", flux1.BaseDescription);
        Assert.AreEqual(ImageModelFamily.Unknown, unknown.Base);
    }

    [TestMethod]
    public void Inspect_MetadataTellsApartWhatTensorsCannot_AndNotesRetrainedSdxl()
    {
        using TestFolder folder = new();
        (string, long[])[] fluxBlocks = [("diffusion_model.double_blocks.0.img_attn.qkv.lora_A.weight", [16, 3072])];

        LoraInspection flux1 = LoraInspector.Inspect(Write(folder, "flux1.safetensors", fluxBlocks, new() { ["ss_base_model_version"] = "flux1" }));
        LoraInspection klein = LoraInspector.Inspect(Write(folder, "klein.safetensors", fluxBlocks, new() { ["modelspec.architecture"] = "flux-2-klein-4b/lora" }));
        LoraInspection pony = LoraInspector.Inspect(Write(folder, "pony.safetensors", [("lora_unet_input_blocks_4_1_transformer_blocks_0_attn2_to_k.lora_down.weight", [16, 2048])],
            new() { ["ss_sd_model_name"] = "ponyDiffusionV6XL_v6StartWithThisOne.safetensors" }));
        LoraInspection declaredOnly = LoraInspector.Inspect(Write(folder, "declared.safetensors", [("odd.weight", [4, 4])], new() { ["ss_base_model_version"] = "sdxl_base_v1-0" }));

        Assert.AreEqual(ImageModelFamily.OtherImageModel, flux1.Base);
        Assert.AreEqual(ImageModelFamily.Flux2Klein4B, klein.Base);
        Assert.AreEqual(ImageModelFamily.StableDiffusionXL, pony.Base);
        Assert.Contains("Pony", pony.Note!);
        Assert.AreEqual(ImageModelFamily.StableDiffusionXL, declaredOnly.Base);
    }

    [TestMethod]
    public void Inspect_FindsTriggerWords_FromTheMostTrustedSource()
    {
        using TestFolder folder = new();
        (string, long[])[] sdxl = [("lora_unet_input_blocks_4_1_transformer_blocks_0_attn2_to_k.lora_down.weight", [16, 2048])];
        string captions = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, int>>
        {
            ["10_ohwx woman"] = new() { ["ohwx woman"] = 20, ["solo"] = 20, ["red dress"] = 19, ["smile"] = 5 }
        });

        LoraInspection fromCaptions = LoraInspector.Inspect(Write(folder, "captions.safetensors", sdxl, new() { ["ss_tag_frequency"] = captions }));
        LoraInspection fromPhrase = LoraInspector.Inspect(Write(folder, "phrase.safetensors", sdxl, new() { ["ss_tag_frequency"] = captions, ["modelspec.trigger_phrase"] = "ohwx " }));

        string civitaiPath = Write(folder, "civitai.safetensors", sdxl, new() { ["modelspec.trigger_phrase"] = "ignored" });
        File.WriteAllText(Path.Combine(folder.Path, "civitai.civitai.info"), """{"baseModel":"SDXL 1.0","trainedWords":["mystyle"," glowing "]}""");
        LoraInspection fromCivitai = LoraInspector.Inspect(civitaiPath);

        string webUiPath = Write(folder, "webui.safetensors", sdxl);
        File.WriteAllText(Path.Combine(folder.Path, "webui.json"), """{"activation text":"zzz style,","preferred weight":0.6,"sd version":"SDXL"}""");
        LoraInspection fromWebUi = LoraInspector.Inspect(webUiPath);

        LoraInspection none = LoraInspector.Inspect(Write(folder, "none.safetensors", sdxl));

        Assert.AreEqual("ohwx woman, red dress", fromCaptions.TriggerWords);
        Assert.AreEqual("its training captions", fromCaptions.TriggerWordsSource);
        Assert.AreEqual("ohwx", fromPhrase.TriggerWords);
        Assert.AreEqual("mystyle, glowing", fromCivitai.TriggerWords);
        Assert.AreEqual("its Civitai info", fromCivitai.TriggerWordsSource);
        Assert.AreEqual("zzz style", fromWebUi.TriggerWords);
        Assert.AreEqual(0.6f, fromWebUi.Strength);
        Assert.IsNull(none.TriggerWords);
        Assert.IsNull(none.Strength);
        Assert.IsNull(LoraInspector.FromCaptions("""{"a":{"only once":1}}"""));

        // Plain-language captions count as one tag each; a repeated caption is no trigger word.
        Assert.AreEqual("glossy lips", LoraInspector.FromCaptions("""{"10_set":{"a woman with bold red lipstick smiling at the camera":12,"glossy lips":12}}"""));

        // Caption counts that are not numbers are passed over.
        Assert.AreEqual("ohwx", LoraInspector.FromCaptions("""{"set":{"trigger":"many","ohwx":5}}"""));

        // A weight that is no number is left out, rather than failing the inspection.
        string oddPath = Write(folder, "odd.safetensors", sdxl);
        File.WriteAllText(Path.Combine(folder.Path, "odd.json"), """{"activation text":"odd","preferred weight":null}""");
        LoraInspection odd = LoraInspector.Inspect(oddPath);
        Assert.AreEqual("odd", odd.TriggerWords);
        Assert.IsNull(odd.Strength);
    }

    [TestMethod]
    public void Inspect_ANonSafetensorsFile_SaysSo()
    {
        using TestFolder folder = new();
        string path = Path.Combine(folder.Path, "broken.safetensors");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("not a safetensors file at all"));

        LoraInspection inspection = LoraInspector.Inspect(path);

        Assert.AreEqual(ImageModelFamily.Unknown, inspection.Base);
        Assert.IsNotNull(inspection.Problem);

        // A header whose shapes are not numbers is read without failing.
        string odd = Path.Combine(folder.Path, "odd.safetensors");
        byte[] header = Encoding.UTF8.GetBytes("""{"x.lora_down.weight":{"dtype":"F16","shape":["a",null,true],"data_offsets":[0,0]}}""");
        File.WriteAllBytes(odd, [.. System.BitConverter.GetBytes((ulong)header.Length), .. header]);
        Assert.AreEqual(ImageModelFamily.Unknown, LoraInspector.Inspect(odd).Base);
    }

    [TestMethod]
    public async Task Library_ScansTheFolder_KeepsSettings_AndUsesOnlyLorasThatAreOnAndFit()
    {
        using TestFolder images = new();
        using TestFolder loras = new();
        Directory.CreateDirectory(Path.Combine(loras.Path, "styles"));
        Write(loras, "xl-style.safetensors", [("lora_unet_input_blocks_4_1_transformer_blocks_0_attn2_to_k.lora_down.weight", [16, 2048])], new() { ["modelspec.trigger_phrase"] = "xlstyle" });
        Write(loras, Path.Combine("styles", "sd1-style.safetensors"), [("lora_unet_down_blocks_0_attentions_0_transformer_blocks_0_attn2_to_k.lora_down.weight", [8, 768])]);
        File.WriteAllText(Path.Combine(loras.Path, "readme.txt"), "not a LoRA");
        ImageLoraLibrary library = new(images.Path, NullLogger<ImageLoraLibrary>.Instance);

        Assert.IsNotNull(library.SetFolder(Path.Combine(loras.Path, "missing")));
        Assert.IsNull(library.SetFolder(loras.Path));
        IReadOnlyList<ImageLora> found = await library.ScanAsync(TestContext.CancellationToken);

        Assert.AreSequenceEqual(new[] { "styles/sd1-style.safetensors", "xl-style.safetensors" }, found.Select(lora => lora.Name).ToArray());
        ImageLora xl = found.Single(lora => lora.Base == ImageModelFamily.StableDiffusionXL);
        Assert.IsFalse(xl.Settings.IsEnabled, "Off until turned on.");
        Assert.AreEqual("xlstyle", xl.Settings.TriggerWords);
        Assert.AreEqual(ImageLoraSettings.DEFAULT_STRENGTH, xl.Settings.Strength);
        Assert.IsTrue(xl.Fits(ImageModelFamily.StableDiffusionXL));
        Assert.IsFalse(xl.Fits(ImageModelFamily.StableDiffusion1));
        Assert.IsEmpty(library.InUse(ImageModelFamily.StableDiffusionXL, ImageModelPurpose.Portraits));

        Assert.IsNotNull(library.Save(xl.Name, xl.Settings with { Strength = 5 }));
        Assert.IsNull(library.Save(xl.Name, xl.Settings with { IsEnabled = true, Strength = 0.65f, Purposes = ImageModelPurpose.Portraits }));

        ImageLoraUse use = library.InUse(ImageModelFamily.StableDiffusionXL, ImageModelPurpose.Portraits).Single();
        Assert.AreEqual(Path.Combine(loras.Path, "xl-style.safetensors"), use.Path);
        Assert.AreEqual(0.65f, use.Strength);
        Assert.AreEqual("xlstyle", use.TriggerWords);
        Assert.IsEmpty(library.InUse(ImageModelFamily.StableDiffusionXL, ImageModelPurpose.Selfies));
        Assert.IsEmpty(library.InUse(ImageModelFamily.StableDiffusion1, ImageModelPurpose.Portraits));
        Assert.IsEmpty(library.InUse(ImageModelFamily.Flux2Klein4B, ImageModelPurpose.Portraits));

        // Kept across restarts.
        ImageLoraLibrary reopened = new(images.Path, NullLogger<ImageLoraLibrary>.Instance);
        Assert.AreEqual(loras.Path, reopened.Folder);
        Assert.AreEqual(0.65f, reopened.InUse(ImageModelFamily.StableDiffusionXL, ImageModelPurpose.Portraits).Single().Strength);

        // Choosing the same folder again keeps its settings.
        Assert.IsNull(reopened.SetFolder(loras.Path));
        Assert.HasCount(1, reopened.InUse(ImageModelFamily.StableDiffusionXL, ImageModelPurpose.Portraits));

        // A LoRA removed from the folder is no longer used.
        File.Delete(Path.Combine(loras.Path, "xl-style.safetensors"));
        Assert.IsEmpty(reopened.InUse(ImageModelFamily.StableDiffusionXL, ImageModelPurpose.Portraits));

        // Another folder's file of the same name is a different LoRA, off until turned on.
        using TestFolder other = new();
        Write(other, "xl-style.safetensors", [("lora_unet_input_blocks_4_1_transformer_blocks_0_attn2_to_k.lora_down.weight", [16, 2048])]);
        Assert.IsNull(reopened.SetFolder(other.Path));
        Assert.IsEmpty(reopened.InUse(ImageModelFamily.StableDiffusionXL, ImageModelPurpose.Portraits));
    }

    /// <summary>
    /// Writes a <c>.safetensors</c> file with the given tensors (shapes only; no weights) and metadata, returning its path.
    /// </summary>
    private static string Write(TestFolder folder, string name, IEnumerable<(string Name, long[] Shape)> tensors, Dictionary<string, string>? metadata = null)
    {
        Dictionary<string, object> header = [];

        if (metadata is not null)
        {
            header["__metadata__"] = metadata;
        }

        foreach ((string tensor, long[] shape) in tensors)
        {
            header[tensor] = new { dtype = "F16", shape, data_offsets = new[] { 0, 0 } };
        }

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(header);
        byte[] file = new byte[8 + json.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(file, (ulong)json.Length);
        json.CopyTo(file, 8);

        string path = Path.Combine(folder.Path, name);
        File.WriteAllBytes(path, file);
        return path;
    }
}

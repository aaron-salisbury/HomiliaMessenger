using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Credentials;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Business.Modules.Speech;
using HomiliaMessenger.Business.Modules.WebSearch;
using HomiliaMessenger.Integrations.Credentials;
using HomiliaMessenger.Integrations.Embeddings;
using HomiliaMessenger.Integrations.ImageGeneration;
using HomiliaMessenger.Integrations.LocalModels;
using HomiliaMessenger.Integrations.Speech;
using HomiliaMessenger.Integrations.WebSearch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace HomiliaMessenger.Integrations;

public static class DependencyInjection
{
    private const string MODELS_DIRECTORY_NAME = "Models";
    private const string EMBEDDINGS_DIRECTORY_NAME = "Embeddings";
    private const string IMAGES_DIRECTORY_NAME = "Images";
    private const string SPEECH_DIRECTORY_NAME = "Speech";
    private const string RUNTIMES_DIRECTORY_NAME = "runtimes";

    /// <summary>
    /// Registers internal integrations-tier services. Local agent models need <see cref="IHttpClientFactory"/> and
    /// logging, registered by the composition root.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="applicationDataDirectory">The directory under which downloaded local models are stored.</param>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    public static IServiceCollection RegisterInternalIntegrationsServices(this IServiceCollection services, string applicationDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);

        services.AddSingleton<ICredentialStore>(_ =>
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new WindowsCredentialStore();
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return new LinuxSecretServiceCredentialStore();
            }

            return new UnsupportedCredentialStore();
        });

        // Agent providers (and later speech providers) that satisfy Business capability contracts register here.
        string modelsDirectory = Path.Combine(applicationDataDirectory, MODELS_DIRECTORY_NAME);
        services.AddSingleton<OnnxModelHost>();
        services.AddSingleton<LlamaModelHost>();
        services.AddSingleton(provider => new LocalModelLibrary(
            modelsDirectory,
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<OnnxModelHost>(),
            provider.GetRequiredService<LlamaModelHost>(),
            provider.GetRequiredService<ICredentialStore>(),
            provider.GetRequiredService<ILogger<LocalModelLibrary>>()));
        services.AddSingleton<ILocalModelLibrary>(provider => provider.GetRequiredService<LocalModelLibrary>());
        services.AddSingleton<IConversationAgent, LocalConversationAgent>();

        // Embedding models are a separate category from conversational models, with their own folder and host.
        services.AddSingleton<LlamaEmbeddingHost>();
        services.AddSingleton(provider => new EmbeddingModelLibrary(
            Path.Combine(modelsDirectory, EMBEDDINGS_DIRECTORY_NAME),
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<LlamaEmbeddingHost>(),
            provider.GetRequiredService<ILogger<EmbeddingModelLibrary>>()));
        services.AddSingleton<IEmbeddingModelLibrary>(provider => provider.GetRequiredService<EmbeddingModelLibrary>());
        services.AddSingleton<ITextEmbedder>(provider => new LocalTextEmbedder(
            provider.GetRequiredService<EmbeddingModelLibrary>(),
            provider.GetRequiredService<ILogger<LocalTextEmbedder>>(),
            provider.GetRequiredService<LlamaEmbeddingHost>()));
        // Image models are a third category, with their own folder and runtime (stable-diffusion.cpp, built with the app).
        // Only one image is generated at a time, and conversation models are released (waiting for any reply in progress)
        // and kept from reloading until it is drawn, to make room for it.
        // The runtime is the one included with the app (CPU and Vulkan), or the CUDA build once downloaded in Settings.
        string imagesDirectory = Path.Combine(modelsDirectory, IMAGES_DIRECTORY_NAME);
        string cudaDirectory = Path.Combine(imagesDirectory, RUNTIMES_DIRECTORY_NAME, CudaRuntime.DIRECTORY_NAME);
        services.AddSingleton<ImageGenerationGate>();
        services.AddSingleton<IStableDiffusionBackend>(provider => new NativeStableDiffusionBackend(
            AppContext.BaseDirectory,
            provider.GetRequiredService<ILogger<NativeStableDiffusionBackend>>(),
            cudaDirectory));
        services.AddSingleton<IImageGpuRuntime>(provider => new CudaRuntime(
            Path.Combine(imagesDirectory, RUNTIMES_DIRECTORY_NAME),
            provider.GetRequiredService<IStableDiffusionBackend>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<ILogger<CudaRuntime>>()));

        // LoRAs stay in the folder the user keeps them in; only how each is used is saved beside the image models.
        services.AddSingleton(provider => new ImageLoraLibrary(imagesDirectory, provider.GetRequiredService<ILogger<ImageLoraLibrary>>()));
        services.AddSingleton<IImageLoraLibrary>(provider => provider.GetRequiredService<ImageLoraLibrary>());
        services.AddSingleton(provider => new ImageModelLibrary(
            imagesDirectory,
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<ICredentialStore>(),
            provider.GetRequiredService<ImageGenerationGate>(),
            provider.GetRequiredService<IStableDiffusionBackend>(),
            provider.GetRequiredService<ILogger<ImageModelLibrary>>()));
        services.AddSingleton<IImageModelLibrary>(provider => provider.GetRequiredService<ImageModelLibrary>());

        // One generator draws contact photos and selfies (with face matching), sharing the one-at-a-time gate.
        services.AddSingleton(provider =>
        {
            OnnxModelHost onnx = provider.GetRequiredService<OnnxModelHost>();
            LlamaModelHost llama = provider.GetRequiredService<LlamaModelHost>();

            return new StableDiffusionImageGenerator(
                provider.GetRequiredService<ImageModelLibrary>(),
                provider.GetRequiredService<IStableDiffusionBackend>(),
                provider.GetRequiredService<ImageGenerationGate>(),
                provider.GetRequiredService<ILogger<StableDiffusionImageGenerator>>(),
                draw => onnx.HoldUnloadedAsync(() => llama.HoldUnloadedAsync(draw)),
                provider.GetRequiredService<IImageLoraLibrary>());
        });
        services.AddSingleton<IImageGenerator>(provider => provider.GetRequiredService<StableDiffusionImageGenerator>());
        services.AddSingleton<ISelfieImageGenerator>(provider => provider.GetRequiredService<StableDiffusionImageGenerator>());

        services.AddSingleton<ITextToSpeech>(provider => new KokoroSpeech(
            Path.Combine(modelsDirectory, SPEECH_DIRECTORY_NAME, "Kokoro"),
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<ILogger<KokoroSpeech>>()));

        // Web search sends queries to Tavily only when the user has turned it on and saved a key.
        services.AddSingleton<IWebSearchProvider, TavilyWebSearchProvider>();

        return services;
    }
}

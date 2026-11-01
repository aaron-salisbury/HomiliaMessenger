using System.Collections.Generic;
using System.Linq;

namespace HomiliaMessenger.Integrations.LocalModels;

internal sealed record LocalModelFile(string Name, long Size, string Sha256);

/// <summary>
/// A model the application knows how to download and run with ONNX Runtime GenAI.
/// </summary>
/// <param name="ContextTokens">
/// How many tokens of prompt and reply to keep in the model's cache. The cache is allocated up front, so this bounds the
/// memory used beyond the model itself; longer chats are trimmed to fit.
/// </param>
/// <param name="BaseUrl">Where the files are downloaded from, pinned to a specific revision so the hashes stay valid.</param>
internal sealed record LocalModelDefinition(string Id, string DisplayName, string Description, string License, int ContextTokens, string BaseUrl, IReadOnlyList<LocalModelFile> Files)
{
    public long DownloadBytes => Files.Sum(file => file.Size);
}

/// <summary>
/// The models offered for download. Each uses the CPU build published by Microsoft, so no GPU is needed.
/// </summary>
internal static class LocalModelCatalog
{
    internal const string PHI_3_5_MINI = "phi-3.5-mini";
    internal const string PHI_4_MINI = "phi-4-mini";

    internal static IReadOnlyList<LocalModelDefinition> Models { get; } =
    [
        new(
            PHI_3_5_MINI,
            "Phi-3.5 mini",
            "Microsoft's 3.8B model, 4-bit for CPUs. The best fit for most laptops: about 4 GB of memory while chatting.",
            "MIT",
            // Its cache takes about 0.8 MB per token on a CPU.
            1536,
            "https://huggingface.co/microsoft/Phi-3.5-mini-instruct-onnx/resolve/7230dcd6c1dd28aab70f263ecc8734ec9d9bcb70/cpu_and_mobile/cpu-int4-awq-block-128-acc-level-4/",
            [
                new("config.json", 3451, "224de4f6a15b9d2a89695ec04b7f7ab2dd93a008a506979925e5a88cb5804974"),
                new("genai_config.json", 1580, "d1036a44e904c816c864931b961483eccf18e985dbd6797eecb33e01b626f580"),
                new("phi-3.5-mini-instruct-cpu-int4-awq-block-128-acc-level-4.onnx", 52176615, "c4f05e6ef52f2588df181e566afbf5e8eeba097fece2fc8246770473a10225fd"),
                new("phi-3.5-mini-instruct-cpu-int4-awq-block-128-acc-level-4.onnx.data", 2728144896, "3351fe9cc669eba43e07fb3cec436078629d5145531a28bc36fe6d5ad7683eb8"),
                new("special_tokens_map.json", 569, "474d699677e264edd3851559548cbbb61801ad8da6b249c81455007602313c34"),
                new("tokenizer.json", 1844436, "d0f067e1e15cd0a36ebef3668024882cb67a80b86fb4b7b4b128481f0d474db7"),
                new("tokenizer_config.json", 3364, "a12badd3fb56adb29bc7cfe03cf6fa7e3e19dd3d7602b85ce6ed63d424f5f222")
            ]),
        new(
            PHI_4_MINI,
            "Phi-4 mini",
            "Microsoft's newer 3.8B model, 4-bit for CPUs. A larger download and about 6 GB of memory while chatting, but it remembers more of a chat.",
            "MIT",
            // Its cache is about a third the size per token of Phi-3.5 mini's.
            4096,
            "https://huggingface.co/microsoft/Phi-4-mini-instruct-onnx/resolve/fc04c8f93df696602fd9f300a30d1bf2e3081347/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/",
            [
                new("added_tokens.json", 249, "d4f2aceb0f20b71dd1f4bcc7e052e4412946bf281840b8f83d39f259571af486"),
                new("config.json", 2504, "ac65d86061d3d0d704ee2511fd0eb8713ef19eb6eedba17c3080a4165d5b933b"),
                new("genai_config.json", 1520, "0fcfa1e663f2bc867f8dc62fae65dd0924f0a4d68b43d1234df742dd19171470"),
                new("merges.txt", 2418348, "856ce61180bb689282eed6b3a6838bb1f438399be23aefe9d20eb379791fb4ad"),
                new("model.onnx", 52118230, "701aa5d185b6a782bc27104a990dd5b634fa507840b7c42f7ee6f1fb812d0b83"),
                new("model.onnx.data", 4856573952, "cb0267fa60befa1a4ade8c98b6d32a3d67f51abbd307c7f793f132e8d9092131"),
                new("special_tokens_map.json", 587, "aff38493227d813e29fcf8406e8e90062f1f031aa47d589325e9c31d89ac7cc3"),
                new("tokenizer.json", 15524095, "382cc235b56c725945e149cc25f191da667c836655efd0857b004320e90e91ea"),
                new("tokenizer_config.json", 2960, "c565326a315fbe62cda093a59d298828c8f3f823122661325f41f3ba577a7dec"),
                new("vocab.json", 3910310, "6cb65a857824fa6615bb1782d95d882617a8bbce1da0317118586b36f39e98bd")
            ])
    ];
}

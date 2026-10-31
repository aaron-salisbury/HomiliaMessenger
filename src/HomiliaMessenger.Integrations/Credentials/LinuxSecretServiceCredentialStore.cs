using HomiliaMessenger.Business.Modules.Credentials;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.Credentials;

internal sealed class LinuxSecretServiceCredentialStore : ICredentialStore
{
    private const string ATTRIBUTE_NAME = "homiliamessenger-credential";
    private const string SECRET_TOOL = "secret-tool";

    public async Task DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunAsync(["clear", ATTRIBUTE_NAME, reference.Value], null, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Secret Service failed to delete the credential: {result.Error}");
        }
    }

    public async Task<CredentialMaterial?> GetAsync(CredentialReference reference, CancellationToken cancellationToken = default)
    {
        ProcessResult result = await RunAsync(["lookup", ATTRIBUTE_NAME, reference.Value], null, cancellationToken);

        if (result.ExitCode != 0)
        {
            // secret-tool exits nonzero silently when no item matches; any stderr output is a real failure
            // (locked keyring, unavailable D-Bus service) that must not be mistaken for a missing credential.
            if (string.IsNullOrWhiteSpace(result.Error))
            {
                return null;
            }

            throw new InvalidOperationException($"Secret Service failed to look up the credential: {result.Error}");
        }

        // secret-tool only appends a newline when stdout is a terminal; redirected output is the exact secret.
        return string.IsNullOrEmpty(result.Output) ? null : new CredentialMaterial(result.Output);
    }

    public async Task StoreAsync(CredentialReference reference, CredentialMaterial material, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);

        ProcessResult result = await RunAsync(
            ["store", $"--label=Homilia Messenger ({reference.Value})", ATTRIBUTE_NAME, reference.Value],
            material.Value,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Secret Service failed to store the credential: {result.Error}");
        }
    }

    private static async Task<ProcessResult> RunAsync(string[] arguments, string? standardInput, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = SECRET_TOOL,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start secret-tool.");

            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }

            // Drain both streams concurrently so a full stderr pipe cannot block the child while stdout is read.
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(output, error);
            await process.WaitForExitAsync(cancellationToken);

            return new ProcessResult(process.ExitCode, await output, (await error).Trim());
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException("Linux Secret Service support requires the libsecret secret-tool command.", exception);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}

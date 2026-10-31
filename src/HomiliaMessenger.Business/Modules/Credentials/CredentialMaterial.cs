using System;

namespace HomiliaMessenger.Business.Modules.Credentials;

public sealed record CredentialMaterial
{
    public CredentialMaterial(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }
}

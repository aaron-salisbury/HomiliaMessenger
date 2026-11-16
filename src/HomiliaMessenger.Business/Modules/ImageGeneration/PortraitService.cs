using FluentValidation;
using FluentValidation.Results;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Business.Modules.ImageGeneration;

/// <summary>
/// A description of the person a contact photo should show: their appearance, and optionally their clothing and setting.
/// </summary>
public sealed record PortraitRequest(string? Description)
{
    public const int MAX_DESCRIPTION_LENGTH = 400;
}

internal sealed class PortraitRequestValidator : AbstractValidator<PortraitRequest>
{
    public PortraitRequestValidator()
    {
        RuleFor(request => request.Description)
            .NotEmpty().WithMessage("Describe the person to generate a photo.")
            .MaximumLength(PortraitRequest.MAX_DESCRIPTION_LENGTH)
            .WithMessage($"Keep the description to {PortraitRequest.MAX_DESCRIPTION_LENGTH} characters or fewer.");
    }
}

/// <summary>
/// The outcome of generating a contact photo. When the description is invalid, <see cref="Validation"/> says why and
/// nothing was generated; otherwise <see cref="Result"/> holds the generator's outcome.
/// </summary>
/// <param name="Prompt">The prompt the description was composed into, when it was valid.</param>
public sealed record PortraitGeneration(ValidationResult Validation, ImageGenerationResult? Result, string? Prompt, long Seed)
{
    public bool IsSuccessful => Validation.IsValid && Result?.IsSuccessful == true;
}

/// <summary>
/// Generates contact photos from descriptions of people.
/// </summary>
public interface IPortraitService
{
    ImageGeneratorAvailability GetAvailability();

    /// <summary>
    /// Generates a new portrait of the person described, with a fresh seed each time, so generating again from the same
    /// description offers another likeness.
    /// </summary>
    Task<PortraitGeneration> GenerateAsync(PortraitRequest request, IProgress<ImageGenerationProgress>? progress = null, CancellationToken cancellationToken = default);
}

internal sealed class PortraitService : IPortraitService
{
    private readonly IImageGenerator _generator;
    private readonly IValidator<PortraitRequest> _validator;
    private readonly Func<long> _nextSeed;

    public PortraitService(IImageGenerator generator, IValidator<PortraitRequest> validator)
        : this(generator, validator, () => Random.Shared.NextInt64(0, int.MaxValue))
    {
    }

    internal PortraitService(IImageGenerator generator, IValidator<PortraitRequest> validator, Func<long> nextSeed)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _nextSeed = nextSeed ?? throw new ArgumentNullException(nameof(nextSeed));
    }

    public ImageGeneratorAvailability GetAvailability()
    {
        return _generator.GetAvailability();
    }

    public async Task<PortraitGeneration> GenerateAsync(PortraitRequest request, IProgress<ImageGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);

        if (!validation.IsValid)
        {
            return new PortraitGeneration(validation, null, null, 0);
        }

        string prompt = PortraitPromptComposer.Compose(request.Description!);
        long seed = _nextSeed();
        ImageGenerationResult result = await _generator.GenerateAsync(new ImageGenerationRequest(prompt, seed), progress, cancellationToken);

        return new PortraitGeneration(validation, result, prompt, seed);
    }
}

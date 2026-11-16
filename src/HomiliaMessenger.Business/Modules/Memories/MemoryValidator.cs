using FluentValidation;

namespace HomiliaMessenger.Business.Modules.Memories;

internal sealed class MemoryValidator : AbstractValidator<Memory>
{
    public MemoryValidator()
    {
        RuleFor(memory => memory.Text)
            .NotEmpty()
            .WithName("Memory")
            .WithMessage("A memory cannot be empty.")
            .MaximumLength(Memory.MAX_TEXT_LENGTH)
            .WithName("Memory");
    }
}

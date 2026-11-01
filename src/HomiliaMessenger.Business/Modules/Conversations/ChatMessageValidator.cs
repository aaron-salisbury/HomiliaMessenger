using FluentValidation;
using System;

namespace HomiliaMessenger.Business.Modules.Conversations;

internal sealed class ChatMessageValidator : AbstractValidator<ChatMessage>
{
    internal const int MAX_TEXT_LENGTH = 4000;

    public ChatMessageValidator()
    {
        RuleFor(message => message.Id.Value)
            .NotEqual(Guid.Empty)
            .WithMessage("A message ID is required.");

        RuleFor(message => message.ConversationId.Value)
            .NotEqual(Guid.Empty)
            .WithMessage("A conversation ID is required.");

        RuleFor(message => message.Text)
            .NotEmpty()
            .WithName("Message")
            .WithMessage("A message cannot be empty.")
            .MaximumLength(MAX_TEXT_LENGTH)
            .WithName("Message");
    }
}

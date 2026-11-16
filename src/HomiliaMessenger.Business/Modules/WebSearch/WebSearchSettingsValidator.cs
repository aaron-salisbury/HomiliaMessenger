using FluentValidation;

namespace HomiliaMessenger.Business.Modules.WebSearch;

internal sealed class WebSearchSettingsValidator : AbstractValidator<WebSearchSettings>
{
    internal const int MAX_MONTHLY_CREDIT_BUDGET = 1_000_000;

    public WebSearchSettingsValidator()
    {
        RuleFor(settings => settings.MonthlyCreditBudget)
            .InclusiveBetween(0, MAX_MONTHLY_CREDIT_BUDGET)
            .WithName("Monthly credit budget");

        // Every month has days 1 to 28, so the period always starts on the same day.
        RuleFor(settings => settings.CreditResetDay)
            .InclusiveBetween(1, 28)
            .WithName("Credit reset day");
    }
}

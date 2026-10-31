using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Contacts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

namespace HomiliaMessenger.Tests.Contacts;

[TestClass]
public sealed class ContactValidatorTests
{
    [TestMethod]
    public void Birthday_IsRequired()
    {
        ValidationResult result = Validate(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), birthday: null);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("A birthday is required.", result.Errors.Single().ErrorMessage);
    }

    [TestMethod]
    [DataRow(2008, 10, 31, true, DisplayName = "18th birthday today")]
    [DataRow(2008, 11, 1, false, DisplayName = "18th birthday tomorrow")]
    [DataRow(1876, 11, 1, true, DisplayName = "149, turning 150 tomorrow")]
    [DataRow(1876, 10, 31, true, DisplayName = "150 today")]
    [DataRow(1875, 10, 31, false, DisplayName = "151 today")]
    public void Birthday_MustMakeTheContactAnAdultAndNoOlderThan150(int year, int month, int day, bool expectedValid)
    {
        ValidationResult result = Validate(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero), new DateOnly(year, month, day));

        Assert.AreEqual(expectedValid, result.IsValid);
    }

    [TestMethod]
    public void Birthday_AgeIsJudgedOnTheUtcDateRatherThanLocalTime()
    {
        // 9 PM on October 30 in US Central (UTC-5) is already October 31 in UTC.
        DateTimeOffset centralEvening = new(2026, 10, 30, 21, 0, 0, TimeSpan.FromHours(-5));

        ValidationResult turnsEighteenOnUtcDate = Validate(centralEvening, new DateOnly(2008, 10, 31));
        ValidationResult turnsEighteenTheDayAfter = Validate(centralEvening, new DateOnly(2008, 11, 1));

        Assert.IsTrue(turnsEighteenOnUtcDate.IsValid);
        Assert.IsFalse(turnsEighteenTheDayAfter.IsValid);
        Assert.AreEqual("Contacts must be at least 18 years old.", turnsEighteenTheDayAfter.Errors.Single().ErrorMessage);
    }

    [TestMethod]
    public void Birthday_LeapDayBirthdayBecomesAnAdultOnFebruary28()
    {
        ValidationResult dayBefore = Validate(new DateTimeOffset(2026, 2, 27, 12, 0, 0, TimeSpan.Zero), new DateOnly(2008, 2, 29));
        ValidationResult february28 = Validate(new DateTimeOffset(2026, 2, 28, 12, 0, 0, TimeSpan.Zero), new DateOnly(2008, 2, 29));

        Assert.IsFalse(dayBefore.IsValid);
        Assert.IsTrue(february28.IsValid);
    }

    private static ValidationResult Validate(DateTimeOffset now, DateOnly? birthday)
    {
        ContactValidator validator = new(new FixedTimeProvider(now));

        return validator.Validate(new Contact { Id = ContactId.New(), GivenName = "Walt", Birthday = birthday });
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now.ToUniversalTime();
        }
    }
}

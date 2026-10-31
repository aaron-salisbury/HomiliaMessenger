using HomiliaMessenger.Business.Modules.Contacts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace HomiliaMessenger.Tests.Contacts;

[TestClass]
public sealed class ContactBirthdayTests
{
    [TestMethod]
    [DataRow(2026, 3, 13, 77)]
    [DataRow(2026, 3, 14, 78)]
    [DataRow(2026, 10, 31, 78)]
    public void AgeOn_CountsWholeYearsUpToTheBirthday(int year, int month, int day, int expected)
    {
        Assert.AreEqual(expected, ContactBirthday.AgeOn(new DateOnly(1948, 3, 14), new DateOnly(year, month, day)));
    }

    [TestMethod]
    public void AgeOn_CountsLeapDayBirthdaysOnFebruary28InCommonYears()
    {
        DateOnly leapDay = new(2000, 2, 29);

        Assert.AreEqual(24, ContactBirthday.AgeOn(leapDay, new DateOnly(2025, 2, 27)));
        Assert.AreEqual(25, ContactBirthday.AgeOn(leapDay, new DateOnly(2025, 2, 28)));
    }

    [TestMethod]
    public void TryParse_ReadsIsoDatesAndTimestampsWithoutShiftingTheDate()
    {
        Assert.IsTrue(ContactBirthday.TryParse("1948-03-14", out DateOnly date));
        Assert.AreEqual(new DateOnly(1948, 3, 14), date);
        Assert.IsTrue(ContactBirthday.TryParse("2000-01-01T23:59:59-06:00", out DateOnly timestamp));
        Assert.AreEqual(new DateOnly(2000, 1, 1), timestamp);
        Assert.IsFalse(ContactBirthday.TryParse("--0314", out _));
        Assert.AreEqual("1948-03-14", ContactBirthday.Format(new DateOnly(1948, 3, 14)));
    }
}

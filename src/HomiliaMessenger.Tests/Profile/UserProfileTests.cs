using FluentValidation.Results;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Profile;
using HomiliaMessenger.Presentation.Desktop.Models;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.Profile;

[TestClass]
public sealed class UserProfileTests
{
    private static readonly byte[] PNG = [137, 80, 78, 71, 13, 10, 26, 10];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task GetAsync_IsEmptyUntilTheProfileIsSaved()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        IUserProfileService service = database.Services.GetRequiredService<IUserProfileService>();

        UserProfile profile = await service.GetAsync(TestContext.CancellationToken);

        Assert.AreEqual(new UserProfile(), profile);
    }

    [TestMethod]
    public async Task SaveAsync_PersistsEveryFieldAndReplacesThePreviousProfile()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        IUserProfileService service = database.Services.GetRequiredService<IUserProfileService>();
        UserProfile aaron = new() { Name = "Aaron", Birthday = new DateOnly(1990, 5, 1), Gender = Gender.Male, About = "Nurse in Denver.", Photo = PNG };

        await service.SaveAsync(new UserProfile { Name = "Someone else" }, TestContext.CancellationToken);
        ValidationResult result = await service.SaveAsync(aaron, TestContext.CancellationToken);
        UserProfile stored = await service.GetAsync(TestContext.CancellationToken);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(aaron with { Photo = null }, stored with { Photo = null });
        Assert.AreEqual(Convert.ToHexString(PNG), Convert.ToHexString(stored.Photo!));
    }

    [TestMethod]
    public async Task SaveAsync_RejectsInvalidProfilesWithoutSavingThem()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        IUserProfileService service = database.Services.GetRequiredService<IUserProfileService>();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        UserProfile[] invalid =
        [
            new() { Name = new string('a', 101) },
            new() { About = new string('a', UserProfile.MAX_ABOUT_LENGTH + 1) },
            new() { Birthday = today.AddDays(1) },
            new() { Birthday = today.AddYears(-151) },
            new() { Gender = (Gender)7 },
            new() { Photo = new byte[(8 * 1024 * 1024) + 1] }
        ];

        foreach (UserProfile profile in invalid)
        {
            ValidationResult result = await service.SaveAsync(profile, TestContext.CancellationToken);
            Assert.IsFalse(result.IsValid);
        }

        Assert.AreEqual(new UserProfile(), await service.GetAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ProfileViewModel_LoadsEditsAndSavesTheProfile()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        IUserProfileService service = database.Services.GetRequiredService<IUserProfileService>();
        await service.SaveAsync(new UserProfile { Name = "Aaron", Gender = Gender.Male }, TestContext.CancellationToken);
        ProfileViewModel profile = new(service);

        await profile.InitializeAsync();
        bool dirtyAfterLoading = profile.IsDirty;
        bool canSaveUnchanged = profile.SaveCommand.CanExecute(null);
        profile.About = "  Nurse in Denver.  ";
        profile.Birthday = new DateOnly(1990, 5, 1);
        await profile.SetPhotoAsync(new MemoryStream(PNG));
        await profile.SaveCommand.ExecuteAsync(null);
        UserProfile stored = await service.GetAsync(TestContext.CancellationToken);

        Assert.IsFalse(dirtyAfterLoading);
        Assert.IsFalse(canSaveUnchanged);
        Assert.AreEqual("Aaron", profile.DisplayName);
        Assert.StartsWith("Age ", profile.Summary);
        Assert.EndsWith(" · Male", profile.Summary);
        Assert.IsTrue(profile.HasPhoto);
        Assert.IsFalse(profile.IsDirty);
        Assert.AreEqual("Nurse in Denver.", stored.About);
        Assert.AreEqual(new DateOnly(1990, 5, 1), stored.Birthday);
        Assert.IsNotNull(stored.Photo);
    }

    [TestMethod]
    public async Task ProfileViewModel_RejectsAPhotoThatIsNotAJpegOrPng()
    {
        using TestDatabase database = await TestDatabase.CreateAsync(TestContext);
        ProfileViewModel profile = new(database.Services.GetRequiredService<IUserProfileService>());

        await profile.InitializeAsync();
        await profile.SetPhotoAsync(new MemoryStream([1, 2, 3, 4]));
        string photoStatus = profile.Status;
        profile.Gender = GenderOption.For(Gender.Female);
        profile.ClearBirthdayCommand.Execute(null);

        Assert.IsFalse(profile.HasPhoto);
        Assert.Contains("JPEG or PNG", photoStatus);
        Assert.AreEqual(string.Empty, profile.Status);
        Assert.AreEqual(Gender.Female, profile.ToProfile().Gender);
        Assert.IsNull(profile.ToProfile().Birthday);
    }
}

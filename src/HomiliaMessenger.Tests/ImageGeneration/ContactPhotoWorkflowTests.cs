using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Presentation.Desktop.Models;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Tests.ImageGeneration;

/// <summary>
/// Generating a contact's photo: the portrait service, the photo dialog, and the contact editor around it.
/// </summary>
[TestClass]
public sealed class ContactPhotoWorkflowTests
{
    private const string DESCRIPTION = "An older man with a long white beard and glasses, wearing a plaid shirt in his workshop.";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Service_ComposesThePromptAndUsesAFreshSeedEachTime()
    {
        FakeImageGenerator generator = new();
        long seed = 100;
        PortraitService service = new(generator, new PortraitRequestValidator(), () => seed++);

        PortraitGeneration first = await service.GenerateAsync(new PortraitRequest(DESCRIPTION), cancellationToken: TestContext.CancellationToken);
        PortraitGeneration second = await service.GenerateAsync(new PortraitRequest(DESCRIPTION), cancellationToken: TestContext.CancellationToken);

        Assert.IsTrue(first.IsSuccessful);
        Assert.AreEqual(PortraitPromptComposer.Compose(DESCRIPTION), generator.Requests[0].Prompt);
        Assert.AreEqual(generator.Requests[0].Prompt, generator.Requests[1].Prompt);
        Assert.AreEqual(100, first.Seed);
        Assert.AreEqual(101, second.Seed);
        Assert.AreNotEqual(generator.Requests[0].Seed, generator.Requests[1].Seed);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    public async Task Service_RefusesAnEmptyDescriptionWithoutGenerating(string? description)
    {
        FakeImageGenerator generator = new();
        PortraitService service = new(generator, new PortraitRequestValidator());

        PortraitGeneration generation = await service.GenerateAsync(new PortraitRequest(description), cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(generation.Validation.IsValid);
        Assert.IsNull(generation.Result);
        Assert.IsEmpty(generator.Requests);
    }

    [TestMethod]
    public async Task Service_RefusesAnOverlongDescription()
    {
        PortraitService service = new(new FakeImageGenerator(), new PortraitRequestValidator());

        PortraitGeneration generation = await service.GenerateAsync(new PortraitRequest(new string('a', PortraitRequest.MAX_DESCRIPTION_LENGTH + 1)), cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(generation.Validation.IsValid);
    }

    [TestMethod]
    public void Dialog_WithoutAModel_PointsToSettingsInsteadOfDownloading()
    {
        FakeImageGenerator generator = new() { Availability = new(false, null, true, "Download an image model in Settings to generate photos.") };
        using GeneratePhotoViewModel dialog = new(new PortraitService(generator, new PortraitRequestValidator()), DESCRIPTION);
        bool closed = false;
        dialog.CloseRequested += (_, _) => closed = true;

        bool canGenerate = dialog.GenerateCommand.CanExecute(null);
        dialog.OpenSettingsCommand.Execute(null);

        Assert.IsTrue(dialog.NeedsModel);
        Assert.IsFalse(canGenerate);
        Assert.Contains("Settings", dialog.Status);
        Assert.IsTrue(dialog.WantsSettings);
        Assert.IsTrue(closed);
        Assert.IsNull(dialog.ChosenPhoto);
        Assert.IsEmpty(generator.Requests);
    }

    [TestMethod]
    public void Dialog_WithoutTheRuntime_SaysSoAndDoesNotOfferSettings()
    {
        FakeImageGenerator generator = new() { Availability = new(false, null, false, "This build does not include image generation.") };
        using GeneratePhotoViewModel dialog = new(new PortraitService(generator, new PortraitRequestValidator()), DESCRIPTION);

        Assert.IsTrue(dialog.IsUnsupported);
        Assert.IsFalse(dialog.NeedsModel);
        Assert.IsFalse(dialog.GenerateCommand.CanExecute(null));
        Assert.AreEqual("This build does not include image generation.", dialog.Status);
    }

    [TestMethod]
    public async Task Dialog_GeneratesAgainFromTheSameDescriptionAndUsesOnlyTheChosenPhoto()
    {
        FakeImageGenerator generator = new();
        long seed = 1;
        using GeneratePhotoViewModel dialog = new(new PortraitService(generator, new PortraitRequestValidator(), () => seed++));
        dialog.Description = DESCRIPTION;

        bool canUseBefore = dialog.UsePhotoCommand.CanExecute(null);
        await dialog.GenerateCommand.ExecuteAsync(null);
        await dialog.GenerateCommand.ExecuteAsync(null);
        byte[]? beforeChoosing = dialog.ChosenPhoto;
        dialog.UsePhotoCommand.Execute(null);

        Assert.IsFalse(canUseBefore);
        Assert.HasCount(2, generator.Requests);
        Assert.AreEqual(generator.Requests[0].Prompt, generator.Requests[1].Prompt);
        Assert.AreNotEqual(generator.Requests[0].Seed, generator.Requests[1].Seed);
        Assert.AreEqual(DESCRIPTION, dialog.Description);
        Assert.IsNull(beforeChoosing);
        Assert.AreSequenceEqual(FakeImageGenerator.TinyPng(2), dialog.ChosenPhoto);
    }

    [TestMethod]
    public async Task Dialog_StoppedOrFailed_ChoosesNoPhoto()
    {
        FakeImageGenerator generator = new()
        {
            Respond = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { }, TaskScheduler.Default);
                return ImageGenerationResult.Canceled;
            }
        };
        using GeneratePhotoViewModel dialog = new(new PortraitService(generator, new PortraitRequestValidator()), DESCRIPTION);

        Task generating = dialog.GenerateCommand.ExecuteAsync(null);
        bool stoppable = dialog.StopCommand.CanExecute(null);
        dialog.StopCommand.Execute(null);
        await generating;
        string stopped = dialog.Status;
        generator.Respond = (_, _) => Task.FromResult(ImageGenerationResult.Failed("There was not enough memory to generate the photo."));
        await dialog.GenerateCommand.ExecuteAsync(null);

        Assert.IsTrue(stoppable);
        Assert.Contains("Stopped", stopped);
        Assert.Contains("not enough memory", dialog.Status);
        Assert.IsFalse(dialog.IsGenerating);
        Assert.IsFalse(dialog.UsePhotoCommand.CanExecute(null));
        Assert.IsNull(dialog.ChosenPhoto);
    }

    [TestMethod]
    public void Editor_WithoutImageGeneration_OffersOnlyFiles()
    {
        using ContactDetailViewModel detail = new();

        Assert.IsFalse(detail.CanGeneratePhoto);
        Assert.IsNull(detail.CreatePhotoGenerator());
    }

    [TestMethod]
    public async Task Editor_UsesTheChosenPhotoThroughTheUsualPhotoPath()
    {
        byte[] existing = FakeImageGenerator.TinyPng(200);
        using ContactDetailViewModel detail = Editor(existing, out FakeImageGenerator _);
        List<string> messages = [];
        detail.MessageRaised += (_, message) => messages.Add(message);

        using GeneratePhotoViewModel dialog = detail.CreatePhotoGenerator()!;
        dialog.Description = DESCRIPTION;
        await dialog.GenerateCommand.ExecuteAsync(null);
        dialog.UsePhotoCommand.Execute(null);
        detail.AcceptGeneratedPhoto(dialog);
        Contact saved = detail.ToContact();

        Assert.IsTrue(detail.IsDirty);
        Assert.AreSequenceEqual(dialog.ChosenPhoto, saved.Photo);
        Assert.AreNotSequenceEqual(existing, saved.Photo);
        Assert.Contains("Save the contact", messages.Single());
    }

    [TestMethod]
    public async Task Editor_KeepsItsPhotoWhenTheDialogIsClosedWithoutChoosing_EvenAfterGenerating()
    {
        byte[] existing = FakeImageGenerator.TinyPng(200);
        using ContactDetailViewModel detail = Editor(existing, out FakeImageGenerator generator);

        using GeneratePhotoViewModel dialog = detail.CreatePhotoGenerator()!;
        dialog.Description = DESCRIPTION;
        await dialog.GenerateCommand.ExecuteAsync(null);
        await dialog.GenerateCommand.ExecuteAsync(null);
        detail.AcceptGeneratedPhoto(dialog);

        Assert.HasCount(2, generator.Requests);
        Assert.IsFalse(detail.IsDirty);
        Assert.AreSequenceEqual(existing, detail.ToContact().Photo);
    }

    [TestMethod]
    public void Editor_RemembersTheDescriptionForTheNextDialog_AndForwardsAskingForSettings()
    {
        using ContactDetailViewModel detail = Editor(null, out _);
        bool settingsRequested = false;
        detail.SettingsRequested += (_, _) => settingsRequested = true;

        using (GeneratePhotoViewModel first = detail.CreatePhotoGenerator()!)
        {
            first.Description = DESCRIPTION;
            first.OpenSettingsCommand.Execute(null);
            detail.AcceptGeneratedPhoto(first);
        }

        using GeneratePhotoViewModel second = detail.CreatePhotoGenerator()!;

        Assert.IsTrue(settingsRequested);
        Assert.AreEqual(DESCRIPTION, second.Description);
        Assert.IsFalse(detail.IsDirty);
    }

    [TestMethod]
    public async Task Editor_StillAcceptsAnUploadedPhoto()
    {
        using ContactDetailViewModel detail = Editor(null, out _);
        byte[] uploaded = FakeImageGenerator.TinyPng(50);

        await detail.SetPhotoAsync(new MemoryStream(uploaded));

        Assert.IsTrue(detail.IsDirty);
        Assert.AreSequenceEqual(uploaded, detail.ToContact().Photo);
    }

    [TestMethod]
    public async Task SettingsItem_AsksToAcceptTheModelLicenseBeforeDownloading()
    {
        RecordingImageLibrary library = new();
        List<string> asked = [];
        bool accept = false;
        ImageModelItem item = new(library.List()[0], library, (title, message) =>
        {
            asked.Add(message);
            return Task.FromResult(accept);
        }, () => { });

        await item.DownloadCommand.ExecuteAsync(null);
        int downloadsAfterDeclining = library.Downloads;
        accept = true;
        await item.DownloadCommand.ExecuteAsync(null);

        Assert.AreEqual(0, downloadsAfterDeclining);
        Assert.AreEqual(1, library.Downloads);
        Assert.Contains("CreativeML Open RAIL-M", asked[0]);
        Assert.Contains("Attachment A", asked[0]);
        Assert.Contains("DreamShaper 8 LCM", asked[0]);
    }

    private static ContactDetailViewModel Editor(byte[]? photo, out FakeImageGenerator generator)
    {
        generator = new FakeImageGenerator();
        long seed = 1;
        Contact contact = new() { Id = ContactId.New(), GivenName = "Walt", Birthday = new DateOnly(1948, 3, 14), Photo = photo };

        return new ContactDetailViewModel(contact, portraits: new PortraitService(generator, new PortraitRequestValidator(), () => seed++));
    }

    private sealed class RecordingImageLibrary : IImageModelLibrary
    {
        public int Downloads { get; private set; }

        public IReadOnlyList<ImageModel> List(ImageModelPurpose purpose = ImageModelPurpose.Portraits)
        {
            return [new ImageModel("dreamshaper-8-lcm-q8_0", "DreamShaper 8 LCM", "Portraits.", "CreativeML Open RAIL-M", "https://example.test/license", 1_802_220_721, Downloads > 0, Downloads > 0, false, true,
                new ImageGenerationSettings(512, 512, "lcm", null, 4, 1f), ImageModelPurpose.Portraits | ImageModelPurpose.Selfies)];
        }

        public FaceMatching GetFaceMatching()
        {
            return new FaceMatching("IP-Adapter Plus-Face", "Apache 2.0", null, 2_626_556_736, 774_322_936, false);
        }

        public Task<Business.Modules.Agents.LocalModelDownloadResult> DownloadAsync(string modelId, ImageModelPurpose purpose = ImageModelPurpose.Portraits, IProgress<Business.Modules.Agents.LocalModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Downloads++;
            return Task.FromResult(Business.Modules.Agents.LocalModelDownloadResult.Success);
        }

        public Task<bool> DeleteAsync(string modelId, ImageModelPurpose purpose = ImageModelPurpose.Portraits, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public bool Select(string modelId, ImageModelPurpose purpose = ImageModelPurpose.Portraits)
        {
            return true;
        }

        public Task<ImageModelLookup> LookUpAsync(string repository, ImageProcessor processor = ImageProcessor.Cpu, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Business.Modules.Agents.LocalModelAddResult> AddAsync(ImageModelLookup lookup, ImageModelFile file, ImageGenerationSettings settings, ImageModelPurpose purpose = ImageModelPurpose.Portraits, ImagePromptStyle? promptStyle = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}

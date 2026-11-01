using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentValidation.Results;
using HomiliaMessenger.Business.Base.Extensions;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Profile;
using HomiliaMessenger.Presentation.Desktop.Base;
using HomiliaMessenger.Presentation.Desktop.Models;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.ViewModels;

/// <summary>
/// The user's own profile: who contacts are talking to.
/// </summary>
public partial class ProfileViewModel : BaseViewModel
{
    private static readonly string[] TRACKED_PROPERTIES = [nameof(Name), nameof(Birthday), nameof(Gender), nameof(About)];

    private readonly IUserProfileService _profiles;
    private byte[]? _photo;
    private bool _loaded;
    private bool _trackChanges;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string? _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private DateOnly? _birthday;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private GenderOption _gender = GenderOption.NotSpecified;

    [ObservableProperty] private string? _about;
    [ObservableProperty] private Bitmap? _photoPreview;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isDirty;

    [ObservableProperty] private string _status = string.Empty;

    public ProfileViewModel(IUserProfileService profiles)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    public IReadOnlyList<GenderOption> GenderOptions => GenderOption.All;

    public int MaxAboutLength => UserProfile.MAX_ABOUT_LENGTH;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Your name" : Name.Trim();

    /// <summary>
    /// A line under the name, such as "Age 42 · Male".
    /// </summary>
    public string Summary
    {
        get
        {
            List<string> parts = [];

            if (Birthday is { } birthday)
            {
                parts.Add($"Age {ContactBirthday.AgeOn(birthday, DateOnly.FromDateTime(DateTime.UtcNow))}");
            }

            if (Gender.Value is not null)
            {
                parts.Add(Gender.Label);
            }

            return parts.Count == 0 ? "Tell your contacts a little about yourself." : string.Join(" · ", parts);
        }
    }

    public bool HasPhoto => _photo is not null;

    public override async Task InitializeAsync()
    {
        if (_loaded)
        {
            return;
        }

        try
        {
            IsBusy = true;
            Load(await _profiles.GetAsync());
            _loaded = true;
        }
        catch (Exception error)
        {
            Status = "Could not load your profile: " + error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Load(UserProfile profile)
    {
        _trackChanges = false;
        Name = profile.Name;
        Birthday = profile.Birthday;
        Gender = GenderOption.For(profile.Gender);
        About = profile.About;
        SetPhoto(profile.Photo);
        IsDirty = false;
        _trackChanges = true;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_trackChanges && Array.IndexOf(TRACKED_PROPERTIES, e.PropertyName) >= 0)
        {
            IsDirty = true;
            Status = string.Empty;
        }
    }

    public UserProfile ToProfile()
    {
        return new UserProfile
        {
            Name = Clean(Name),
            Birthday = Birthday,
            Gender = Gender.Value,
            About = Clean(About),
            Photo = _photo is null ? null : [.. _photo]
        };
    }

    public async Task SetPhotoAsync(Stream stream)
    {
        try
        {
            SetPhoto(await PhotoFile.ReadAsync(stream));
            IsDirty = true;
            Status = "Photo selected. Save your profile to keep it.";
        }
        catch (Exception error)
        {
            ReportPhotoError(error);
        }
    }

    public void ReportPhotoError(Exception error)
    {
        Status = "Could not use photo: " + error.Message;
    }

    [RelayCommand]
    private void RemovePhoto()
    {
        if (_photo is not null)
        {
            SetPhoto(null);
            IsDirty = true;
        }
    }

    [RelayCommand]
    private void ClearBirthday()
    {
        Birthday = null;
    }

    private bool CanSave()
    {
        return IsDirty;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        try
        {
            IsBusy = true;
            ValidationResult result = await _profiles.SaveAsync(ToProfile());

            if (!result.IsValid)
            {
                Status = result.ToStringPretty("Save");
                return;
            }

            IsDirty = false;
            Status = "Profile saved. Your contacts will know who they are talking to.";
        }
        catch (Exception error)
        {
            Status = "Could not save: " + error.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetPhoto(byte[]? bytes)
    {
        Bitmap? previous = PhotoPreview;
        _photo = bytes is null ? null : [.. bytes];
        PhotoPreview = bytes is null ? null : PhotoFile.TryPreview(bytes);
        OnPropertyChanged(nameof(HasPhoto));
        previous?.Dispose();
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

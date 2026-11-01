using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Presentation.Desktop.Base;
using HomiliaMessenger.Presentation.Desktop.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.ViewModels;

/// <summary>
/// Editing state for one persona contact.
/// </summary>
public partial class ContactDetailViewModel : ObservableObject, IDisposable
{
    private readonly ContactId _id;
    private byte[]? _photo;
    private bool _disposed;
    private readonly bool _trackChanges;

    [ObservableProperty] private string? _givenName;
    [ObservableProperty] private string? _familyName;
    [ObservableProperty] private string? _nickname;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgeDescription))]
    private DateOnly? _birthday;

    [ObservableProperty] private GenderOption _gender = GenderOption.NotSpecified;
    [ObservableProperty] private string? _relationship;
    [ObservableProperty] private string? _personality;
    [ObservableProperty] private string? _background;
    [ObservableProperty] private AgentOption _agent = AgentOption.None;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private Bitmap? _photoPreview;
    [ObservableProperty] private string _photoDescription = "No photo";
    [ObservableProperty] private bool _isDirty;

    /// <summary>
    /// Raised with a user-facing message when an editor action (such as choosing a photo) fails.
    /// </summary>
    public event EventHandler<string>? MessageRaised;

    public ContactId Id => _id;

    public IReadOnlyList<GenderOption> GenderOptions => GenderOption.All;

    /// <summary>
    /// No agent, then the models offered in Settings.
    /// </summary>
    public IReadOnlyList<AgentOption> AgentOptions { get; }

    /// <summary>
    /// The persona's current age, derived from <see cref="Birthday"/> as of the UTC date (matching Business validation).
    /// </summary>
    public string AgeDescription => Birthday is { } birthday
        ? $"Age {ContactBirthday.AgeOn(birthday, DateOnly.FromDateTime(DateTime.UtcNow))}"
        : "Birthday required (18 or older)";

    /// <param name="contact">The contact to edit, or <see langword="null"/> for a new one.</param>
    /// <param name="agentOptions">The agents to choose from; when omitted, only no agent.</param>
    public ContactDetailViewModel(Contact? contact = null, IReadOnlyList<AgentOption>? agentOptions = null)
    {
        _id = contact?.Id ?? ContactId.New();
        AgentOptions = agentOptions ?? [AgentOption.None];

        if (contact is not null)
        {
            GivenName = contact.GivenName;
            FamilyName = contact.FamilyName;
            Nickname = contact.Nickname;
            Birthday = contact.Birthday;
            Gender = GenderOption.For(contact.Gender);
            Relationship = contact.Relationship;
            Personality = contact.Personality;
            Background = contact.Background;
            Agent = AgentOptions.FirstOrDefault(option => option.Assignment == contact.Agent) ?? AgentOption.None;
            IsFavorite = contact.IsFavorite;
            LoadStoredPhoto(contact.Photo);
        }

        _trackChanges = true;
    }

    /// <summary>
    /// Marks the current values as saved.
    /// </summary>
    public void AcceptChanges()
    {
        IsDirty = false;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_trackChanges && e.PropertyName is not (nameof(IsDirty) or nameof(PhotoPreview) or nameof(PhotoDescription) or nameof(AgeDescription)))
        {
            IsDirty = true;
        }
    }

    // A stored photo that cannot be previewed must not break opening the contact;
    // the bytes are kept as they are so saving does not discard them.
    private void LoadStoredPhoto(byte[]? bytes)
    {
        try
        {
            SetPhoto(bytes);
        }
        catch (InvalidDataException)
        {
            _photo = bytes is null ? null : [.. bytes];
            PhotoPreview = null;
            PhotoDescription = "Photo format not supported for preview";
        }
    }

    [RelayCommand]
    private void FlipFavorite()
    {
        IsFavorite = !IsFavorite;
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

    public async Task SetPhotoAsync(Stream stream)
    {
        try
        {
            SetPhoto(await PhotoFile.ReadAsync(stream));
            IsDirty = true;
            MessageRaised?.Invoke(this, "Photo selected. Save the contact to keep it.");
        }
        catch (Exception error)
        {
            ReportPhotoError(error);
        }
    }

    /// <summary>
    /// Reports a failure to pick or open a photo file through the editor's user-facing message path.
    /// </summary>
    public void ReportPhotoError(Exception error)
    {
        MessageRaised?.Invoke(this, "Could not use photo: " + error.Message);
    }

    public void SetPhoto(byte[]? bytes)
    {
        Bitmap? preview = null;

        if (bytes is not null)
        {
            PhotoFile.EnsureSupported(bytes);

            // Keep the original bytes even if the platform cannot preview the image.
            preview = PhotoFile.TryPreview(bytes);
        }

        Bitmap? previous = PhotoPreview;
        _photo = bytes is null ? null : [.. bytes];
        PhotoPreview = preview;
        PhotoDescription = PhotoFile.Describe(bytes, preview is not null);

        previous?.Dispose();
    }

    public Contact ToContact()
    {
        return new Contact
        {
            Id = _id,
            GivenName = Clean(GivenName),
            FamilyName = Clean(FamilyName),
            Nickname = Clean(Nickname),
            Birthday = Birthday,
            Gender = Gender.Value,
            Relationship = Clean(Relationship),
            Personality = Clean(Personality),
            Background = Clean(Background),
            Agent = Agent.Assignment,
            IsFavorite = IsFavorite,
            Photo = _photo is null ? null : [.. _photo]
        };
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            PhotoPreview?.Dispose();
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

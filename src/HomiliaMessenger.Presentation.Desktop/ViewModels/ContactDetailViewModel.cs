using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.Contacts;
using System;
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
    private const int MAX_PHOTO_BYTES = 8 * 1024 * 1024;

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

    [ObservableProperty] private string? _gender;
    [ObservableProperty] private string? _personality;
    [ObservableProperty] private string? _background;
    [ObservableProperty] private string? _agentProvider;
    [ObservableProperty] private string? _agentModel;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private Bitmap? _photoPreview;
    [ObservableProperty] private string _photoDescription = "No photo";
    [ObservableProperty] private bool _isDirty;

    /// <summary>
    /// Raised with a user-facing message when an editor action (such as choosing a photo) fails.
    /// </summary>
    public event EventHandler<string>? MessageRaised;

    public ContactId Id => _id;

    /// <summary>
    /// The persona's current age, derived from <see cref="Birthday"/> as of the UTC date (matching Business validation).
    /// </summary>
    public string AgeDescription => Birthday is { } birthday
        ? $"Age {ContactBirthday.AgeOn(birthday, DateOnly.FromDateTime(DateTime.UtcNow))}"
        : "Birthday required (18 or older)";

    public ContactDetailViewModel(Contact? contact = null)
    {
        _id = contact?.Id ?? ContactId.New();

        if (contact is not null)
        {
            GivenName = contact.GivenName;
            FamilyName = contact.FamilyName;
            Nickname = contact.Nickname;
            Birthday = contact.Birthday;
            Gender = contact.Gender;
            Personality = contact.Personality;
            Background = contact.Background;
            AgentProvider = contact.Agent?.Provider;
            AgentModel = contact.Agent?.Model;
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
            if (stream.CanSeek && stream.Length - stream.Position > MAX_PHOTO_BYTES)
            {
                throw new InvalidDataException("Photos must be 8 MiB or smaller.");
            }

            // Read at most one byte past the limit so an oversized stream is rejected without buffering all of it.
            using MemoryStream buffer = new();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MAX_PHOTO_BYTES + 1 - buffer.Length)))) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MAX_PHOTO_BYTES)
                {
                    throw new InvalidDataException("Photos must be 8 MiB or smaller.");
                }
            }

            SetPhoto(buffer.ToArray());
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
        if (bytes?.Length > MAX_PHOTO_BYTES)
        {
            throw new InvalidDataException("Photos must be 8 MiB or smaller.");
        }

        Bitmap? preview = null;
        if (bytes is not null)
        {
            if (!IsJpeg(bytes) && !IsPng(bytes))
            {
                throw new InvalidDataException("Select a JPEG or PNG photo.");
            }

            try
            {
                using MemoryStream stream = new(bytes);
                preview = new Bitmap(stream);
            }
            catch
            {
                // Keep the original bytes even if the platform cannot preview the image.
            }
        }

        Bitmap? previous = PhotoPreview;
        _photo = bytes is null ? null : [.. bytes];
        PhotoPreview = preview;
        PhotoDescription = bytes is null ? "No photo" : preview is null ? "Photo saved; preview unavailable" :
            $"{(IsPng(bytes) ? "PNG" : "JPEG")} photo ({bytes.Length / 1024} KiB)";

        previous?.Dispose();
    }

    private static bool IsPng(byte[] data)
    {
        return data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    }

    private static bool IsJpeg(byte[] data)
    {
        return data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff;
    }

    public Contact ToContact()
    {
        string? agentProvider = Clean(AgentProvider);
        string? agentModel = Clean(AgentModel);

        return new Contact
        {
            Id = _id,
            GivenName = Clean(GivenName),
            FamilyName = Clean(FamilyName),
            Nickname = Clean(Nickname),
            Birthday = Birthday,
            Gender = Clean(Gender),
            Personality = Clean(Personality),
            Background = Clean(Background),

            // A half-filled assignment is passed on so Business validation can explain what is missing.
            Agent = agentProvider is null && agentModel is null ? null : new AgentAssignment(agentProvider ?? string.Empty, agentModel ?? string.Empty),
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

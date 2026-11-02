using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Speech;
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
    private IReadOnlyList<SpeechVoice> _voices = [];
    private bool _refreshingVoices;
    private bool _refreshingAgents;
    private string? _voiceId;

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

    /// <summary>
    /// No agent, then the models offered in Settings, as of the last refresh.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<AgentOption> _agentOptions = [AgentOption.None];
    [ObservableProperty] private VoiceOption? _voice = VoiceOption.None;
    [ObservableProperty] private bool _isSpeechAvailable;
    [ObservableProperty] private IReadOnlyList<VoiceOption> _voiceOptions = [VoiceOption.None];

    public string VoiceDescription => _voiceId is null ? "No voice selected."
        : "Current voice: " + (_voices.FirstOrDefault(voice => voice.Id == _voiceId)?.DisplayName ?? _voiceId);
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
    /// The persona's current age, derived from <see cref="Birthday"/> as of the UTC date (matching Business validation).
    /// </summary>
    public string AgeDescription => Birthday is { } birthday
        ? $"Age {ContactBirthday.AgeOn(birthday, DateOnly.FromDateTime(DateTime.UtcNow))}"
        : "Birthday required (18 or older)";

    /// <param name="contact">The contact to edit, or <see langword="null"/> for a new one.</param>
    /// <param name="agentOptions">The agents to choose from; when omitted, only no agent.</param>
    public ContactDetailViewModel(Contact? contact = null, IReadOnlyList<AgentOption>? agentOptions = null, IReadOnlyList<SpeechVoice>? voices = null, bool isSpeechAvailable = false)
    {
        _id = contact?.Id ?? ContactId.New();
        AgentOptions = agentOptions ?? [AgentOption.None];
        _voiceId = contact?.VoiceId;

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

        RefreshVoices(voices ?? [], isSpeechAvailable);
        _trackChanges = true;
    }

    /// <summary>
    /// Offers the current models, such as one just added or downloaded in Settings, keeping the contact's agent and any
    /// unsaved edits. Replacing the options is not itself an edit.
    /// </summary>
    public void RefreshAgentOptions(IReadOnlyList<AgentOption> agentOptions)
    {
        ArgumentNullException.ThrowIfNull(agentOptions);
        AgentOption? previous = Agent;
        AgentAssignment? current = previous?.Assignment;
        _refreshingAgents = true;

        try
        {
            // A model removed in Settings stays chosen, marked as removed, so the assignment is not lost; adding the
            // model again restores it.
            AgentOptions = current is null || agentOptions.Any(option => option.Assignment == current)
                ? agentOptions
                : [.. agentOptions, new AgentOption($"{previous!.Label.Replace(" (not downloaded)", string.Empty, StringComparison.Ordinal)} (removed)", current)];

            // The combo box may clear its selection while its options are replaced; select the same agent again.
            Agent = AgentOptions.FirstOrDefault(option => option.Assignment == current) ?? AgentOption.None;
        }
        finally
        {
            _refreshingAgents = false;
        }
    }

    public void RefreshVoices(IReadOnlyList<SpeechVoice> voices, bool isAvailable)
    {
        _refreshingVoices = true;
        try
        {
            _voices = voices;
            IsSpeechAvailable = isAvailable;
            VoiceOptions = [VoiceOption.None, .. _voices.Where(voice => Gender.Value is null || voice.Gender == Gender.Value)
                .Select(voice => new VoiceOption(voice.DisplayName, voice.Id, voice.Gender))];
            // A saved voice outside the filter is described separately. A cleared ComboBox selection is not an edit;
            // only choosing an actual option (including None) changes the saved voice.
            Voice = VoiceOptions.FirstOrDefault(option => option.Id == _voiceId);
            OnPropertyChanged(nameof(VoiceDescription));
        }
        finally
        {
            _refreshingVoices = false;
        }
    }

    partial void OnGenderChanged(GenderOption value)
    {
        RefreshVoices(_voices, IsSpeechAvailable);
    }

    partial void OnVoiceChanged(VoiceOption? value)
    {
        // Replacing the options here, while the combo box is still applying the user's choice, could clear it. The
        // options depend only on gender, so only the description needs updating.
        if (!_refreshingVoices && value is not null)
        {
            _voiceId = value.Id;
            OnPropertyChanged(nameof(VoiceDescription));
        }
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

        if (_trackChanges && !_refreshingVoices && !_refreshingAgents && !(e.PropertyName == nameof(Voice) && Voice is null)
            && e.PropertyName is not (nameof(IsDirty) or nameof(PhotoPreview) or nameof(PhotoDescription) or nameof(AgeDescription)))
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
            Agent = Agent?.Assignment,
            VoiceId = _voiceId,
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

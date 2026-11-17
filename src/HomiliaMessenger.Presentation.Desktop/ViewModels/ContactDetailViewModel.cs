using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using HomiliaMessenger.Business.Modules.Speech;
using HomiliaMessenger.Presentation.Desktop.Base;
using HomiliaMessenger.Presentation.Desktop.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
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
    private string? _voiceId;
    private AgentAssignment? _agentAssignment;

    // The values as last saved (or as opened), which the editor is dirty only while it differs from. The photo is kept
    // apart because arrays compare by reference; photo arrays are replaced, never changed in place.
    private Contact _saved;
    private byte[]? _savedPhoto;
    private readonly ITextToSpeech? _speech;
    private readonly IPortraitService? _portraits;
    private CancellationTokenSource? _sample;

    // The description last used to generate a photo, offered again so it can be adjusted rather than retyped.
    private string? _portraitDescription;

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
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayVoiceSampleCommand))]
    private VoiceOption? _voice = VoiceOption.None;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceSampleHint))]
    private bool _isPlayingSample;

    public string VoiceSampleHint => IsPlayingSample ? "Stop sample" : "Play a sample of this voice";
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

    /// <summary>
    /// Raised when the user asks to go to Settings, such as to download an image model.
    /// </summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Whether photos can be generated from a description, in addition to being chosen from a file.
    /// </summary>
    public bool CanGeneratePhoto => _portraits is not null;

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
    public ContactDetailViewModel(Contact? contact = null, IReadOnlyList<AgentOption>? agentOptions = null, IReadOnlyList<SpeechVoice>? voices = null, bool isSpeechAvailable = false, ITextToSpeech? speech = null, IPortraitService? portraits = null)
    {
        _speech = speech;
        _portraits = portraits;
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
        _saved = Snapshot();
        _savedPhoto = _photo;
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
        AgentAssignment? current = _agentAssignment;

        // A model removed in Settings stays chosen, marked as removed, so the assignment is not lost; adding the model
        // again restores it.
        AgentOptions = current is null || agentOptions.Any(option => option.Assignment == current)
            ? agentOptions
            : [.. agentOptions, new AgentOption($"{(previous?.Label ?? current.Model).Replace(" (not downloaded)", string.Empty, StringComparison.Ordinal)} (removed)", current)];

        // The combo box may clear its selection while its options are replaced; select the same agent again.
        Agent = AgentOptions.FirstOrDefault(option => option.Assignment == current) ?? AgentOption.None;
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
        StopVoiceSample();

        // Replacing the options here, while the combo box is still applying the user's choice, could clear it. The
        // options depend only on gender, so only the description needs updating.
        if (!_refreshingVoices && value is not null)
        {
            _voiceId = value.Id;
            OnPropertyChanged(nameof(VoiceDescription));
        }
    }

    partial void OnAgentChanged(AgentOption value)
    {
        // A combo box clears its selection while its options are replaced, such as when the editor shows another
        // contact; a cleared selection keeps the contact's agent.
        if (value is not null)
        {
            _agentAssignment = value.Assignment;
        }
    }

    private bool CanPlayVoiceSample()
    {
        return _speech is not null && Voice?.Id is not null;
    }

    /// <summary>
    /// Plays a short sample of the selected voice, or stops the one already playing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPlayVoiceSample), AllowConcurrentExecutions = true)]
    private async Task PlayVoiceSampleAsync()
    {
        if (IsPlayingSample)
        {
            StopVoiceSample();
            return;
        }

        if (_speech is null || Voice?.Id is not { } voiceId)
        {
            return;
        }

        CancellationTokenSource sample = new();
        _sample = sample;
        IsPlayingSample = true;

        try
        {
            string name = Clean(Nickname) ?? Clean(GivenName) ?? "your contact";
            SpeechResult result = await _speech.SpeakAsync($"Hi, it's {name}. This is how I sound.", voiceId, sample.Token);
            if (!result.IsSuccessful && ReferenceEquals(sample, _sample))
            {
                MessageRaised?.Invoke(this, "Could not play the voice: " + (result.Problem ?? "unknown problem"));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped, another voice chosen, or the editor closed.
        }
        catch (Exception error)
        {
            MessageRaised?.Invoke(this, "Could not play the voice: " + error.Message);
        }
        finally
        {
            if (ReferenceEquals(sample, _sample))
            {
                _sample = null;
                IsPlayingSample = false;
            }

            sample.Dispose();
        }
    }

    private void StopVoiceSample()
    {
        CancellationTokenSource? sample = _sample;
        _sample = null;
        IsPlayingSample = false;
        sample?.Cancel();
    }

    /// <summary>
    /// Marks the current values as saved.
    /// </summary>
    public void AcceptChanges()
    {
        _saved = Snapshot();
        _savedPhoto = _photo;
        IsDirty = false;
    }

    /// <summary>
    /// Whether the editor's values differ from those last saved. Comparing values, rather than counting every change as
    /// an edit, keeps the editor clean when a control writes a value back without the user changing anything, as the
    /// agent and voice selectors briefly do while the editor switches from one contact to another.
    /// </summary>
    private void UpdateDirty()
    {
        bool samePhoto = _photo is null ? _savedPhoto is null : _savedPhoto is not null && _photo.AsSpan().SequenceEqual(_savedPhoto);
        IsDirty = !samePhoto || Snapshot() != _saved;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_trackChanges && e.PropertyName is not (nameof(IsDirty) or nameof(PhotoPreview) or nameof(PhotoDescription) or nameof(AgeDescription)
            or nameof(IsPlayingSample) or nameof(VoiceSampleHint) or nameof(VoiceDescription) or nameof(AgentOptions) or nameof(VoiceOptions)
            or nameof(IsSpeechAvailable)))
        {
            UpdateDirty();
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
            UpdateDirty();
        }
    }

    public async Task SetPhotoAsync(Stream stream)
    {
        try
        {
            SetPhoto(await PhotoFile.ReadAsync(stream));
            UpdateDirty();
            MessageRaised?.Invoke(this, "Photo selected. Save the contact to keep it.");
        }
        catch (Exception error)
        {
            ReportPhotoError(error);
        }
    }

    /// <summary>
    /// A dialog for generating a photo, offering the description used last time; <see langword="null"/> when photos
    /// cannot be generated.
    /// </summary>
    public GeneratePhotoViewModel? CreatePhotoGenerator()
    {
        return _portraits is null ? null : new GeneratePhotoViewModel(_portraits, _portraitDescription);
    }

    /// <summary>
    /// Applies what the user did in the photo dialog: the photo they chose to use replaces the current one, through the
    /// same path as a chosen file; closing the dialog any other way changes nothing.
    /// </summary>
    public void AcceptGeneratedPhoto(GeneratePhotoViewModel generator)
    {
        ArgumentNullException.ThrowIfNull(generator);

        if (!string.IsNullOrWhiteSpace(generator.Description))
        {
            _portraitDescription = generator.Description;
        }

        if (generator.ChosenPhoto is { } photo)
        {
            try
            {
                SetPhoto(photo);
                UpdateDirty();
                MessageRaised?.Invoke(this, "Photo generated. Save the contact to keep it.");
            }
            catch (Exception error)
            {
                ReportPhotoError(error);
            }
        }

        if (generator.WantsSettings)
        {
            SettingsRequested?.Invoke(this, EventArgs.Empty);
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
        return Snapshot() with { Photo = _photo is null ? null : [.. _photo] };
    }

    // The editor's values without the photo, which is compared and copied separately.
    private Contact Snapshot()
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
            Agent = _agentAssignment,
            VoiceId = _voiceId,
            IsFavorite = IsFavorite
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
            StopVoiceSample();
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

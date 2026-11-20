using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.ImageGeneration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HomiliaMessenger.Presentation.Desktop.Models;

/// <summary>
/// A LoRA in Settings: what it was made for, whether it is on, how strongly, its trigger words, and for what. Every
/// change is saved as it is made.
/// </summary>
public sealed partial class ImageLoraItem : ObservableObject
{
    private readonly IImageLoraLibrary _library;
    private readonly IReadOnlyList<ImageModel> _models;
    private bool _loading;

    [ObservableProperty] private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrengthText))]
    private double _strength;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseSuggestedTriggerWords))]
    private string _triggerWords;

    [ObservableProperty] private bool _forPhotos;
    [ObservableProperty] private bool _forSelfies;
    [ObservableProperty] private string _status = string.Empty;

    /// <param name="models">Every image model offered, to say which this LoRA fits.</param>
    public ImageLoraItem(ImageLora lora, IImageLoraLibrary library, IReadOnlyList<ImageModel> models)
    {
        ArgumentNullException.ThrowIfNull(lora);
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        Name = lora.Name;
        DisplayName = lora.DisplayName;
        Base = lora.Base;
        Note = lora.Note;
        SuggestedTriggerWords = lora.SuggestedTriggerWords;
        TriggerWordsSource = lora.TriggerWordsSource;

        _loading = true;
        _isEnabled = lora.Settings.IsEnabled;
        _strength = lora.Settings.Strength;
        _triggerWords = lora.Settings.TriggerWords;
        _forPhotos = lora.Settings.Purposes.HasFlag(ImageModelPurpose.Portraits);
        _forSelfies = lora.Settings.Purposes.HasFlag(ImageModelPurpose.Selfies);
        _loading = false;

        string[] fits = [.. models.Where(model => lora.Fits(model.Family)).Select(model => model.DisplayName).Distinct()];
        IsUsable = fits.Length > 0;
        Details = $"{FormatSize(lora.Bytes)} · {lora.BaseDescription} · "
            + (IsUsable ? "Fits " + string.Join(", ", fits) : lora.Base == ImageModelFamily.Unknown ? "Base model not recognized; not used" : "No model here fits it; not used");
    }

    public string Name { get; }

    public string DisplayName { get; }

    public ImageModelFamily Base { get; }

    /// <summary>
    /// Its size, base model, and the models it fits.
    /// </summary>
    public string Details { get; }

    public string? Note { get; }

    public bool HasNote => Note is not null;

    /// <summary>
    /// Whether any offered model fits it; a LoRA for another base can be listed, but is never used.
    /// </summary>
    public bool IsUsable { get; }

    public string? SuggestedTriggerWords { get; }

    public string? TriggerWordsSource { get; }

    public string TriggerWordsHint => SuggestedTriggerWords is null
        ? "No trigger words were found for it. If its download page names any, enter them here."
        : $"Found in {TriggerWordsSource}: {SuggestedTriggerWords}";

    public bool CanUseSuggestedTriggerWords => SuggestedTriggerWords is not null && TriggerWords != SuggestedTriggerWords;

    public string StrengthText => Strength.ToString("0.00", CultureInfo.CurrentCulture);

    public double MinStrength => ImageLoraSettings.MIN_STRENGTH;

    public double MaxStrength => ImageLoraSettings.MAX_STRENGTH;

    [RelayCommand]
    private void UseSuggestedTriggerWords()
    {
        if (SuggestedTriggerWords is not null)
        {
            TriggerWords = SuggestedTriggerWords;
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        Persist();
    }

    partial void OnStrengthChanged(double value)
    {
        Persist();
    }

    partial void OnTriggerWordsChanged(string value)
    {
        Persist();
    }

    partial void OnForPhotosChanged(bool value)
    {
        Persist();
    }

    partial void OnForSelfiesChanged(bool value)
    {
        Persist();
    }

    private void Persist()
    {
        if (_loading)
        {
            return;
        }

        ImageModelPurpose purposes = (ForPhotos ? ImageModelPurpose.Portraits : ImageModelPurpose.None) | (ForSelfies ? ImageModelPurpose.Selfies : ImageModelPurpose.None);
        float strength = (float)Math.Round(Strength, 2);

        try
        {
            Status = _library.Save(Name, new ImageLoraSettings(IsEnabled, strength, TriggerWords ?? string.Empty, purposes)) ?? string.Empty;
        }
        catch (Exception error)
        {
            Status = "Could not save the LoRA's settings: " + error.Message;
        }
    }

    private static string FormatSize(long bytes)
    {
        return bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000d:0.0} GB" : $"{bytes / 1_000_000d:0} MB";
    }
}

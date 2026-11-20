using HomiliaMessenger.Business.Modules.ImageGeneration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// LoRAs from a folder the user chooses, and how each is used, kept in <c>loras.json</c> beside the image models. The
/// files stay where the user keeps them; nothing is copied or downloaded.
/// </summary>
/// <remarks>
/// A LoRA's base is read from its file (see <see cref="LoraInspector"/>), so one is only ever applied to models of the
/// family it was trained for: SDXL LoRAs to DreamShaper XL Lightning, Stable Diffusion 1.5 LoRAs to DreamShaper 8 LCM, and
/// FLUX.2 [klein] 4B LoRAs to FLUX.2 [klein] 4B. A LoRA is off until the user turns it on. Inspections are kept for as
/// long as a file's size and time are unchanged.
/// </remarks>
internal sealed class ImageLoraLibrary : IImageLoraLibrary
{
    internal const string SETTINGS_FILE = "loras.json";
    internal const string EXTENSION = ".safetensors";

    private static readonly JsonSerializerOptions JSON = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly string _directory;
    private readonly ILogger<ImageLoraLibrary> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string Stamp, LoraInspection Inspection)> _inspections = new(StringComparer.OrdinalIgnoreCase);
    private Stored _stored;

    /// <param name="directory">The image models' folder, where the settings are kept.</param>
    public ImageLoraLibrary(string directory, ILogger<ImageLoraLibrary> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stored = Load();
    }

    public string? Folder
    {
        get
        {
            lock (_lock)
            {
                return _stored.Folder;
            }
        }
    }

    public string? SetFolder(string? folder)
    {
        string? chosen = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (chosen is not null)
        {
            try
            {
                chosen = Path.GetFullPath(chosen);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return "That is not a folder path.";
            }

            if (!Directory.Exists(chosen))
            {
                return "That folder does not exist.";
            }
        }

        lock (_lock)
        {
            // Settings are kept for each folder, so another folder starts with its own (none, at first): a file there that
            // shares a name with one turned on here is a different LoRA, and stays off until it is turned on. Coming back
            // to a folder finds its settings as they were.
            try
            {
                Save(_stored with { Folder = chosen });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(error, "Could not save the LoRA folder.");
                return "Could not save the LoRA folder: " + error.Message;
            }
        }

        _logger.LogInformation("LoRAs are now read from {Folder}.", chosen ?? "(no folder)");

        return null;
    }

    public Task<IReadOnlyList<ImageLora>> ScanAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ImageLora>>(() =>
        {
            string? folder = Folder;

            if (folder is null || !Directory.Exists(folder))
            {
                return [];
            }

            List<ImageLora> found = [];
            EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4, MatchCasing = MatchCasing.CaseInsensitive };

            foreach (string path in Directory.EnumerateFiles(folder, "*" + EXTENSION, options).Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Name(folder, path);
                LoraInspection inspection = InspectCached(path);
                ImageLoraSettings settings = SettingsOf(name, inspection);

                found.Add(new ImageLora(
                    name,
                    Path.GetFileNameWithoutExtension(path),
                    new FileInfo(path).Length,
                    inspection.Base,
                    inspection.BaseDescription,
                    inspection.Problem ?? inspection.Note,
                    inspection.TriggerWords,
                    inspection.TriggerWordsSource,
                    inspection.Strength,
                    settings));
            }

            return found;
        }, cancellationToken);
    }

    public string? Save(string name, ImageLoraSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Problem() is { } problem)
        {
            return problem;
        }

        lock (_lock)
        {
            if (_stored.Folder is not { } folder)
            {
                return "Choose a LoRA folder first.";
            }

            Dictionary<string, ImageLoraSettings> loras = new(_stored.Current, StringComparer.OrdinalIgnoreCase) { [name] = settings with { TriggerWords = settings.TriggerWords.Trim() } };
            Dictionary<string, Dictionary<string, ImageLoraSettings>> folders = new(_stored.Folders, StringComparer.OrdinalIgnoreCase) { [folder] = loras };
            Save(_stored with { Folders = folders });
        }

        return null;
    }

    public IReadOnlyList<ImageLoraUse> InUse(ImageModelFamily family, ImageModelPurpose purpose)
    {
        string? folder;
        List<KeyValuePair<string, ImageLoraSettings>> enabled;

        lock (_lock)
        {
            folder = _stored.Folder;
            enabled = [.. _stored.Current.Where(entry => entry.Value.IsEnabled && entry.Value.Purposes.HasFlag(purpose) && entry.Value.Strength > 0)];
        }

        if (folder is null || enabled.Count == 0)
        {
            return [];
        }

        List<ImageLoraUse> used = [];
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;

        foreach ((string name, ImageLoraSettings settings) in enabled.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.GetFullPath(Path.Combine(folder, name));

            // Never outside the folder, whatever the settings file says.
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                _logger.LogWarning("LoRA {Name} is on but is no longer in {Folder}, so it is not used.", name, folder);
                continue;
            }

            if (InspectCached(path).Base == family && family != ImageModelFamily.Unknown)
            {
                used.Add(new ImageLoraUse(name, path, settings.Strength, settings.TriggerWords));
            }
        }

        return used;
    }

    /// <summary>
    /// A LoRA's settings as saved, or as suggested when it was never changed: off, at the strength its notes suggest, with
    /// the trigger words found for it, for contact photos and selfies.
    /// </summary>
    private ImageLoraSettings SettingsOf(string name, LoraInspection inspection)
    {
        lock (_lock)
        {
            return _stored.Current.TryGetValue(name, out ImageLoraSettings? saved)
                ? saved
                : new ImageLoraSettings(false, inspection.Strength ?? ImageLoraSettings.DEFAULT_STRENGTH, inspection.TriggerWords ?? string.Empty, ImageModelPurpose.Portraits | ImageModelPurpose.Selfies);
        }
    }

    private LoraInspection InspectCached(string path)
    {
        string stamp = Stamp(path);

        lock (_lock)
        {
            if (_inspections.TryGetValue(path, out (string Stamp, LoraInspection Inspection) cached) && cached.Stamp == stamp)
            {
                return cached.Inspection;
            }
        }

        LoraInspection inspection = LoraInspector.Inspect(path);

        lock (_lock)
        {
            _inspections[path] = (stamp, inspection);
        }

        return inspection;
    }

    /// <summary>
    /// The size and time of a LoRA and of the notes beside it, which its inspection reads, so editing either reads it again.
    /// </summary>
    private static string Stamp(string path)
    {
        string stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));

        return string.Join("|", new[] { path, stem + ".civitai.info", stem + ".json" }.Select(file => new FileInfo(file) is { Exists: true } info
            ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}"
            : "-"));
    }

    /// <summary>
    /// A file's path within the folder, with forward slashes, which identifies it in the settings.
    /// </summary>
    private static string Name(string folder, string path)
    {
        return Path.GetRelativePath(folder, path).Replace('\\', '/');
    }

    private Stored Load()
    {
        string path = Path.Combine(_directory, SETTINGS_FILE);

        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), JSON) is { } stored)
            {
                return stored with
                {
                    Folders = new Dictionary<string, Dictionary<string, ImageLoraSettings>>(
                        (stored.Folders ?? []).Select(entry => KeyValuePair.Create(entry.Key, new Dictionary<string, ImageLoraSettings>(entry.Value ?? [], StringComparer.OrdinalIgnoreCase))),
                        StringComparer.OrdinalIgnoreCase)
                };
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(error, "Could not read the LoRA settings from {Path}.", path);
        }

        return new Stored(null, new Dictionary<string, Dictionary<string, ImageLoraSettings>>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Writes a new file and replaces the old one, so an interrupted save cannot leave a half-written file. Called with the
    /// lock held.
    /// </summary>
    private void Save(Stored next)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, SETTINGS_FILE);
        string temporary = path + ".tmp";

        File.WriteAllText(temporary, JsonSerializer.Serialize(next, JSON));
        File.Move(temporary, path, overwrite: true);

        // In use only once saved, so a failed save changes nothing.
        _stored = next;
    }

    /// <summary>
    /// The chosen folder, and each folder's LoRA settings by a file's path within it.
    /// </summary>
    private sealed record Stored(string? Folder, Dictionary<string, Dictionary<string, ImageLoraSettings>> Folders)
    {
        private static readonly Dictionary<string, ImageLoraSettings> NONE = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The chosen folder's settings.
        /// </summary>
        [JsonIgnore]
        public IReadOnlyDictionary<string, ImageLoraSettings> Current => Folder is not null && Folders.TryGetValue(Folder, out Dictionary<string, ImageLoraSettings>? settings) ? settings : NONE;
    }
}

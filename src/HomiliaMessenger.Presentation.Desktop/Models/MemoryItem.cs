using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Modules.Memories;
using System;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.Models;

/// <summary>
/// One memory in the memory dialog, which the user can correct in place or delete.
/// </summary>
public sealed partial class MemoryItem : ObservableObject
{
    private readonly Func<MemoryItem, Task> _saveAsync;
    private readonly Func<MemoryItem, Task> _deleteAsync;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OriginText))]
    private Memory _memory;

    [ObservableProperty] private bool _isEditing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _editText = string.Empty;

    public MemoryItem(Memory memory, Func<MemoryItem, Task> saveAsync, Func<MemoryItem, Task> deleteAsync)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
        _deleteAsync = deleteAsync ?? throw new ArgumentNullException(nameof(deleteAsync));
    }

    public string Text => Memory.Text;

    /// <summary>
    /// Whether the contact learned this from the chat or the user wrote it, in words.
    /// </summary>
    public string OriginText => Memory.Origin switch
    {
        MemoryOrigin.Extracted => "Learned from your chat",
        MemoryOrigin.Imported => "From your earlier notes",
        _ => "Added by you"
    };

    public int MaxLength => Business.Modules.Memories.Memory.MAX_TEXT_LENGTH;

    partial void OnMemoryChanged(Memory value)
    {
        OnPropertyChanged(nameof(Text));
    }

    [RelayCommand]
    private void Edit()
    {
        EditText = Memory.Text;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
    }

    private bool CanSave()
    {
        return !string.IsNullOrWhiteSpace(EditText);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync()
    {
        return _saveAsync(this);
    }

    [RelayCommand]
    private Task DeleteAsync()
    {
        return _deleteAsync(this);
    }
}

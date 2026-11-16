using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Business.Base.Extensions;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Business.Modules.Conversations;
using HomiliaMessenger.Business.Modules.Memories;
using HomiliaMessenger.Presentation.Desktop.Models;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.ViewModels;

/// <summary>
/// Reading and correcting what a contact remembers: each memory can be edited or deleted, and new ones added.
/// </summary>
public partial class MemoryEditorViewModel : ObservableObject
{
    private readonly IMemoryService _memories;
    private readonly ConversationId _conversationId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _newText = string.Empty;

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isEmpty = true;

    public MemoryEditorViewModel(IMemoryService memories, Contact contact, ConversationId conversationId)
    {
        _memories = memories ?? throw new ArgumentNullException(nameof(memories));
        ArgumentNullException.ThrowIfNull(contact);
        ContactId = contact.Id;
        ContactName = contact.DisplayName;
        _conversationId = conversationId;
    }

    public ContactId ContactId { get; }

    public string ContactName { get; }

    public string Heading => $"What {ContactName} remembers";

    public string Explanation => $"As your chat grows, {ContactName} picks out lasting things you've shared, like names, places, "
        + $"dates, and plans, so they aren't forgotten. Recent messages are remembered as they are. Correct anything that's "
        + $"wrong, or add what you want {ContactName} to remember.";

    public string EmptyText => $"Nothing yet. {ContactName} starts remembering once your chat is longer than they can keep in mind at once.";

    public int MaxLength => Memory.MAX_TEXT_LENGTH;

    public ObservableCollection<MemoryItem> Memories { get; } = [];

    /// <summary>
    /// Whether any memory was added, corrected, or deleted, so the changed ones can be embedded afterwards.
    /// </summary>
    public bool HasChanges { get; private set; }

    public async Task LoadAsync()
    {
        try
        {
            Memories.Clear();

            foreach (Memory memory in await _memories.ListAsync(ContactId))
            {
                Memories.Add(CreateItem(memory));
            }

            Status = null;
        }
        catch (Exception error)
        {
            Status = "Could not load the memories: " + error.Message;
        }

        IsEmpty = Memories.Count == 0;
    }

    private bool CanAdd()
    {
        return !string.IsNullOrWhiteSpace(NewText);
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        try
        {
            MemorySaveResult result = await _memories.AddAsync(_conversationId, NewText);

            if (!result.Validation.IsValid || result.Memory is null)
            {
                Status = result.Validation.ToStringPretty("Adding the memory");
                return;
            }

            Memories.Add(CreateItem(result.Memory));
            NewText = string.Empty;
            HasChanges = true;
            IsEmpty = false;
            Status = null;
        }
        catch (Exception error)
        {
            Status = "Could not add the memory: " + error.Message;
        }
    }

    private MemoryItem CreateItem(Memory memory)
    {
        return new MemoryItem(memory, SaveAsync, DeleteAsync);
    }

    private async Task SaveAsync(MemoryItem item)
    {
        if (string.Equals(item.EditText.Trim(), item.Memory.Text, StringComparison.Ordinal))
        {
            item.IsEditing = false;
            return;
        }

        try
        {
            MemorySaveResult result = await _memories.UpdateAsync(item.Memory.Id, item.EditText, item.Memory.Text);

            if (!result.Validation.IsValid || result.Memory is null)
            {
                Status = result.Validation.ToStringPretty("Saving the memory");
                return;
            }

            item.Memory = result.Memory;
            item.IsEditing = false;
            HasChanges = true;
            Status = null;
        }
        catch (Exception error)
        {
            Status = "Could not save the memory: " + error.Message;
        }
    }

    private async Task DeleteAsync(MemoryItem item)
    {
        try
        {
            await _memories.DeleteAsync(item.Memory.Id);
            Memories.Remove(item);
            HasChanges = true;
            IsEmpty = Memories.Count == 0;
            Status = null;
        }
        catch (Exception error)
        {
            Status = "Could not delete the memory: " + error.Message;
        }
    }
}

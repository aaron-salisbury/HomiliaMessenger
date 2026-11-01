using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentValidation.Results;
using HomiliaMessenger.Business.Base.Extensions;
using HomiliaMessenger.Business.Modules.Agents;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Presentation.Desktop.Base.Controls.RibbonControls;
using HomiliaMessenger.Presentation.Desktop.Models;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.ViewModels;

public partial class ContactsViewModel : BaseViewModel, IRibbonProvider
{
    private readonly IContactService _contacts;
    private readonly ILocalModelLibrary _localModels;
    private bool _loaded;
    private bool _preserveDetail;
    private bool _syncingListSelection;

    /// <summary>
    /// The contact whose details are shown. Set programmatically; user selection goes through <see cref="ListSelection"/>.
    /// </summary>
    [ObservableProperty] private Contact? _selectedContact;

    /// <summary>
    /// The list's selected item. A user selection is only applied to <see cref="SelectedContact"/> once any unsaved
    /// changes in <see cref="Detail"/> may be discarded.
    /// </summary>
    [ObservableProperty] private Contact? _listSelection;

    [ObservableProperty] private ContactDetailViewModel _detail;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _status = "Loading contacts…";
    [ObservableProperty] private bool _hasContacts;

    public ObservableCollection<Contact> Contacts { get; } = [];
    public ObservableCollection<Contact> VisibleContacts { get; } = [];

    /// <summary>
    /// Supplied by the view to ask the user whether unsaved contact changes may be discarded.
    /// </summary>
    public Func<Task<bool>>? ConfirmDiscardChangesAsync { get; set; }

    public Type RibbonControlType => typeof(ContactsRibbonControl);

    public ContactsViewModel(IContactService contacts, ILocalModelLibrary localModels)
    {
        _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        _localModels = localModels ?? throw new ArgumentNullException(nameof(localModels));
        _detail = CreateDetail(null);
        _detail.MessageRaised += Detail_MessageRaised;
    }

    public override async Task InitializeAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        if (!await TryRefreshAsync())
        {
            // Allow the next view attachment to retry a failed initial load.
            _loaded = false;
            return;
        }

        if (SelectedContact is null && !Detail.IsDirty)
        {
            SelectedContact = VisibleContacts.FirstOrDefault();
        }
    }

    partial void OnListSelectionChanged(Contact? value)
    {
        // Null arrives when the list's items are cleared by filtering or reloading; keep showing the current detail.
        if (_syncingListSelection || value is null || ReferenceEquals(value, SelectedContact))
        {
            return;
        }

        _ = SelectFromListAsync(value);
    }

    private async Task SelectFromListAsync(Contact value)
    {
        if (await CanDiscardChangesAsync())
        {
            SelectedContact = value;
        }
        else
        {
            SyncListSelection();
        }
    }

    partial void OnSelectedContactChanged(Contact? value)
    {
        SyncListSelection();

        if (_preserveDetail && value is not null && value.Id == Detail.Id)
        {
            return;
        }

        // A reload that no longer finds the edited contact (e.g. removed outside this editor) must not silently
        // discard unsaved edits; keep them so the user can still save or explicitly discard.
        if (_preserveDetail && value is null && Detail.IsDirty)
        {
            return;
        }

        ReplaceDetail(CreateDetail(value));
    }

    /// <summary>
    /// An editor for <paramref name="contact"/>, offering the local models as they are now, so a model downloaded since
    /// the last editor opened is shown as ready.
    /// </summary>
    private ContactDetailViewModel CreateDetail(Contact? contact)
    {
        return new ContactDetailViewModel(contact, AgentOption.From(_localModels));
    }

    private void ReplaceDetail(ContactDetailViewModel next)
    {
        ContactDetailViewModel previous = Detail;
        previous.MessageRaised -= Detail_MessageRaised;
        next.MessageRaised += Detail_MessageRaised;

        // Switch the binding to the new editor before disposing the old one, so the view never shows a disposed model.
        Detail = next;
        previous.Dispose();
    }

    private void Detail_MessageRaised(object? sender, string message)
    {
        Status = message;
    }

    private void SyncListSelection()
    {
        _syncingListSelection = true;

        try
        {
            ListSelection = SelectedContact is not null && VisibleContacts.Contains(SelectedContact) ? SelectedContact : null;
        }
        finally
        {
            _syncingListSelection = false;
        }
    }

    public async Task<bool> CanDiscardChangesAsync()
    {
        return !Detail.IsDirty || ConfirmDiscardChangesAsync is null || await ConfirmDiscardChangesAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        Filter();
    }

    private void Filter()
    {
        string query = SearchText.Trim();
        VisibleContacts.Clear();

        foreach (Contact contact in Contacts)
        {
            if (query.Length == 0 || new[] { contact.GivenName, contact.FamilyName, contact.Nickname, contact.Personality, contact.Agent?.Model }
                .Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
            {
                VisibleContacts.Add(contact);
            }
        }

        SyncListSelection();
    }

    /// <summary>
    /// Reloads contacts and selects <paramref name="selectId"/>. Unsaved edits to that same contact are kept unless
    /// <paramref name="rebuildDetail"/> is set. Does not change <see cref="BaseViewModel.IsBusy"/>; callers own it.
    /// </summary>
    private async Task ReloadAsync(ContactId? selectId, bool rebuildDetail = false)
    {
        IReadOnlyList<Contact> items = await _contacts.ListAsync();
        Contacts.Clear();

        foreach (Contact contact in items)
        {
            Contacts.Add(contact);
        }

        Filter();
        HasContacts = Contacts.Count > 0;

        _preserveDetail = !rebuildDetail;

        try
        {
            SelectedContact = selectId is null ? null : Contacts.FirstOrDefault(contact => contact.Id == selectId);
        }
        finally
        {
            _preserveDetail = false;
        }

        // A deleted or missing selection leaves an empty editor rather than a stale one.
        if (SelectedContact is null && selectId is not null && Detail.Id == selectId && (rebuildDetail || !Detail.IsDirty))
        {
            ReplaceDetail(CreateDetail(null));
        }
    }

    private string CountStatus()
    {
        return Contacts.Count switch
        {
            0 => "No contacts yet. Add a persona to get started.",
            1 => "1 contact",
            _ => $"{Contacts.Count} contacts"
        };
    }

    [RelayCommand]
    private Task RefreshAsync() => TryRefreshAsync();

    private async Task<bool> TryRefreshAsync()
    {
        try
        {
            IsBusy = true;
            // A clean editor is rebuilt so it shows the reloaded values; unsaved edits are kept.
            await ReloadAsync(SelectedContact?.Id, rebuildDetail: !Detail.IsDirty);
            Status = CountStatus();
            return true;
        }
        catch (Exception error)
        {
            Status = "Could not load contacts: " + error.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task NewContactAsync()
    {
        if (!await CanDiscardChangesAsync())
        {
            return;
        }

        if (SelectedContact is null)
        {
            ReplaceDetail(CreateDetail(null));
        }
        else
        {
            SelectedContact = null;
        }

        Status = "New contact. Save when finished.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            IsBusy = true;

            Contact contact = Detail.ToContact();
            ValidationResult result = await _contacts.SaveAsync(contact);

            if (!result.IsValid)
            {
                LongRunningProcessSuccessful = false;
                Status = result.ToStringPretty("Save");
                return;
            }

            Detail.AcceptChanges();
            await ReloadAsync(contact.Id);
            LongRunningProcessSuccessful = true;
            Status = "Contact saved.";
        }
        catch (Exception error)
        {
            LongRunningProcessSuccessful = false;
            Status = "Could not save: " + error.Message;
        }
        finally
        {
            IsBusy = false;
            LongRunningProcessSuccessful = null;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedContact is null)
        {
            Status = "Select a saved contact to delete.";
            return;
        }

        try
        {
            IsBusy = true;

            Contact deleted = SelectedContact;
            int index = VisibleContacts.IndexOf(deleted);
            Contact? neighbor = index < 0 ? null : VisibleContacts.ElementAtOrDefault(index + 1) ?? VisibleContacts.ElementAtOrDefault(index - 1);

            try
            {
                await _contacts.DeleteAsync(deleted.Id);
            }
            catch (Exception error)
            {
                Status = "Could not delete: " + error.Message;
                return;
            }

            // The deletion is committed: drop it locally first so a failed reload cannot leave its editor open,
            // where saving would recreate the contact.
            Contacts.Remove(deleted);
            VisibleContacts.Remove(deleted);
            HasContacts = Contacts.Count > 0;
            SelectedContact = neighbor;

            if (SelectedContact is null)
            {
                ReplaceDetail(CreateDetail(null));
            }

            try
            {
                await ReloadAsync(neighbor?.Id, rebuildDetail: true);
                Status = "Contact deleted.";
            }
            catch (Exception error)
            {
                Status = "Contact deleted, but the list could not be reloaded: " + error.Message;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}

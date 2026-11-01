using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomiliaMessenger.Presentation.Desktop.Base.Controls.RibbonControls;
using HomiliaMessenger.Presentation.Desktop.Models;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace HomiliaMessenger.Presentation.Desktop.ViewModels;

public partial class MainViewModel : BaseViewModel
{
    private readonly IRibbonControlFactory _ribbonControlFactory;
    private readonly ContactsViewModel _contactsViewModel;
    private readonly ProfileViewModel _profileViewModel;
    private readonly SettingsViewModel _settingsViewModel;

    [ObservableProperty]
    private BaseViewModel _currentContent;

    [ObservableProperty]
    private bool _isPaneOpen;

    [ObservableProperty]
    private string _pageTitle;

    [ObservableProperty]
    private object? _ribbonContent;

    [ObservableProperty]
    private MenuPaneItemTemplate? _selectedPaneItem;

    public ObservableCollection<MenuPaneItemTemplate> PaneItems { get; }

    public MainViewModel(IRibbonControlFactory ribbonControlFactory, MessengerViewModel messengerViewModel, ContactsViewModel contactsViewModel, ProfileViewModel profileViewModel, SettingsViewModel settingsViewModel)
    {
        _ribbonControlFactory = ribbonControlFactory ?? throw new ArgumentNullException(nameof(ribbonControlFactory));
        ArgumentNullException.ThrowIfNull(messengerViewModel);
        _contactsViewModel = contactsViewModel ?? throw new ArgumentNullException(nameof(contactsViewModel));
        _profileViewModel = profileViewModel ?? throw new ArgumentNullException(nameof(profileViewModel));

        _settingsViewModel = settingsViewModel ?? throw new ArgumentNullException(nameof(settingsViewModel));

        List<MenuPaneItemTemplate> paneItemTemplates =
        [
            // Icon key ref: https://pictogrammers.com/library/mdi/

            new MenuPaneItemTemplate(messengerViewModel, "ForumOutline", "Messenger"),
            new MenuPaneItemTemplate(contactsViewModel, "CardAccountMail", "Contacts"),
            new MenuPaneItemTemplate(profileViewModel, "AccountCircleOutline", "Profile")
        ];

        IsPaneOpen = false;
        PaneItems = new ObservableCollection<MenuPaneItemTemplate>(paneItemTemplates);
        SelectedPaneItem = PaneItems[0];
        CurrentContent = SelectedPaneItem.Content;
        PageTitle = SelectedPaneItem.Label;
        RibbonContent = _ribbonControlFactory.Create(SelectedPaneItem.Content);
    }

    /// <summary>
    /// Whether the contact editor or the profile holds edits that would be lost if the app closed now.
    /// </summary>
    public bool HasUnsavedChanges => _contactsViewModel.Detail.IsDirty || _profileViewModel.IsDirty;

    [RelayCommand]
    private void TriggerPane()
    {
        IsPaneOpen = !IsPaneOpen;
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task SettingsAsync()
    {
        await _settingsViewModel.InitializeAsync();
        SelectedPaneItem = null;
        CurrentContent = _settingsViewModel;
        PageTitle = "Settings";
        RibbonContent = _ribbonControlFactory.Create(_settingsViewModel);
    }

    partial void OnSelectedPaneItemChanged(MenuPaneItemTemplate? value)
    {
        if (value is null)
        {
            return;
        }

        CurrentContent = value.Content;
        PageTitle = value.Label;
        RibbonContent = _ribbonControlFactory.Create(value.Content);
    }
}

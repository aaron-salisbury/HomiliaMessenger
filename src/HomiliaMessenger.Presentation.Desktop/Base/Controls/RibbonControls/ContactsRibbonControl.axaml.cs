using Avalonia.Interactivity;
using HomiliaMessenger.Presentation.Desktop.Base.Extensions;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using System;

namespace HomiliaMessenger.Presentation.Desktop.Base.Controls.RibbonControls;

public partial class ContactsRibbonControl : BaseRibbonControl
{
    public ContactsRibbonControl()
    {
        InitializeComponent();
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ContactsViewModel model || model.SelectedContact is not { } contact)
        {
            return;
        }

        // async void handler: any escaping exception would crash the app and lose unsaved edits.
        try
        {
            if (await this.ConfirmAsync("Delete contact", $"Permanently delete {contact.DisplayName}?"))
            {
                await model.DeleteCommand.ExecuteAsync(null);
            }
        }
        catch (Exception error)
        {
            model.Status = "Could not delete: " + error.Message;
        }
    }
}

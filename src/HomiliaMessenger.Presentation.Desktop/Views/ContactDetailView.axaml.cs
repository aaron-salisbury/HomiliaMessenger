using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HomiliaMessenger.Presentation.Desktop.Base.Extensions;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using System;
using System.IO;

namespace HomiliaMessenger.Presentation.Desktop.Views;

public partial class ContactDetailView : UserControl
{
    public ContactDetailView()
    {
        InitializeComponent();
    }

    private async void GeneratePhoto_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ContactDetailViewModel model || TopLevel.GetTopLevel(this) is not Window owner
            || model.CreatePhotoGenerator() is not { } generator)
        {
            return;
        }

        // async void handler: any escaping exception would crash the app and lose unsaved edits.
        try
        {
            using (generator)
            {
                await GeneratePhotoWindow.ShowAsync(owner, generator);
                model.AcceptGeneratedPhoto(generator);
            }
        }
        catch (Exception error)
        {
            model.ReportPhotoError(error);
        }
    }

    private async void ChoosePhoto_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ContactDetailViewModel model)
        {
            return;
        }

        // async void handler: any escaping exception would crash the app and lose unsaved edits.
        try
        {
            IStorageFile? file = await this.GetUserSelectedFileAsync("Choose a JPEG or PNG photo", "Images", "*.png", "*.jpg", "*.jpeg");

            if (file is not null)
            {
                await using Stream stream = await file.OpenReadAsync();
                await model.SetPhotoAsync(stream);
            }
        }
        catch (Exception error)
        {
            model.ReportPhotoError(error);
        }
    }
}

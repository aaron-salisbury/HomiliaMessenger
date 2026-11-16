using Avalonia.Controls;
using Avalonia.Interactivity;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using System;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.Views;

/// <summary>
/// A dialog for generating a contact photo from a description. Closing it any way but "Use photo" leaves the contact's
/// photo as it was, and stops a generation in progress.
/// </summary>
public partial class GeneratePhotoWindow : Window
{
    public GeneratePhotoWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the dialog over <paramref name="owner"/> until the user chooses a photo or closes it.
    /// </summary>
    public static async Task ShowAsync(Window owner, GeneratePhotoViewModel model)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(model);

        GeneratePhotoWindow dialog = new() { DataContext = model };
        void Close(object? sender, EventArgs e)
        {
            dialog.Close();
        }

        model.CloseRequested += Close;

        try
        {
            dialog.Closed += (_, _) => model.CancelGeneration();
            await dialog.ShowDialog(owner);
        }
        finally
        {
            model.CloseRequested -= Close;
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

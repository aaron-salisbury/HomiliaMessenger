using Avalonia.Controls;
using Avalonia.Interactivity;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using System;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.Views;

/// <summary>
/// A dialog for adding a GGUF model from a Hugging Face repository.
/// </summary>
public partial class AddLocalModelWindow : Window
{
    public AddLocalModelWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the dialog over <paramref name="owner"/>.
    /// </summary>
    /// <returns>The added model's name, or <see langword="null"/> when nothing was added.</returns>
    public static async Task<string?> ShowAsync(Window owner, AddLocalModelViewModel model)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(model);

        AddLocalModelWindow dialog = new() { DataContext = model };
        void Close(object? sender, EventArgs e)
        {
            dialog.Close(model.AddedModelId);
        }

        model.Added += Close;

        try
        {
            dialog.Opened += async (_, _) => await model.InitializeAsync();
            return await dialog.ShowDialog<string?>(owner);
        }
        finally
        {
            model.Added -= Close;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}

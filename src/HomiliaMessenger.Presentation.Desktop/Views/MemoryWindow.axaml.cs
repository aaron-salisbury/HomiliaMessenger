using Avalonia.Controls;
using Avalonia.Interactivity;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using System;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.Views;

/// <summary>
/// A dialog for reading and correcting what a contact remembers, kept out of the chat itself. Changes are saved as they
/// are made.
/// </summary>
public partial class MemoryWindow : Window
{
    public MemoryWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the dialog over <paramref name="owner"/> until the user is done.
    /// </summary>
    public static Task ShowAsync(Window owner, MemoryEditorViewModel model)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(model);

        MemoryWindow dialog = new() { DataContext = model };
        dialog.Opened += async (_, _) => await model.LoadAsync();

        return dialog.ShowDialog(owner);
    }

    private void Done_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

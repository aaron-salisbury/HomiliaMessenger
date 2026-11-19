using Avalonia.Controls;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using System;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.Views;

/// <summary>
/// A dialog for checking, and changing, the words a planned selfie is drawn with before it is taken. Closing it any way
/// but taking the selfie drops it.
/// </summary>
public partial class SelfiePromptWindow : Window
{
    public SelfiePromptWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the dialog over <paramref name="owner"/> until the user takes the selfie or closes it.
    /// </summary>
    public static Task ShowAsync(Window owner, SelfiePromptEditorViewModel model)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(model);

        SelfiePromptWindow dialog = new() { DataContext = model };
        model.Closed += (_, _) => dialog.Close();
        dialog.Opened += (_, _) => dialog.PromptBox.Focus();

        return dialog.ShowDialog(owner);
    }
}

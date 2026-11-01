using Avalonia.Controls;
using HomiliaMessenger.Presentation.Desktop.Base;
using HomiliaMessenger.Presentation.Desktop.Base.Extensions;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using System;
using System.Runtime.InteropServices;

namespace HomiliaMessenger.Presentation.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _discardConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        this.Title = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? AppInfo.AppDisplayName : string.Empty;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel || _discardConfirmed || e.CloseReason == WindowCloseReason.OSShutdown ||
            DataContext is not MainViewModel { HasUnsavedChanges: true })
        {
            return;
        }

        // Closing cannot await, so cancel this attempt and close again once the user confirms.
        e.Cancel = true;
        ConfirmCloseAsync();
    }

    private async void ConfirmCloseAsync()
    {
        try
        {
            if (await this.ConfirmAsync("Unsaved changes", "Discard your unsaved changes and exit?"))
            {
                _discardConfirmed = true;
                Close();
            }
        }
        catch (Exception)
        {
            // async void: never let a dialog failure crash the app; the window simply stays open.
        }
    }
}

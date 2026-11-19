using Avalonia.Input;
using Avalonia.Interactivity;
using HomiliaMessenger.Presentation.Desktop.ViewModels;

namespace HomiliaMessenger.Presentation.Desktop.Base.Controls.RibbonControls;

public partial class MessengerRibbonControl : BaseRibbonControl
{
    public MessengerRibbonControl()
    {
        InitializeComponent();

        // Avalonia raises ContextRequested for a right-click, but not for the keyboard's context-menu gestures, so those
        // are caught here, before the button sees them.
        SelfieButton.AddHandler(KeyDownEvent, SelfieButton_KeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Right-click asks for a selfie whose prompt the user checks first.
    /// </summary>
    private void SelfieButton_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        ReviewSelfie();
    }

    /// <summary>
    /// Shift+F10, or the context-menu key, does what right-click does.
    /// </summary>
    private void SelfieButton_KeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift) || e.Key == Key.Apps)
        {
            e.Handled = true;
            ReviewSelfie();
        }
    }

    private void ReviewSelfie()
    {
        if (DataContext is MessengerViewModel model && model.ReviewSelfieCommand.CanExecute(null))
        {
            model.ReviewSelfieCommand.Execute(null);
        }
    }
}

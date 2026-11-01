using Avalonia.Controls;
using HomiliaMessenger.Presentation.Desktop.Base.Extensions;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.Views;

public partial class ContactsView : UserControl
{
    private ContactsViewModel? _attachedModel;

    public ContactsView()
    {
        InitializeComponent();

        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is not ContactsViewModel model)
            {
                return;
            }

            // The view model outlives this view (navigation recreates views), so bind the prompt to the live view
            // on every attachment rather than keeping one that captured a detached view.
            _attachedModel = model;
            model.ConfirmDiscardChangesAsync = ConfirmDiscardChangesAsync;
            await model.InitializeAsync();
        };

        DetachedFromVisualTree += (_, _) =>
        {
            if (_attachedModel is not null && _attachedModel.ConfirmDiscardChangesAsync == ConfirmDiscardChangesAsync)
            {
                _attachedModel.ConfirmDiscardChangesAsync = null;
            }

            _attachedModel = null;
        };
    }

    private Task<bool> ConfirmDiscardChangesAsync()
    {
        return this.ConfirmAsync("Unsaved changes", "Discard your unsaved changes to this contact?");
    }
}

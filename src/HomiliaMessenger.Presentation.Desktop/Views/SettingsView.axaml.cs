using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HomiliaMessenger.Presentation.Desktop.Base.Extensions;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _attachedModel;

    public SettingsView()
    {
        InitializeComponent();

        // The view model outlives this view, so bind the prompt to the live view on every attachment.
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is SettingsViewModel model)
            {
                _attachedModel = model;
                model.ConfirmAsync = ConfirmWithDialogAsync;
                model.ShowAddModelAsync = ShowAddModelAsync;
                model.ShowAddImageModelAsync = ShowAddImageModelAsync;
                model.PickFolderAsync = PickFolderAsync;
            }
        };

        DetachedFromVisualTree += (_, _) =>
        {
            if (_attachedModel is not null && _attachedModel.ConfirmAsync == ConfirmWithDialogAsync)
            {
                _attachedModel.ConfirmAsync = null;
            }

            if (_attachedModel is not null && _attachedModel.ShowAddModelAsync == ShowAddModelAsync)
            {
                _attachedModel.ShowAddModelAsync = null;
            }

            if (_attachedModel is not null && _attachedModel.ShowAddImageModelAsync == ShowAddImageModelAsync)
            {
                _attachedModel.ShowAddImageModelAsync = null;
            }

            if (_attachedModel is not null && _attachedModel.PickFolderAsync == PickFolderAsync)
            {
                _attachedModel.PickFolderAsync = null;
            }

            _attachedModel = null;
        };
    }

    private Task<string?> ShowAddModelAsync(AddLocalModelViewModel model)
    {
        return TopLevel.GetTopLevel(this) is Window owner ? AddLocalModelWindow.ShowAsync(owner, model) : Task.FromResult<string?>(null);
    }

    private Task<string?> ShowAddImageModelAsync(AddImageModelViewModel model)
    {
        return TopLevel.GetTopLevel(this) is Window owner ? AddImageModelWindow.ShowAsync(owner, model) : Task.FromResult<string?>(null);
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        IStorageFolder? folder = await this.GetUserSelectedFolderAsync();
        return folder?.TryGetLocalPath();
    }

    private Task<bool> ConfirmWithDialogAsync(string title, string message)
    {
        return this.ConfirmAsync(title, message);
    }
}

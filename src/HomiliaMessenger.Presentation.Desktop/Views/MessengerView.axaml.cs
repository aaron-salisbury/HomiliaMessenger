using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Presentation.Desktop.Base.Extensions;
using HomiliaMessenger.Presentation.Desktop.Models;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.Views;

public partial class MessengerView : UserControl
{
    private MessengerViewModel? _attachedModel;

    public MessengerView()
    {
        InitializeComponent();

        // Tunnel so Enter is seen before the multi-line text box turns it into a new line.
        Composer.AddHandler(KeyDownEvent, Composer_KeyDown, RoutingStrategies.Tunnel);

        AttachedToVisualTree += async (_, _) =>
        {
            if (DataContext is not MessengerViewModel model)
            {
                return;
            }

            // The view model outlives this view (navigation recreates views), so bind to the live view on every attachment.
            Attach(model);
            await model.InitializeAsync();
            ScrollToEnd();
        };

        DetachedFromVisualTree += (_, _) =>
        {
            Attach(null);
        };
    }

    private void Attach(MessengerViewModel? model)
    {
        if (_attachedModel is not null)
        {
            _attachedModel.Messages.CollectionChanged -= Messages_CollectionChanged;
            _attachedModel.PropertyChanged -= Model_PropertyChanged;

            if (_attachedModel.ConfirmAsync == ConfirmWithDialogAsync)
            {
                _attachedModel.ConfirmAsync = null;
            }
        }

        _attachedModel = model;

        if (model is not null)
        {
            model.ConfirmAsync = ConfirmWithDialogAsync;
            model.Messages.CollectionChanged += Messages_CollectionChanged;
            model.PropertyChanged += Model_PropertyChanged;
        }
    }

    private Task<bool> ConfirmWithDialogAsync(string title, string message)
    {
        return this.ConfirmAsync(title, message);
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScrollToEnd();
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MessengerViewModel.IsContactTyping):
                ScrollToEnd();
                break;
            case nameof(MessengerViewModel.ActiveContact):
                // Ready to type as soon as a chat opens.
                Dispatcher.UIThread.Post(() => Composer.Focus(), DispatcherPriority.Background);
                break;
        }
    }

    private void ScrollToEnd()
    {
        // Wait for layout so the new bubble's height is included.
        Dispatcher.UIThread.Post(() => MessagesScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void Composer_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || _attachedModel is not { } model)
        {
            return;
        }

        e.Handled = true;

        if (model.SendCommand.CanExecute(null))
        {
            model.SendCommand.Execute(null);
        }
    }

    private void ContactPicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox picker || picker.SelectedItem is not Contact contact || _attachedModel is not { } model)
        {
            return;
        }

        picker.SelectedItem = null;
        NewChatButton.Flyout?.Hide();
        model.StartChatCommand.Execute(contact);
    }

    private void DeleteConversation_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ConversationListItem item } && _attachedModel is { } model)
        {
            model.DeleteConversationCommand.Execute(item);
        }
    }
}

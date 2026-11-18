using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HomiliaMessenger.Business.Modules.Contacts;
using HomiliaMessenger.Presentation.Desktop.Base.Extensions;
using HomiliaMessenger.Presentation.Desktop.Models;
using HomiliaMessenger.Presentation.Desktop.ViewModels;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.Views;

public partial class MessengerView : UserControl
{
    // The chat column keeps a portrait shape: never wider than the thread is tall, but never cramped either.
    private const double MIN_CHAT_WIDTH = 480;
    private const double CHAT_ASPECT = 0.85;

    private MessengerViewModel? _attachedModel;

    public MessengerView()
    {
        InitializeComponent();

        // Tunnel so Enter is seen before the multi-line text box turns it into a new line.
        Composer.AddHandler(KeyDownEvent, Composer_KeyDown, RoutingStrategies.Tunnel);
        ThreadGrid.SizeChanged += (_, e) => SizeChatColumn(e.NewSize);

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
            // A group only talks while it is on screen.
            _attachedModel.Suspend();
            _attachedModel.Messages.CollectionChanged -= Messages_CollectionChanged;
            _attachedModel.PropertyChanged -= Model_PropertyChanged;

            if (_attachedModel.ConfirmAsync == ConfirmWithDialogAsync)
            {
                _attachedModel.ConfirmAsync = null;
            }

            if (_attachedModel.ShowMemoryEditorAsync == ShowMemoryEditorAsync)
            {
                _attachedModel.ShowMemoryEditorAsync = null;
            }
        }

        _attachedModel = model;

        if (model is not null)
        {
            model.ConfirmAsync = ConfirmWithDialogAsync;
            model.ShowMemoryEditorAsync = ShowMemoryEditorAsync;
            model.Messages.CollectionChanged += Messages_CollectionChanged;
            model.PropertyChanged += Model_PropertyChanged;
        }
    }

    private Task ShowMemoryEditorAsync(MemoryEditorViewModel editor)
    {
        return TopLevel.GetTopLevel(this) is Window owner ? MemoryWindow.ShowAsync(owner, editor) : Task.CompletedTask;
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
            case nameof(MessengerViewModel.IsSelfieInThread):
                ScrollToEnd();
                break;
            case nameof(MessengerViewModel.ActiveContact):
            case nameof(MessengerViewModel.ActiveGroup):
                // Ready to type as soon as a chat opens.
                Dispatcher.UIThread.Post(() => Composer.Focus(), DispatcherPriority.Background);
                break;
        }
    }

    private void SizeChatColumn(Size thread)
    {
        // Pad the sides rather than centering, so the two sides of the conversation still sit at the column's edges.
        double width = Math.Max(MIN_CHAT_WIDTH, thread.Height * CHAT_ASPECT);
        double side = Math.Max(16, (thread.Width - width) / 2);

        MessagesPanel.Margin = new Thickness(side, 8, side, 12);
        ReplyProblemBorder.Margin = new Thickness(side, 0, side, 6);
        ComposerGrid.Margin = new Thickness(side - 16, 0, side - 16, 0);
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

    private void InvitePicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox picker || picker.SelectedItem is not Contact contact || _attachedModel is not { } model)
        {
            return;
        }

        picker.SelectedItem = null;
        InviteButton.Flyout?.Hide();
        model.InviteContactCommand.Execute(contact);
    }

    private void ContinueWithSender_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: MessageItem { Sender: { } contact } } && _attachedModel is { } model && model.ContinueWithCommand.CanExecute(contact))
        {
            model.ContinueWithCommand.Execute(contact);
        }
    }

    private void ContinueGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (_attachedModel is { } model && model.ContinueGroupCommand.CanExecute(null))
        {
            model.ContinueGroupCommand.Execute(null);
        }
    }

    private void DeleteConversation_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ConversationListItem item } && _attachedModel is { } model)
        {
            model.DeleteConversationCommand.Execute(item);
        }
    }

    /// <summary>
    /// Saves a message's picture, such as a selfie, as a PNG file where the user chooses.
    /// </summary>
    private async void SavePicture_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: MessageItem { ImagePng: { } png } item } || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            return;
        }

        try
        {
            string name = $"{item.SenderName ?? "Selfie"} {item.Message.SentAtUtc.ToLocalTime():yyyy-MM-dd HHmm}.png";
            IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save picture",
                SuggestedFileName = name,
                DefaultExtension = "png",
                FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"], MimeTypes = ["image/png"] }]
            });

            if (file is null)
            {
                return;
            }

            await using Stream stream = await file.OpenWriteAsync();
            await stream.WriteAsync(png);
        }
        catch (Exception error)
        {
            _attachedModel?.ReportProblem("Could not save the picture: " + error.Message);
        }
    }
}

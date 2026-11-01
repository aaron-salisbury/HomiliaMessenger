using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Threading.Tasks;

namespace HomiliaMessenger.Presentation.Desktop.Views;

/// <summary>
/// A dialog for reading and correcting what a contact remembers from earlier in a chat, kept out of the chat itself.
/// </summary>
public partial class MemoryWindow : Window
{
    private readonly int _maxLength;

    public MemoryWindow() : this("this contact", null, 1000)
    {
    }

    public MemoryWindow(string contactName, string? notes, int maxLength)
    {
        InitializeComponent();

        _maxLength = maxLength;
        Title = $"What {contactName} remembers";
        Heading.Text = $"What {contactName} remembers";
        Explanation.Text = $"As your chat grows, {contactName} condenses older messages into these notes so they aren't forgotten. "
            + "Recent messages aren't included; they're remembered as they are. Correct anything that's wrong, or add what "
            + $"you want {contactName} to remember.";
        Notes.MaxLength = maxLength;
        Notes.PlaceholderText = $"Nothing yet. Notes begin once your chat is longer than {contactName} can keep in mind at once.";
        Notes.Text = notes ?? string.Empty;
        Notes.TextChanged += (_, _) => UpdateCount();
        UpdateCount();

        Opened += (_, _) => Notes.Focus();
    }

    /// <summary>
    /// Shows the dialog over <paramref name="owner"/>.
    /// </summary>
    /// <returns>The edited notes, or <see langword="null"/> when the user cancels.</returns>
    public static Task<string?> ShowAsync(Window owner, string contactName, string? notes, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return new MemoryWindow(contactName, notes, maxLength).ShowDialog<string?>(owner);
    }

    private void UpdateCount()
    {
        Count.Text = $"{Notes.Text?.Length ?? 0} / {_maxLength}";
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        Close(Notes.Text ?? string.Empty);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}

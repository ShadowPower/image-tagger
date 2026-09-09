using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ImageTagger.App;
using ImageTagger.App.ViewModels;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Services;
using Xunit;

namespace ImageTagger.Tests.Workflows.A;

public sealed class MainWindowHeadlessTests
{
    [AvaloniaFact]
    public void List_selection_and_keyboard_commands_update_the_same_image_identity()
    {
        var documents = Enumerable.Range(0, 3).Select(Document).ToArray();
        var viewModel = new MainViewModel();
        viewModel.AddImported(new ImageImportResult(documents, [], 0));
        var list = new ListBox { ItemsSource = viewModel.Images };
        list.SelectionChanged += (_, _) =>
            viewModel.SelectedImage = list.SelectedItem as ImageListItemViewModel;
        var window = new Window { Width = 320, Height = 240, Content = list };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        list.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(documents[1].Id, viewModel.CurrentImageId);

        Assert.True(MainWindow.TryHandleSessionKey(viewModel, Key.Down, KeyModifiers.None, false));
        Assert.Equal(documents[2].Id, viewModel.CurrentImageId);

        Assert.True(MainWindow.TryHandleSessionKey(viewModel, Key.Delete, KeyModifiers.None, false));
        Assert.Equal(2, viewModel.Images.Count);
        Assert.Equal(documents[1].Id, viewModel.CurrentImageId);
        window.Close();
    }

    [AvaloniaFact]
    public void Delete_does_not_remove_an_image_while_a_text_editor_has_focus()
    {
        var viewModel = new MainViewModel();
        viewModel.AddImported(new ImageImportResult([Document(0)], [], 0));
        var editor = new TextBox { Text = "editable", CaretIndex = 1 };
        var window = new Window { DataContext = viewModel, Content = editor };
        window.Show();
        editor.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.False(MainWindow.TryHandleSessionKey(viewModel, Key.Delete, KeyModifiers.None, true));

        Assert.Single(viewModel.Images);
        window.Close();
    }

    private static ImageDocument Document(int index) => new()
    {
        Id = $"image-{index}",
        CanonicalPath = Path.GetFullPath($"image-{index}.png"),
        FileName = $"image-{index}.png",
        FileSize = 100,
        Format = "png",
        PixelWidth = 10,
        PixelHeight = 10,
    };
}

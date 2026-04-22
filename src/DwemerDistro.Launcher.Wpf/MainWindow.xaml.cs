using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using DwemerDistro.Launcher.Wpf.ViewModels;
using Brushes = System.Windows.Media.Brushes;

namespace DwemerDistro.Launcher.Wpf;

public partial class MainWindow : Window
{
    private static readonly Regex UrlRegex = new(@"https?://[^\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly MainWindowViewModel _viewModel = new();
    private readonly Paragraph _outputParagraph = new();
    private int _renderedOutputLength;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        OutputRichTextBox.Document = new FlowDocument(_outputParagraph)
        {
            PagePadding = new Thickness(0)
        };
        OutputRichTextBox.Document.TextAlignment = TextAlignment.Left;
        OutputRichTextBox.Document.Foreground = Brushes.White;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        await _viewModel.ShutdownAsync();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.OutputText))
        {
            return;
        }

        Dispatcher.Invoke(() => RenderOutputText(_viewModel.OutputText));
    }

    private void RenderOutputText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _outputParagraph.Inlines.Clear();
            _renderedOutputLength = 0;
            return;
        }

        if (value.Length < _renderedOutputLength)
        {
            _outputParagraph.Inlines.Clear();
            _renderedOutputLength = 0;
        }

        var appendedText = value[_renderedOutputLength..];
        if (appendedText.Length == 0)
        {
            return;
        }

        AppendFormattedText(appendedText);
        _renderedOutputLength = value.Length;
        OutputRichTextBox.ScrollToEnd();
    }

    private void AppendFormattedText(string text)
    {
        var lastIndex = 0;
        foreach (Match match in UrlRegex.Matches(text))
        {
            if (match.Index > lastIndex)
            {
                _outputParagraph.Inlines.Add(new Run(text[lastIndex..match.Index]));
            }

            var url = match.Value.TrimEnd('.', ',', ';', ')', ']', '}');
            var trailing = match.Value[url.Length..];

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var hyperlink = new Hyperlink(new Run(url))
                {
                    NavigateUri = uri,
                    Foreground = Brushes.LightSkyBlue
                };
                hyperlink.Click += Hyperlink_Click;
                _outputParagraph.Inlines.Add(hyperlink);
            }
            else
            {
                _outputParagraph.Inlines.Add(new Run(match.Value));
                lastIndex = match.Index + match.Length;
                continue;
            }

            if (!string.IsNullOrEmpty(trailing))
            {
                _outputParagraph.Inlines.Add(new Run(trailing));
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            _outputParagraph.Inlines.Add(new Run(text[lastIndex..]));
        }
    }

    private static void Hyperlink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink { NavigateUri: not null } hyperlink)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(hyperlink.NavigateUri.AbsoluteUri)
        {
            UseShellExecute = true
        });
    }
}

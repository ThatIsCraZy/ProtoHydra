using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;

namespace DataService.App.Views;

public sealed partial class LicenseWindow : Window
{
    public LicenseWindow()
    {
        InitializeComponent();
        LicenseText.Text = BuildText();
    }

    private static string BuildText()
    {
        var mit = ReadResource("avares://FileHydra/Legal/LICENSE.txt");
        var notices = ReadResource("avares://FileHydra/Legal/THIRD-PARTY-NOTICES.txt");
        return
            "FileHydra — License (MIT)\n" +
            "==============================================================================\n\n" +
            mit +
            "\n\n\n" +
            notices;
    }

    private static string ReadResource(string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"[Could not load {uri}: {ex.Message}]";
        }
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e)
        => Close();
}

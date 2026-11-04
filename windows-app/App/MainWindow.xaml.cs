using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using NmsFrigateFeedOutcomes.Core;

namespace NmsFrigateFeedOutcomes.App;

public partial class MainWindow : Window
{
    private FeedPredictor? _predictor;
    private List<LivingFrigate> _frigates = [];
    private List<FoodOutcome> _outcomes = [];
    private ICollectionView? _outcomesView;

    public MainWindow()
    {
        InitializeComponent();
        StatusText.Text = "Preparing game data from the local installation. First launch may take a few minutes.";
        Loaded += Window_Loaded;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var gameDataRoot = await GameDataBootstrapper.EnsurePreparedAsync();
            _predictor = new FeedPredictor(gameDataRoot);
            OpenSaveButton.IsEnabled = true;
            StatusText.Text = "Open a save file and select a living frigate to compare food outcomes.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not prepare local game data: {exception.Message}";
        }
    }

    private void OpenSave_Click(object sender, RoutedEventArgs e)
    {
        if (_predictor is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Open No Man's Sky save",
            Filter = "No Man's Sky save files (*.hg;*.json)|*.hg;*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var imported = NmsSaveFileReader.ReadLivingFrigates(dialog.FileName);
            _frigates = imported.Frigates.ToList();
            FrigateSelector.ItemsSource = _frigates;
            FrigateSelector.SelectedIndex = _frigates.Count == 0 ? -1 : 0;
            if (_frigates.Count == 0)
            {
                ResourceSeedBox.Clear();
                StatusText.Text = "No living frigates with a valid ResourceSeed were found in this save.";
                return;
            }

            StatusText.Text = $"Loaded {_frigates.Count} living frigate(s). Save data was only read.";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusText.Text = $"Could not read this save: {exception.Message}";
        }
    }

    private void Frigate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FrigateSelector.SelectedItem is not LivingFrigate frigate)
        {
            ResourceSeedBox.Clear();
            CurrentTraitsList.ItemsSource = null;
            CurrentTraitsPlaceholder.Visibility = Visibility.Visible;
            ClearOutcomes();
            return;
        }

        var predictor = _predictor;
        if (predictor is null)
        {
            return;
        }

        ResourceSeedBox.Text = $"0x{frigate.ResourceSeed:X16}";
        var currentTraitDetails = predictor.DescribeTraits(frigate.TraitIds);
        CurrentTraitsList.ItemsSource = currentTraitDetails;
        CurrentTraitsPlaceholder.Visibility = currentTraitDetails.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CurrentTraitsPlaceholder.Text = currentTraitDetails.Count == 0
            ? "No current traits were recorded."
            : "Select a frigate to inspect its traits.";
        CalculateForFrigate(frigate);
    }

    private void CalculateForFrigate(LivingFrigate frigate)
    {
        var predictor = _predictor;
        if (predictor is null)
        {
            return;
        }

        try
        {
            _outcomes = predictor.Calculate(frigate.ResourceSeed, frigate.TraitSlots).ToList();
            _outcomesView = CollectionViewSource.GetDefaultView(_outcomes);
            _outcomesView.Filter = FilterOutcome;
            ResultsGrid.ItemsSource = _outcomesView;
            ResultsGrid.SelectedItem = null;
            ClearSelectedOutcome();
            UpdateCount();
            StatusText.Text = $"Calculated outcomes for {frigate.DisplayName}.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void ClearOutcomes()
    {
        _outcomes = [];
        _outcomesView = null;
        ResultsGrid.ItemsSource = null;
        ClearSelectedOutcome();
        UpdateCount();
    }

    private void ClearSelectedOutcome()
    {
        SelectedTraitsList.ItemsSource = null;
        SelectedOutcomeHeader.Text = "Selected food outcome";
        SelectedTraitsPlaceholder.Visibility = Visibility.Visible;
        RecipeReferenceButton.IsEnabled = false;
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not FoodOutcome outcome)
        {
            ClearSelectedOutcome();
            return;
        }

        if (_predictor is null)
        {
            return;
        }

        SelectedOutcomeHeader.Text = outcome.FoodName;
        RecipeReferenceButton.IsEnabled = true;
        SelectedTraitsList.ItemsSource = _predictor.DescribeTraits(outcome.TraitIds);
        SelectedTraitsPlaceholder.Visibility = outcome.TraitIds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectedTraitsPlaceholder.Text = outcome.TraitIds.Count == 0
            ? "This frigate has no predicted trait slots."
            : "Select a food row to inspect its traits.";
    }

    private void RecipeReference_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not FoodOutcome outcome)
        {
            return;
        }

        var url = $"https://nomansskyrecipes.com/food/{Uri.EscapeDataString(outcome.FoodId)}";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception exception)
        {
            StatusText.Text = $"Could not open recipe reference: {exception.Message}";
        }
    }

    private bool FilterOutcome(object item)
    {
        if (item is not FoodOutcome outcome)
        {
            return false;
        }

        var query = FilterBox.Text.Trim();
        return query.Length == 0
            || outcome.FoodName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || outcome.FoodId.Contains(query, StringComparison.OrdinalIgnoreCase)
            || outcome.NameKey.Contains(query, StringComparison.OrdinalIgnoreCase)
            || outcome.Seed.Contains(query, StringComparison.OrdinalIgnoreCase)
            || outcome.LocalizedTraits.Contains(query, StringComparison.OrdinalIgnoreCase)
            || outcome.Traits.Contains(query, StringComparison.OrdinalIgnoreCase)
            || outcome.BestAt.Contains(query, StringComparison.OrdinalIgnoreCase)
            || outcome.Combat.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
            || outcome.Trading.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
            || outcome.Exploration.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
            || outcome.Mining.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
            || outcome.ExpeditionDuration.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
            || outcome.ExpeditionFuelCost.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void Filter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _outcomesView?.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        ResultsCount.Text = _outcomesView is null
            ? "No results"
            : $"{_outcomesView.Cast<object>().Count():N0} foods shown";
    }
}
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MMODashboard;

public partial class MainWindow
{
    private IServerAdminWorkspace? _adminWorkspace;
    private RuntimeConfigEditor? _runtimeConfigEditor;
    private GatewaySettingsEditor? _gatewaySettingsEditor;
    private GameplayItemEditor? _gameplayItemEditor;

    private readonly ObservableCollection<ExistingItemRow> _visibleItems = new();
    private readonly ObservableCollection<ItemStatModifierDraft> _itemModifiers = new();
    private readonly ObservableCollection<ItemUseEffectDraft> _itemEffects = new();
    private readonly ObservableCollection<ResourceChoice> _resourceChoices = new();
    private readonly ObservableCollection<string> _statusEffectIds = new();
    private readonly ObservableCollection<string> _equipmentSlotIds = new();
    private IReadOnlyList<ExistingItemRow> _allItems = Array.Empty<ExistingItemRow>();
    private string? _editingOriginalItemId;
    private bool _loadingItemForm;

    private async Task InitializeAdminAsync()
    {
        _adminWorkspace?.Dispose();
        _adminWorkspace = new LocalServerAdminWorkspace(_paths);
        AdminWorkspaceText.Text = $"{_adminWorkspace.DisplayName} • remote workspace contract ready";

        ExistingItemsGrid.ItemsSource = _visibleItems;
        StatModifiersGrid.ItemsSource = _itemModifiers;
        UseEffectsGrid.ItemsSource = _itemEffects;
        AllowedSlotsList.ItemsSource = _equipmentSlotIds;

        EffectKindColumn.ItemsSource = new[] { "RestoreResource", "ApplyStatusEffect" };
        EffectKindColumn.SelectedItemBinding = new Binding(nameof(ItemUseEffectDraft.Kind)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
        EffectResourceColumn.ItemsSource = _resourceChoices;
        EffectResourceColumn.DisplayMemberPath = nameof(ResourceChoice.Display);
        EffectResourceColumn.SelectedValuePath = nameof(ResourceChoice.Token);
        EffectResourceColumn.SelectedValueBinding = new Binding(nameof(ItemUseEffectDraft.ResourceToken)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
        EffectStatusColumn.ItemsSource = _statusEffectIds;
        EffectStatusColumn.SelectedItemBinding = new Binding(nameof(ItemUseEffectDraft.StatusEffectId)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };

        await ReloadAdminDocumentsAsync(logSuccess: false);
    }

    private async Task ReloadAdminDocumentsAsync(bool logSuccess = true)
    {
        if (_adminWorkspace is null) return;

        await LoadRuntimeConfigAsync();
        await LoadGatewaySettingsAsync();
        await LoadGameplayContentAsync();

        if (logSuccess)
            Append("Server configuration and gameplay content reloaded.", LogKind.Success);
    }

    private async Task LoadRuntimeConfigAsync()
    {
        if (_adminWorkspace is null) return;
        try
        {
            AdminDocument document = await _adminWorkspace.ReadAsync(AdminDocumentKind.RuntimeConfig);
            _runtimeConfigEditor = RuntimeConfigEditor.Parse(document);
            RuntimeConfigGrid.ItemsSource = _runtimeConfigEditor.Settings;
            RuntimeConfigDocumentText.Text = document.DisplayPath;
        }
        catch (Exception ex)
        {
            _runtimeConfigEditor = null;
            RuntimeConfigGrid.ItemsSource = null;
            RuntimeConfigDocumentText.Text = ex is FileNotFoundException file && !string.IsNullOrWhiteSpace(file.FileName)
                ? $"Missing: {file.FileName}"
                : $"Unavailable: {ex.Message}";
        }
    }

    private async Task LoadGatewaySettingsAsync()
    {
        if (_adminWorkspace is null) return;
        try
        {
            AdminDocument document = await _adminWorkspace.ReadAsync(AdminDocumentKind.GatewaySettings);
            _gatewaySettingsEditor = GatewaySettingsEditor.Parse(document);
            GatewaySettingsGrid.ItemsSource = _gatewaySettingsEditor.Settings;
            GatewaySettingsDocumentText.Text = document.DisplayPath;
        }
        catch (Exception ex)
        {
            _gatewaySettingsEditor = null;
            GatewaySettingsGrid.ItemsSource = null;
            GatewaySettingsDocumentText.Text = ex is FileNotFoundException file && !string.IsNullOrWhiteSpace(file.FileName)
                ? $"Missing: {file.FileName}"
                : $"Unavailable: {ex.Message}";
        }
    }

    private async Task LoadGameplayContentAsync()
    {
        if (_adminWorkspace is null) return;
        try
        {
            AdminDocument document = await _adminWorkspace.ReadAsync(AdminDocumentKind.GameplayContent);
            _gameplayItemEditor = GameplayItemEditor.Parse(document);
            GameplayContentDocumentText.Text = document.DisplayPath;
            RepopulateGameplayReferences();
            RefreshItemList();
            NewItemForm();
            ItemBuilderStatus.Text = $"Loaded {_allItems.Count} item definitions. New items are written to canonical GameplayContent.json and increment content revision automatically.";
        }
        catch (Exception ex)
        {
            _gameplayItemEditor = null;
            _allItems = Array.Empty<ExistingItemRow>();
            _visibleItems.Clear();
            _equipmentSlotIds.Clear();
            _resourceChoices.Clear();
            _statusEffectIds.Clear();
            GameplayContentDocumentText.Text = ex is FileNotFoundException file && !string.IsNullOrWhiteSpace(file.FileName)
                ? $"Missing: {file.FileName}"
                : $"Unavailable: {ex.Message}";
            ItemBuilderStatus.Text = "GameplayContent.json is unavailable. The builder will enable when the dashboard is pointed at a server root containing canonical Content/GameplayContent.json.";
        }
    }

    private void RepopulateGameplayReferences()
    {
        if (_gameplayItemEditor is null) return;
        _equipmentSlotIds.Clear();
        foreach (string slot in _gameplayItemEditor.GetEquipmentSlots()) _equipmentSlotIds.Add(slot);

        _resourceChoices.Clear();
        foreach (ResourceChoice resource in _gameplayItemEditor.GetResourceChoices()) _resourceChoices.Add(resource);

        _statusEffectIds.Clear();
        foreach (string status in _gameplayItemEditor.GetStatusEffectIds()) _statusEffectIds.Add(status);
    }

    private void RefreshItemList()
    {
        _allItems = _gameplayItemEditor?.GetItems() ?? Array.Empty<ExistingItemRow>();
        ApplyItemFilter();
    }

    private void ApplyItemFilter()
    {
        string filter = ItemSearchBox.Text?.Trim() ?? string.Empty;
        _visibleItems.Clear();
        foreach (ExistingItemRow item in _allItems)
        {
            if (filter.Length > 0 &&
                !item.DefinitionId.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !item.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            _visibleItems.Add(item);
        }
    }

    private async void ReloadAdminDocuments_Click(object sender, RoutedEventArgs e)
    {
        await ReloadAdminDocumentsAsync();
    }

    private async void ReloadGameplayContent_Click(object sender, RoutedEventArgs e)
    {
        await LoadGameplayContentAsync();
        Append("Gameplay content reloaded.", LogKind.Info);
    }

    private async void SaveRuntimeConfig_Click(object sender, RoutedEventArgs e)
    {
        if (_adminWorkspace is null || _runtimeConfigEditor is null)
        {
            MessageBox.Show(this, "ServerConfig.bat is not loaded.", "MMO Dashboard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            RuntimeConfigGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            RuntimeConfigGrid.CommitEdit(DataGridEditingUnit.Row, true);
            string content = _runtimeConfigEditor.BuildContent();
            AdminDocument saved = await _adminWorkspace.WriteAsync(
                AdminDocumentKind.RuntimeConfig,
                content,
                _runtimeConfigEditor.Document.Version);
            _runtimeConfigEditor.ReplaceDocument(saved);
            RuntimeConfigDocumentText.Text = saved.DisplayPath;
            Append("Runtime ServerConfig.bat saved. Existing running processes keep their current environment until restarted.", LogKind.Success);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Runtime config save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Append($"Runtime config save failed: {ex.Message}", LogKind.Error);
        }
    }

    private async void SaveGatewaySettings_Click(object sender, RoutedEventArgs e)
    {
        if (_adminWorkspace is null || _gatewaySettingsEditor is null)
        {
            MessageBox.Show(this, "Gateway appsettings.json is not loaded.", "MMO Dashboard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            GatewaySettingsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            GatewaySettingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            string content = _gatewaySettingsEditor.BuildContent();
            AdminDocument saved = await _adminWorkspace.WriteAsync(
                AdminDocumentKind.GatewaySettings,
                content,
                _gatewaySettingsEditor.Document.Version);
            _gatewaySettingsEditor.ReplaceDocument(saved);
            GatewaySettingsDocumentText.Text = saved.DisplayPath;
            bool sourceSettings = saved.DisplayPath.Contains(
                $"{Path.DirectorySeparatorChar}Source{Path.DirectorySeparatorChar}GatewayServer{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase);
            Append(sourceSettings
                    ? "Gateway source appsettings saved. Build Gateway, then restart it for the settings to take effect."
                    : "Gateway appsettings saved. Restart Gateway for startup-only settings to take effect.",
                LogKind.Success);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Gateway settings save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Append($"Gateway settings save failed: {ex.Message}", LogKind.Error);
        }
    }

    private void OpenRuntimeConfig_Click(object sender, RoutedEventArgs e) => OpenAdminDocument(_runtimeConfigEditor?.Document.DisplayPath);
    private void OpenGatewaySettings_Click(object sender, RoutedEventArgs e) => OpenAdminDocument(_gatewaySettingsEditor?.Document.DisplayPath);

    private void OpenAdminDocument(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "The local document is not available.", "MMO Dashboard", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void ItemSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ExistingItemsGrid is null) return;
        ApplyItemFilter();
    }

    private void ExistingItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingItemForm || _gameplayItemEditor is null || ExistingItemsGrid.SelectedItem is not ExistingItemRow row)
            return;
        LoadItemForm(row.DefinitionId);
    }

    private void LoadItemForm(string definitionId)
    {
        if (_gameplayItemEditor is null) return;
        try
        {
            _loadingItemForm = true;
            ItemDraft draft = _gameplayItemEditor.LoadDraft(definitionId);
            _editingOriginalItemId = draft.DefinitionId;
            PopulateItemForm(draft);
            ItemBuilderStatus.Text = $"Editing '{draft.DefinitionId}'. Stable Definition ID cannot be renamed. Structural changes may require a server restart.";
        }
        catch (Exception ex)
        {
            ItemBuilderStatus.Text = ex.Message;
        }
        finally
        {
            _loadingItemForm = false;
        }
    }

    private void PopulateItemForm(ItemDraft draft)
    {
        ItemDefinitionIdBox.Text = draft.DefinitionId;
        ItemDisplayNameBox.Text = draft.DisplayName;
        ItemPresentationIdBox.Text = draft.PresentationId.ToString(CultureInfo.InvariantCulture);
        ItemMaxStackBox.Text = draft.MaxStack.ToString(CultureInfo.InvariantCulture);
        ItemWeightBox.Text = draft.Weight.ToString("0.###", CultureInfo.InvariantCulture);
        ItemMaxDurabilityBox.Text = draft.MaxDurability.ToString(CultureInfo.InvariantCulture);
        ItemTagsBox.Text = string.Join(", ", draft.Tags);
        ItemAmmoFamilyBox.Text = draft.AmmoFamily;
        ItemDamageMinBox.Text = draft.DamageMin.ToString("0.###", CultureInfo.InvariantCulture);
        ItemDamageMaxBox.Text = draft.DamageMax.ToString("0.###", CultureInfo.InvariantCulture);
        ItemAttackRangeBox.Text = draft.BasicAttackRange.ToString("0.###", CultureInfo.InvariantCulture);
        ItemAttackIntervalBox.Text = draft.BasicAttackInterval.ToString("0.###", CultureInfo.InvariantCulture);
        ItemConsumeQuantityBox.Text = draft.ConsumeQuantity.ToString(CultureInfo.InvariantCulture);

        AllowedSlotsList.UnselectAll();
        foreach (string slot in draft.AllowedEquipmentSlots)
            if (_equipmentSlotIds.Contains(slot)) AllowedSlotsList.SelectedItems.Add(slot);

        _itemModifiers.Clear();
        foreach (ItemStatModifierDraft modifier in draft.StatModifiers) _itemModifiers.Add(modifier);
        _itemEffects.Clear();
        foreach (ItemUseEffectDraft effect in draft.UseEffects) _itemEffects.Add(effect);
    }

    private void NewItem_Click(object sender, RoutedEventArgs e) => NewItemForm();

    private void NewItemForm()
    {
        _loadingItemForm = true;
        try
        {
            ExistingItemsGrid.UnselectAll();
            _editingOriginalItemId = null;
            PopulateItemForm(new ItemDraft());
            ItemBuilderStatus.Text = "New item. Definition ID and non-zero Presentation ID must be unique. Save automatically bumps GameplayContent revision.";
        }
        finally
        {
            _loadingItemForm = false;
        }
    }

    private void DuplicateItem_Click(object sender, RoutedEventArgs e)
    {
        if (_gameplayItemEditor is null) return;
        ItemDraft draft;
        if (ExistingItemsGrid.SelectedItem is ExistingItemRow row)
            draft = _gameplayItemEditor.LoadDraft(row.DefinitionId);
        else if (!string.IsNullOrWhiteSpace(_editingOriginalItemId))
            draft = _gameplayItemEditor.LoadDraft(_editingOriginalItemId);
        else
        {
            MessageBox.Show(this, "Select an existing item to duplicate.", "MMO Dashboard", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _editingOriginalItemId = null;
        draft.DefinitionId = string.Empty;
        draft.DisplayName = string.IsNullOrWhiteSpace(draft.DisplayName) ? string.Empty : draft.DisplayName + " Copy";
        draft.PresentationId = 0;
        PopulateItemForm(draft);
        ExistingItemsGrid.UnselectAll();
        ItemBuilderStatus.Text = "Duplicated as a new definition. Assign a new stable Definition ID and Presentation ID if this item has presentation data.";
    }

    private void AddModifier_Click(object sender, RoutedEventArgs e) => _itemModifiers.Add(new ItemStatModifierDraft());
    private void RemoveModifier_Click(object sender, RoutedEventArgs e)
    {
        if (StatModifiersGrid.SelectedItem is ItemStatModifierDraft modifier) _itemModifiers.Remove(modifier);
    }

    private void AddEffect_Click(object sender, RoutedEventArgs e) => _itemEffects.Add(new ItemUseEffectDraft());
    private void RemoveEffect_Click(object sender, RoutedEventArgs e)
    {
        if (UseEffectsGrid.SelectedItem is ItemUseEffectDraft effect) _itemEffects.Remove(effect);
    }

    private async void SaveItem_Click(object sender, RoutedEventArgs e)
    {
        if (_adminWorkspace is null || _gameplayItemEditor is null)
        {
            MessageBox.Show(this, "GameplayContent.json is not loaded.", "MMO Dashboard", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool contentMutated = false;
        try
        {
            StatModifiersGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            StatModifiersGrid.CommitEdit(DataGridEditingUnit.Row, true);
            UseEffectsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            UseEffectsGrid.CommitEdit(DataGridEditingUnit.Row, true);

            ItemDraft draft = ReadItemForm();
            ItemSaveAnalysis analysis = _gameplayItemEditor.AnalyzeAndApply(draft, _editingOriginalItemId);
            contentMutated = true;
            string content = _gameplayItemEditor.BuildContent();
            AdminDocument saved = await _adminWorkspace.WriteAsync(
                AdminDocumentKind.GameplayContent,
                content,
                _gameplayItemEditor.Document.Version);
            _gameplayItemEditor.ReplaceDocument(saved);
            GameplayContentDocumentText.Text = saved.DisplayPath;
            _editingOriginalItemId = draft.DefinitionId;
            RefreshItemList();
            SelectVisibleItem(draft.DefinitionId);

            if (analysis.RequiresRestart)
            {
                ItemBuilderStatus.Text = $"Saved '{draft.DefinitionId}' and bumped revision. RESTART REQUIRED: {analysis.RestartReason}";
                Append($"Item '{draft.DefinitionId}' saved. {analysis.RestartReason}", LogKind.Warning);
                MessageBox.Show(this, analysis.RestartReason, "Item saved — restart required", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                ItemBuilderStatus.Text = $"Saved '{draft.DefinitionId}'. Gameplay content revision increased and the live Gateway can hot-reload this item change.";
                Append($"Item '{draft.DefinitionId}' saved to GameplayContent.json; revision bumped.", LogKind.Success);
            }
        }
        catch (Exception ex)
        {
            ItemBuilderStatus.Text = $"Save failed: {ex.Message}";
            Append($"Item save failed: {ex.Message}", LogKind.Error);
            MessageBox.Show(this, ex.Message, "Item save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            // AnalyzeAndApply mutates the in-memory JSON before the atomic write. Only
            // reload when we reached that point; ordinary form validation errors keep the
            // developer's unsaved edits in place.
            if (contentMutated)
                await LoadGameplayContentAsync();
        }
    }

    private ItemDraft ReadItemForm()
    {
        return new ItemDraft
        {
            DefinitionId = ItemDefinitionIdBox.Text.Trim(),
            DisplayName = ItemDisplayNameBox.Text.Trim(),
            PresentationId = ParseInt(ItemPresentationIdBox.Text, "Presentation ID"),
            MaxStack = ParseInt(ItemMaxStackBox.Text, "Max Stack"),
            Weight = ParseDouble(ItemWeightBox.Text, "Weight"),
            MaxDurability = ParseInt(ItemMaxDurabilityBox.Text, "Max Durability"),
            Tags = SplitList(ItemTagsBox.Text),
            AllowedEquipmentSlots = AllowedSlotsList.SelectedItems.Cast<string>().ToArray(),
            StatModifiers = _itemModifiers.ToArray(),
            DamageMin = ParseDouble(ItemDamageMinBox.Text, "Damage Min"),
            DamageMax = ParseDouble(ItemDamageMaxBox.Text, "Damage Max"),
            AmmoFamily = ItemAmmoFamilyBox.Text.Trim(),
            BasicAttackRange = ParseDouble(ItemAttackRangeBox.Text, "Attack Range"),
            BasicAttackInterval = ParseDouble(ItemAttackIntervalBox.Text, "Attack Interval"),
            ConsumeQuantity = ParseInt(ItemConsumeQuantityBox.Text, "Consume Quantity"),
            UseEffects = _itemEffects.ToArray(),
        };
    }

    private void SelectVisibleItem(string definitionId)
    {
        ExistingItemRow? row = _visibleItems.FirstOrDefault(i => string.Equals(i.DefinitionId, definitionId, StringComparison.Ordinal));
        if (row is null) return;
        ExistingItemsGrid.SelectedItem = row;
        ExistingItemsGrid.ScrollIntoView(row);
    }

    private static int ParseInt(string text, string label)
    {
        if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ||
            int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value))
            return value;
        throw new InvalidOperationException($"{label} must be an integer.");
    }

    private static double ParseDouble(string text, string label)
    {
        if ((double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
             double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)) &&
            double.IsFinite(value))
            return value;
        throw new InvalidOperationException($"{label} must be a finite number.");
    }

    private static string[] SplitList(string text) => (text ?? string.Empty)
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

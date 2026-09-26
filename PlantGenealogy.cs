using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace PlantGenealogy;

public sealed class PlantNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ScientificName { get; set; } = "";
    public string Notes { get; set; } = "";
    public int? ParentId { get; set; }
    public DateTime CreatedAt { get; set; }
    public ObservableCollection<PlantNode> Children { get; } = new();
    public string DisplayName => string.IsNullOrWhiteSpace(ScientificName) ? Name : $"{Name} ({ScientificName})";
}

public sealed class PlantRepository
{
    private readonly string _connectionString;

    public PlantRepository()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlantGenealogy");
        Directory.CreateDirectory(folder);
        _connectionString = $"Data Source={Path.Combine(folder, "plants.db")};Foreign Keys=True";
        Initialize();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS Plants(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    ScientificName TEXT NOT NULL DEFAULT '',
    Notes TEXT NOT NULL DEFAULT '',
    ParentId INTEGER NULL REFERENCES Plants(Id) ON DELETE CASCADE,
    CreatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Plants_ParentId ON Plants(ParentId);";
        command.ExecuteNonQuery();
    }

    public List<PlantNode> GetAll(string search = "")
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT Id, Name, ScientificName, Notes, ParentId, CreatedAt
                                FROM Plants
                                WHERE @Search = '' OR Name LIKE @Pattern OR ScientificName LIKE @Pattern OR Notes LIKE @Pattern
                                ORDER BY Name COLLATE NOCASE;";
        command.Parameters.AddWithValue("@Search", search.Trim());
        command.Parameters.AddWithValue("@Pattern", $"%{search.Trim()}%");

        using var reader = command.ExecuteReader();
        var plants = new List<PlantNode>();
        while (reader.Read())
        {
            plants.Add(new PlantNode
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                ScientificName = reader.GetString(2),
                Notes = reader.GetString(3),
                ParentId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                CreatedAt = DateTime.TryParse(reader.GetString(5), out var date) ? date : DateTime.Now
            });
        }
        return plants;
    }

    public ObservableCollection<PlantNode> GetTree(string search = "")
    {
        var all = GetAll(search);
        var byId = all.ToDictionary(p => p.Id);
        var roots = new ObservableCollection<PlantNode>();

        foreach (var plant in all)
        {
            if (plant.ParentId is int parentId && byId.TryGetValue(parentId, out var parent))
                parent.Children.Add(plant);
            else
                roots.Add(plant);
        }
        return roots;
    }

    public int Add(string name, string scientificName, string notes, int? parentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"INSERT INTO Plants(Name, ScientificName, Notes, ParentId, CreatedAt)
                                VALUES(@Name, @ScientificName, @Notes, @ParentId, @CreatedAt);
                                SELECT last_insert_rowid();";
        AddParameters(command, name, scientificName, notes, parentId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Update(int id, string name, string scientificName, string notes, int? parentId)
    {
        if (parentId == id) throw new InvalidOperationException("لا يمكن جعل النبات أباً لنفسه.");
        if (IsDescendant(id, parentId)) throw new InvalidOperationException("لا يمكن نقل النبات أسفل أحد فروعه.");

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"UPDATE Plants SET Name=@Name, ScientificName=@ScientificName,
                                Notes=@Notes, ParentId=@ParentId WHERE Id=@Id;";
        command.Parameters.AddWithValue("@Id", id);
        AddParameters(command, name, scientificName, notes, parentId);
        command.ExecuteNonQuery();
    }

    private bool IsDescendant(int id, int? possibleDescendant)
    {
        if (possibleDescendant is null) return false;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"WITH RECURSIVE descendants(Id) AS
                                (SELECT Id FROM Plants WHERE ParentId=@Id
                                 UNION ALL SELECT p.Id FROM Plants p JOIN descendants d ON p.ParentId=d.Id)
                                SELECT EXISTS(SELECT 1 FROM descendants WHERE Id=@Candidate);";
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Candidate", possibleDescendant.Value);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public void Delete(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Plants WHERE Id=@Id;";
        command.Parameters.AddWithValue("@Id", id);
        command.ExecuteNonQuery();
    }

    private static void AddParameters(SqliteCommand command, string name, string scientificName, string notes, int? parentId)
    {
        command.Parameters.AddWithValue("@Name", name.Trim());
        command.Parameters.AddWithValue("@ScientificName", scientificName.Trim());
        command.Parameters.AddWithValue("@Notes", notes.Trim());
        command.Parameters.AddWithValue("@ParentId", parentId.HasValue ? parentId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("O"));
    }
}

public sealed class MainWindow : Window
{
    private readonly PlantRepository _repository = new();
    private readonly TreeView _tree = new();
    private readonly TextBox _searchBox = new();
    private readonly TextBox _nameBox = new();
    private readonly TextBox _scientificBox = new();
    private readonly TextBox _notesBox = new();
    private readonly TextBlock _selectedText = new();
    private PlantNode? _selected;
    private bool _editing;

    public MainWindow()
    {
        Title = "شجرة النسب النباتية";
        Width = 1150;
        Height = 720;
        MinWidth = 850;
        MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FlowDirection = FlowDirection.RightToLeft;
        Background = Brushes.WhiteSmoke;
        BuildLayout();
        LoadTree();
    }

    private void BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        Content = root;

        var left = new DockPanel { Margin = new Thickness(0, 0, 14, 0) };
        Grid.SetColumn(left, 0);
        root.Children.Add(left);

        var searchPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(searchPanel, Dock.Top);
        left.Children.Add(searchPanel);
        var searchButton = Button("بحث", 85);
        searchButton.Click += (_, _) => LoadTree();
        DockPanel.SetDock(searchButton, Dock.Left);
        searchPanel.Children.Add(searchButton);
        _searchBox.Height = 34;
        _searchBox.Margin = new Thickness(0, 0, 8, 0);
        _searchBox.VerticalContentAlignment = VerticalAlignment.Center;
        _searchBox.ToolTip = "ابحث بالاسم أو الاسم العلمي أو الملاحظات";
        _searchBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) LoadTree(); };
        searchPanel.Children.Add(_searchBox);

        _tree.BorderBrush = new SolidColorBrush(Color.FromRgb(210, 220, 210));
        _tree.BorderThickness = new Thickness(1);
        _tree.Padding = new Thickness(8);
        _tree.SelectedItemChanged += (_, e) => SelectPlant(e.NewValue as PlantNode);
        left.Children.Add(_tree);

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        Grid.SetColumn(panel, 1);
        root.Children.Add(panel);
        panel.Children.Add(new TextBlock { Text = "بيانات النبات", FontSize = 24, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(35, 105, 65)), Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(Label("النبات المحدد"));
        _selectedText.Text = "لا يوجد نبات محدد";
        _selectedText.Foreground = Brushes.Gray;
        _selectedText.Margin = new Thickness(0, 0, 0, 14);
        panel.Children.Add(_selectedText);
        panel.Children.Add(Label("اسم النبات *"));
        panel.Children.Add(_nameBox);
        panel.Children.Add(Label("الاسم العلمي"));
        panel.Children.Add(_scientificBox);
        panel.Children.Add(Label("ملاحظات"));
        _notesBox.Height = 100;
        _notesBox.TextWrapping = TextWrapping.Wrap;
        _notesBox.AcceptsReturn = true;
        _notesBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        panel.Children.Add(_notesBox);

        var addRoot = Button("إضافة نبات جذري", 42);
        addRoot.Click += (_, _) => Save(false);
        panel.Children.Add(addRoot);
        var addChild = Button("إضافة فرع للنبات المحدد", 42);
        addChild.Click += (_, _) => Save(true);
        panel.Children.Add(addChild);
        var edit = Button("حفظ تعديلات النبات", 42);
        edit.Click += (_, _) => Save(false, true);
        panel.Children.Add(edit);
        var delete = Button("حذف النبات وفروعه", 42);
        delete.Background = new SolidColorBrush(Color.FromRgb(190, 65, 65));
        delete.Foreground = Brushes.White;
        delete.Click += DeleteSelected;
        panel.Children.Add(delete);
        var clear = Button("مسح الحقول", 42);
        clear.Click += (_, _) => ClearForm();
        panel.Children.Add(clear);

        var hint = new TextBlock { Text = "ملاحظة: جميع البيانات تحفظ تلقائياً في SQLite داخل مجلد LocalApplicationData.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 22, 0, 0) };
        panel.Children.Add(hint);
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) };

    private static Button Button(string text, double height) => new() { Content = text, Height = height, Margin = new Thickness(0, 5, 0, 5), Padding = new Thickness(8, 0, 8, 0), FontSize = 14 };

    private void LoadTree()
    {
        _tree.ItemsSource = _repository.GetTree(_searchBox.Text);
        _tree.ItemTemplate = new HierarchicalDataTemplate(typeof(PlantNode))
        {
            ItemsSource = new Binding(nameof(PlantNode.Children)),
            VisualTree = new FrameworkElementFactory(typeof(TextBlock))
        };
        var template = (HierarchicalDataTemplate)_tree.ItemTemplate;
        template.VisualTree!.SetBinding(TextBlock.TextProperty, new Binding(nameof(PlantNode.DisplayName)));
        template.VisualTree.SetValue(TextBlock.FontSizeProperty, 16.0);
        template.VisualTree.SetValue(TextBlock.MarginProperty, new Thickness(3, 6, 3, 6));
        ClearForm();
    }

    private void SelectPlant(PlantNode? plant)
    {
        _selected = plant;
        if (plant is null) { _selectedText.Text = "لا يوجد نبات محدد"; return; }
        _selectedText.Text = plant.DisplayName;
        _nameBox.Text = plant.Name;
        _scientificBox.Text = plant.ScientificName;
        _notesBox.Text = plant.Notes;
    }

    private void Save(bool asChild, bool edit = false)
    {
        if (string.IsNullOrWhiteSpace(_nameBox.Text)) { MessageBox.Show("يرجى إدخال اسم النبات.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        try
        {
            int? parentId = asChild ? _selected?.Id : (edit ? _selected?.ParentId : null);
            if (edit)
            {
                if (_selected is null) { MessageBox.Show("اختر نباتاً لتعديله."); return; }
                _repository.Update(_selected.Id, _nameBox.Text, _scientificBox.Text, _notesBox.Text, parentId);
            }
            else _repository.Add(_nameBox.Text, _scientificBox.Text, _notesBox.Text, parentId);
            LoadTree();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "تعذر الحفظ", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void DeleteSelected(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) { MessageBox.Show("اختر نباتاً أولاً."); return; }
        var answer = MessageBox.Show($"سيتم حذف «{_selected.Name}» وجميع فروعه. هل تريد المتابعة؟", "تأكيد الحذف", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) { _repository.Delete(_selected.Id); LoadTree(); }
    }

    private void ClearForm()
    {
        _selected = null;
        _selectedText.Text = "لا يوجد نبات محدد";
        _nameBox.Clear();
        _scientificBox.Clear();
        _notesBox.Clear();
    }
}

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new MainWindow());
    }
}

using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PlantGenealogy;

public sealed class GenealogyNode
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<GenealogyNode> Children { get; } = new();
    public double X { get; set; }
    public double Y { get; set; }
    public bool IsRoot => ParentId is null;
    public Brush Fill => IsRoot ? new SolidColorBrush(Color.FromRgb(214, 180, 98)) : new SolidColorBrush(Color.FromRgb(244, 214, 120));
}

public sealed class GenealogyRepository
{
    private readonly string _connectionString;

    public GenealogyRepository()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlantGenealogy");
        Directory.CreateDirectory(folder);
        _connectionString = $"Data Source={Path.Combine(folder, "genealogy.db")};Foreign Keys=True";
        Initialize();
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }

    private void Initialize()
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Persons (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Title TEXT NOT NULL DEFAULT '',
    Notes TEXT NOT NULL DEFAULT '',
    ParentId INTEGER NULL REFERENCES Persons(Id) ON DELETE CASCADE,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);";
        cmd.ExecuteNonQuery();
    }

    public List<GenealogyNode> LoadAll()
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Title, Notes, ParentId, CreatedAt FROM Persons ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        var list = new List<GenealogyNode>();
        while (reader.Read())
        {
            list.Add(new GenealogyNode
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Notes = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                ParentId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                CreatedAt = DateTime.TryParse(reader.GetString(5), out var d) ? d : DateTime.Now
            });
        }

        var map = list.ToDictionary(x => x.Id);
        foreach (var item in list)
        {
            if (item.ParentId is int parentId && map.TryGetValue(parentId, out var parent))
            {
                parent.Children.Add(item);
            }
        }
        return list;
    }

    public GenealogyNode? LoadRoot()
    {
        var all = LoadAll();
        return all.FirstOrDefault(x => x.ParentId is null) ?? null;
    }

    public int Add(string name, string title, string notes, int? parentId)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO Persons(Name, Title, Notes, ParentId, CreatedAt) VALUES(@Name,@Title,@Notes,@ParentId,@CreatedAt); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@Name", name.Trim());
        cmd.Parameters.AddWithValue("@Title", title.Trim());
        cmd.Parameters.AddWithValue("@Notes", notes.Trim());
        cmd.Parameters.AddWithValue("@ParentId", parentId.HasValue ? parentId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("O"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Update(int id, string name, string title, string notes)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE Persons SET Name=@Name, Title=@Title, Notes=@Notes WHERE Id=@Id;";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@Name", name.Trim());
        cmd.Parameters.AddWithValue("@Title", title.Trim());
        cmd.Parameters.AddWithValue("@Notes", notes.Trim());
        cmd.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM Persons WHERE Id=@Id;";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    }

    public void SeedIfEmpty()
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Persons;";
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        if (count > 0) return;

        var rootId = Add("حسين", "الجد", "جذر الشجرة", null);
        var child1 = Add("محمد", "الأول", "فرع أول", rootId);
        var child2 = Add("علي", "الثاني", "فرع ثاني", rootId);
        Add("حسن", "ابن محمد", "فرع أبنائه", child1);
        Add("سارة", "ابنة محمد", "فرع بناته", child1);
        Add("رامي", "ابن علي", "فرع أبنائه", child2);
        Add("مها", "ابنة علي", "فرع بناته", child2);
        Add("سلمان", "ابن حسن", "أحفاد", null);
    }
}

public partial class MainWindow : Window
{
    private readonly GenealogyRepository _repository = new();
    private GenealogyNode? _selectedNode;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _repository.SeedIfEmpty();
        DrawTree();
    }

    private void DrawTree()
    {
        TreeCanvas.Children.Clear();
        var root = _repository.LoadRoot();
        if (root is null)
        {
            SelectedNodeText.Text = "لا يوجد";
            return;
        }

        var all = Flatten(root);
        var rootWidth = 2200;
        var rootHeight = 1200;
        TreeCanvas.Width = rootWidth;
        TreeCanvas.Height = rootHeight;

        var minX = 200D;
        var maxX = 2000D;
        var minY = 80D;
        var maxY = 1000D;
        ComputeLayout(root, minX, maxX, minY, maxY, 0);

        var allById = all.ToDictionary(x => x.Id);
        foreach (var node in all)
        {
            if (node.ParentId is int parentId && allById.TryGetValue(parentId, out var parent))
            {
                var line = new Line
                {
                    X1 = parent.X,
                    Y1 = parent.Y + 24,
                    X2 = node.X,
                    Y2 = node.Y - 24,
                    Stroke = Brushes.SaddleBrown,
                    StrokeThickness = 3,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeStartLineCap = PenLineCap.Round
                };
                TreeCanvas.Children.Add(line);
            }
        }

        foreach (var node in all)
        {
            var outer = new Ellipse
            {
                Width = 74,
                Height = 74,
                Fill = node.IsRoot ? Brushes.Goldenrod : Brushes.Gold,
                Stroke = Brushes.DarkGreen,
                StrokeThickness = 2.5,
                Tag = node
            };
            Canvas.SetLeft(outer, node.X - 37);
            Canvas.SetTop(outer, node.Y - 37);
            outer.MouseLeftButtonDown += Node_Click;

            var inner = new Ellipse
            {
                Width = 62,
                Height = 62,
                Fill = node.IsRoot ? Brushes.Goldenrod : new SolidColorBrush(Color.FromRgb(247, 229, 157)),
                Stroke = Brushes.DarkGreen,
                StrokeThickness = 1.2
            };
            Canvas.SetLeft(inner, node.X - 31);
            Canvas.SetTop(inner, node.Y - 31);

            var text = new TextBlock
            {
                Text = Truncate(node.Name, 12),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.DarkGreen,
                TextAlignment = TextAlignment.Center,
                Width = 60,
                Height = 34,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Arial")
            };
            Canvas.SetLeft(text, node.X - 30);
            Canvas.SetTop(text, node.Y - 18);

            TreeCanvas.Children.Add(outer);
            TreeCanvas.Children.Add(inner);
            TreeCanvas.Children.Add(text);
        }
    }

    private void ComputeLayout(GenealogyNode node, double left, double right, double top, double bottom, int depth)
    {
        var x = (left + right) / 2.0;
        var y = top + depth * 160;
        node.X = x;
        node.Y = y;

        if (node.Children.Count == 0)
        {
            return;
        }

        var total = node.Children.Sum(c => c.Children.Count == 0 ? 2 : 3);
        var step = (right - left) / Math.Max(1, total);
        double currentLeft = left;

        foreach (var child in node.Children)
        {
            var childWidth = child.Children.Count == 0 ? 220 : 280 + child.Children.Count * 50;
            var childRight = currentLeft + childWidth;
            ComputeLayout(child, currentLeft, childRight, top + 160, bottom, depth + 1);
            currentLeft = childRight + 40;
        }
    }

    private static List<GenealogyNode> Flatten(GenealogyNode root)
    {
        var list = new List<GenealogyNode>();
        void Walk(GenealogyNode node)
        {
            list.Add(node);
            foreach (var child in node.Children) Walk(child);
        }
        Walk(root);
        return list;
    }

    private void Node_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse ellipse && ellipse.Tag is GenealogyNode node)
        {
            _selectedNode = node;
            NameTextBox.Text = node.Name;
            TitleTextBox.Text = node.Title;
            NotesTextBox.Text = node.Notes;
            SelectedNodeText.Text = node.Name;
        }
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value.Substring(0, maxLength - 1) + "…";

    private void AddRootButton_Click(object sender, RoutedEventArgs e)
    {
        AddPerson(null);
    }

    private void AddChildButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null)
        {
            MessageBox.Show("يرجى تحديد شخص أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AddPerson(_selectedNode.Id);
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null)
        {
            MessageBox.Show("يرجى تحديد الشخص المراد تحديثه.", "تنبيه");
            return;
        }
        _repository.Update(_selectedNode.Id, NameTextBox.Text, TitleTextBox.Text, NotesTextBox.Text);
        DrawTree();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null)
        {
            MessageBox.Show("يرجى تحديد شخص أولاً للحذف.", "تنبيه");
            return;
        }

        var result = MessageBox.Show($"هل تريد حذف {_selectedNode.Name} وجميع فروعه؟", "تأكيد الحذف", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            _repository.Delete(_selectedNode.Id);
            ClearForm();
            DrawTree();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ClearForm();
    }

    private void ClearForm()
    {
        _selectedNode = null;
        NameTextBox.Clear();
        TitleTextBox.Clear();
        NotesTextBox.Clear();
        SelectedNodeText.Text = "لا يوجد";
    }

    private void AddPerson(int? parentId)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            MessageBox.Show("يرجى إدخال اسم الشخص.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _repository.Add(NameTextBox.Text, TitleTextBox.Text, NotesTextBox.Text, parentId);
        ClearForm();
        DrawTree();
    }
}

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
    public int Depth { get; set; }
    public bool IsRoot => ParentId is null;
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
CREATE TABLE IF NOT EXISTS Persons(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    Title TEXT NOT NULL DEFAULT '',
    Notes TEXT NOT NULL DEFAULT '',
    ParentId INTEGER NULL REFERENCES Persons(Id) ON DELETE CASCADE,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS IX_Persons_ParentId ON Persons(ParentId);";
        command.ExecuteNonQuery();
    }

    public List<GenealogyNode> LoadAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Title, Notes, ParentId, CreatedAt FROM Persons ORDER BY Id;";
        using var reader = command.ExecuteReader();
        var nodes = new List<GenealogyNode>();
        while (reader.Read())
        {
            nodes.Add(new GenealogyNode
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Title = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Notes = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                ParentId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                CreatedAt = DateTime.TryParse(reader.GetString(5), out var date) ? date : DateTime.Now
            });
        }

        var byId = nodes.ToDictionary(n => n.Id);
        foreach (var node in nodes)
            if (node.ParentId is int parentId && byId.TryGetValue(parentId, out var parent))
                parent.Children.Add(node);
        return nodes;
    }

    public GenealogyNode? LoadRoot()
    {
        var nodes = LoadAll();
        return nodes.FirstOrDefault(n => n.ParentId is null);
    }

    public int Add(string name, string title, string notes, int? parentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Persons(Name,Title,Notes,ParentId,CreatedAt) VALUES(@Name,@Title,@Notes,@ParentId,@CreatedAt); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("@Name", name.Trim());
        command.Parameters.AddWithValue("@Title", title.Trim());
        command.Parameters.AddWithValue("@Notes", notes.Trim());
        command.Parameters.AddWithValue("@ParentId", parentId.HasValue ? parentId.Value : DBNull.Value);
        command.Parameters.AddWithValue("@CreatedAt", DateTime.Now.ToString("O"));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Update(int id, string name, string title, string notes)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Persons SET Name=@Name, Title=@Title, Notes=@Notes WHERE Id=@Id;";
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Name", name.Trim());
        command.Parameters.AddWithValue("@Title", title.Trim());
        command.Parameters.AddWithValue("@Notes", notes.Trim());
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Persons WHERE Id=@Id;";
        command.Parameters.AddWithValue("@Id", id);
        command.ExecuteNonQuery();
    }

    public void SeedIfEmpty()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Persons;";
        if (Convert.ToInt32(command.ExecuteScalar()) != 0) return;

        var root = Add("حسين", "الجذر", "بداية شجرة النسب", null);
        var left = Add("محمد", "الفرع الأول", "", root);
        var right = Add("علي", "الفرع الثاني", "", root);
        var leftLeft = Add("حسن", "ابن محمد", "", left);
        Add("سارة", "ابنة محمد", "", left);
        Add("رامي", "ابن علي", "", right);
        Add("مها", "ابنة علي", "", right);
        Add("سلمان", "ابن حسن", "", leftLeft);
    }
}

public partial class MainWindow : Window
{
    private const double CanvasWidth = 2600;
    private const double CanvasHeight = 1900;
    private readonly GenealogyRepository _repository = new();
    private GenealogyNode? _selectedNode;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { _repository.SeedIfEmpty(); DrawTree(); };
    }

    private void DrawTree()
    {
        TreeCanvas.Children.Clear();
        TreeCanvas.Width = CanvasWidth;
        TreeCanvas.Height = CanvasHeight;
        DrawBackground();

        var root = _repository.LoadRoot();
        if (root is null) return;

        var nodes = Flatten(root);
        var leaves = CountLeaves(root);
        Layout(root, 150, CanvasWidth - 150, 0, Math.Max(1, leaves));

        foreach (var node in nodes)
            foreach (var child in node.Children)
                DrawBranch(node, child);

        foreach (var node in nodes)
        {
            DrawDecorativeLeaves(node);
            DrawNode(node);
        }

        DrawGround();
    }

    private void DrawBackground()
    {
        TreeCanvas.Children.Add(new Rectangle { Width = CanvasWidth, Height = CanvasHeight, Fill = new SolidColorBrush(Color.FromRgb(249, 246, 237)) });
        for (var x = 40; x < CanvasWidth; x += 110)
            TreeCanvas.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x + 45, Y2 = CanvasHeight, Stroke = new SolidColorBrush(Color.FromArgb(20, 173, 145, 95)), StrokeThickness = 1 });
        var title = new TextBlock { Text = "شَجَرَةُ النَّسَب", FontSize = 45, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(82, 53, 24)), Width = CanvasWidth, TextAlignment = TextAlignment.Center, FontFamily = new FontFamily("Traditional Arabic") };
        Canvas.SetTop(title, 30);
        TreeCanvas.Children.Add(title);
        var subtitle = new TextBlock { Text = "سجل النسب التفاعلي", FontSize = 22, Foreground = new SolidColorBrush(Color.FromRgb(31, 106, 69)), Width = CanvasWidth, TextAlignment = TextAlignment.Center };
        Canvas.SetTop(subtitle, 88);
        TreeCanvas.Children.Add(subtitle);
    }

    private void DrawGround()
    {
        var ground = new Path { Stroke = new SolidColorBrush(Color.FromRgb(48, 122, 67)), StrokeThickness = 11, Opacity = .8, Data = Geometry.Parse($"M 80,1780 C 600,1740 900,1810 1300,1770 C 1750,1730 2120,1800 2520,1760") };
        TreeCanvas.Children.Add(ground);
        for (var i = 0; i < 24; i++)
        {
            var blade = new Line { X1 = 120 + i * 100, Y1 = 1782, X2 = 105 + i * 100, Y2 = 1740 - (i % 3) * 12, Stroke = new SolidColorBrush(Color.FromRgb(40, 125, 65)), StrokeThickness = 4 };
            TreeCanvas.Children.Add(blade);
        }
    }

    private void Layout(GenealogyNode node, double left, double right, int depth, int leafCount)
    {
        node.Depth = depth;
        node.Y = CanvasHeight - 190 - depth * 205;
        if (node.Children.Count == 0)
        {
            node.X = (left + right) / 2;
            return;
        }

        var total = node.Children.Sum(CountLeaves);
        var current = left;
        foreach (var child in node.Children)
        {
            var width = (right - left) * CountLeaves(child) / total;
            Layout(child, current, current + width, depth + 1, CountLeaves(child));
            current += width;
        }
        node.X = node.Children.Average(c => c.X);
    }

    private void DrawBranch(GenealogyNode parent, GenealogyNode child)
    {
        var bendY = (parent.Y + child.Y) / 2;
        var path = new Path
        {
            Stroke = new SolidColorBrush(Color.FromRgb(91, 55, 26)),
            StrokeThickness = parent.IsRoot ? 24 : Math.Max(7, 18 - child.Depth * 2),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = Geometry.Parse($"M {parent.X},{parent.Y} C {parent.X},{bendY} {child.X},{bendY} {child.X},{child.Y}")
        };
        TreeCanvas.Children.Add(path);

        var twig = new Line { X1 = child.X, Y1 = child.Y + 35, X2 = child.X + (child.X >= parent.X ? 42 : -42), Y2 = child.Y + 3, Stroke = new SolidColorBrush(Color.FromRgb(105, 67, 33)), StrokeThickness = 5 };
        TreeCanvas.Children.Add(twig);
    }

    private void DrawDecorativeLeaves(GenealogyNode node)
    {
        var count = Math.Min(8, 2 + node.Children.Count * 2);
        for (var i = 0; i < count; i++)
        {
            var angle = -145 + i * (290.0 / Math.Max(1, count - 1));
            var radians = angle * Math.PI / 180;
            var distance = 48 + (i % 3) * 12;
            var leaf = new Ellipse { Width = 34, Height = 17, Fill = new SolidColorBrush(Color.FromRgb(42, 126, 72)), Stroke = new SolidColorBrush(Color.FromRgb(24, 93, 49)), StrokeThickness = 1 };
            Canvas.SetLeft(leaf, node.X + Math.Cos(radians) * distance - 17);
            Canvas.SetTop(leaf, node.Y + Math.Sin(radians) * distance - 8);
            leaf.RenderTransform = new RotateTransform(angle, 17, 8);
            TreeCanvas.Children.Add(leaf);
        }
    }

    private void DrawNode(GenealogyNode node)
    {
        var border = new Border { Width = node.IsRoot ? 142 : 112, Height = node.IsRoot ? 142 : 112, CornerRadius = new CornerRadius(70), Background = node.IsRoot ? new SolidColorBrush(Color.FromRgb(218, 173, 66)) : new SolidColorBrush(Color.FromRgb(249, 224, 121)), BorderBrush = new SolidColorBrush(Color.FromRgb(75, 112, 45)), BorderThickness = new Thickness(4), Tag = node, Cursor = Cursors.Hand };
        var text = new TextBlock { Text = node.Name, FontSize = node.IsRoot ? 23 : 18, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(54, 74, 31)), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Arial") };
        border.Child = text;
        border.MouseLeftButtonDown += Node_Click;
        Canvas.SetLeft(border, node.X - border.Width / 2);
        Canvas.SetTop(border, node.Y - border.Height / 2);
        TreeCanvas.Children.Add(border);
    }

    private static int CountLeaves(GenealogyNode node) => node.Children.Count == 0 ? 1 : node.Children.Sum(CountLeaves);

    private static List<GenealogyNode> Flatten(GenealogyNode root)
    {
        var list = new List<GenealogyNode>();
        void Visit(GenealogyNode node) { list.Add(node); foreach (var child in node.Children) Visit(child); }
        Visit(root);
        return list;
    }

    private void Node_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is GenealogyNode node)
        {
            _selectedNode = node;
            NameTextBox.Text = node.Name;
            TitleTextBox.Text = node.Title;
            NotesTextBox.Text = node.Notes;
            SelectedNodeText.Text = node.Name;
            e.Handled = true;
        }
    }

    private void AddRootButton_Click(object sender, RoutedEventArgs e) => AddPerson(null);

    private void AddChildButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null) { MessageBox.Show("حدد عقدة أولاً.", "تنبيه"); return; }
        AddPerson(_selectedNode.Id);
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null) { MessageBox.Show("حدد عقدة لتحديثها.", "تنبيه"); return; }
        if (string.IsNullOrWhiteSpace(NameTextBox.Text)) { MessageBox.Show("أدخل الاسم.", "تنبيه"); return; }
        _repository.Update(_selectedNode.Id, NameTextBox.Text, TitleTextBox.Text, NotesTextBox.Text);
        DrawTree();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null) { MessageBox.Show("حدد عقدة لحذفها.", "تنبيه"); return; }
        if (MessageBox.Show($"حذف «{_selectedNode.Name}» وجميع فروعه؟", "تأكيد", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            _repository.Delete(_selectedNode.Id);
            ClearForm();
            DrawTree();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => ClearForm();

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
        if (string.IsNullOrWhiteSpace(NameTextBox.Text)) { MessageBox.Show("أدخل الاسم أولاً.", "تنبيه"); return; }
        _repository.Add(NameTextBox.Text, TitleTextBox.Text, NotesTextBox.Text, parentId);
        ClearForm();
        DrawTree();
    }
}

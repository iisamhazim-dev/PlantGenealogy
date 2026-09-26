using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace PlantGenealogy;

public sealed class GenealogyNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public int? ParentId { get; set; }
    public DateTime CreatedAt { get; set; }
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

    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }

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
        command.CommandText = "SELECT Id,Name,Title,Notes,ParentId,CreatedAt FROM Persons ORDER BY Id;";
        using var reader = command.ExecuteReader();
        var nodes = new List<GenealogyNode>();
        while (reader.Read())
        {
            nodes.Add(new GenealogyNode
            {
                Id = reader.GetInt32(0), Name = reader.GetString(1),
                Title = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Notes = reader.IsDBNull(3) ? "" : reader.GetString(3),
                ParentId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                CreatedAt = DateTime.TryParse(reader.GetString(5), out var date) ? date : DateTime.Now
            });
        }
        var byId = nodes.ToDictionary(n => n.Id);
        foreach (var node in nodes)
            if (node.ParentId is int parentId && byId.TryGetValue(parentId, out var parent)) parent.Children.Add(node);
        return nodes;
    }

    public GenealogyNode? LoadRoot() => LoadAll().FirstOrDefault(n => n.ParentId is null);

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
        command.CommandText = "UPDATE Persons SET Name=@Name,Title=@Title,Notes=@Notes WHERE Id=@Id;";
        command.Parameters.AddWithValue("@Id", id); command.Parameters.AddWithValue("@Name", name.Trim());
        command.Parameters.AddWithValue("@Title", title.Trim()); command.Parameters.AddWithValue("@Notes", notes.Trim());
        command.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Persons WHERE Id=@Id;"; command.Parameters.AddWithValue("@Id", id); command.ExecuteNonQuery();
    }

    public void SeedIfEmpty()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Persons;";
        if (Convert.ToInt32(command.ExecuteScalar()) != 0) return;
        var root = Add("شجرة العائلة", "الجذر", "يمكن تغيير هذا الاسم من لوحة البيانات", null);
        var a = Add("الفرع الأول", "فرع رئيسي", "", root);
        var b = Add("الفرع الثاني", "فرع رئيسي", "", root);
        var aa = Add("العقدة الأولى", "جيل جديد", "", a);
        Add("العقدة الثانية", "جيل جديد", "", a); Add("العقدة الثالثة", "جيل جديد", "", b);
        Add("العقدة الرابعة", "جيل جديد", "", b); Add("فرع إضافي", "جيل تالٍ", "", aa);
    }
}

public partial class MainWindow : Window
{
    private const double W = 3000, H = 2100;
    private readonly GenealogyRepository _repository = new();
    private GenealogyNode? _selectedNode;
    private readonly Random _random = new(41);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { _repository.SeedIfEmpty(); DrawTree(); };
    }

    private void DrawTree()
    {
        TreeCanvas.Children.Clear(); TreeCanvas.Width = W; TreeCanvas.Height = H; DrawBackground();
        var root = _repository.LoadRoot(); if (root is null) return;
        var leaves = CountLeaves(root); Layout(root, 180, W - 180, 0, leaves);
        var nodes = Flatten(root);
        foreach (var node in nodes) foreach (var child in node.Children) DrawBranch(node, child);
        foreach (var node in nodes) { DrawLeafCluster(node); DrawNode(node); }
        DrawGround();
    }

    private void DrawBackground()
    {
        var background = new Rectangle { Width = W, Height = H, Fill = new LinearGradientBrush(Color.FromRgb(255, 253, 246), Color.FromRgb(235, 242, 222), 90) };
        TreeCanvas.Children.Add(background);
        var glow = new Ellipse { Width = 1200, Height = 620, Fill = new RadialGradientBrush(Color.FromArgb(70, 255, 224, 136), Colors.Transparent), IsHitTestVisible = false };
        Canvas.SetLeft(glow, 900); Canvas.SetTop(glow, 70); TreeCanvas.Children.Add(glow);
        var title = new TextBlock { Text = "شَجَرَةُ النَّسَب", FontSize = 55, FontWeight = FontWeights.Bold, Foreground = Brush("#68431E"), Width = W, TextAlignment = TextAlignment.Center, FontFamily = new FontFamily("Traditional Arabic") };
        Canvas.SetTop(title, 28); TreeCanvas.Children.Add(title);
        var subtitle = new TextBlock { Text = "سجل تفاعلي حيّ — اضغط على أي عقدة لعرض تفاصيلها", FontSize = 24, Foreground = Brush("#2D8151"), Width = W, TextAlignment = TextAlignment.Center };
        Canvas.SetTop(subtitle, 100); TreeCanvas.Children.Add(subtitle);
        for (var i = 0; i < 13; i++)
        {
            var ornament = new Ellipse { Width = 8, Height = 8, Fill = Brush("#D4A94F"), Opacity = .8 };
            Canvas.SetLeft(ornament, 480 + i * 170); Canvas.SetTop(150); TreeCanvas.Children.Add(ornament);
        }
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private void Layout(GenealogyNode node, double left, double right, int depth, int leaves)
    {
        node.Depth = depth; node.Y = H - 240 - depth * 220;
        if (node.Children.Count == 0) { node.X = (left + right) / 2; return; }
        var total = node.Children.Sum(CountLeaves); var cursor = left;
        foreach (var child in node.Children)
        {
            var width = (right - left) * CountLeaves(child) / total;
            Layout(child, cursor, cursor + width, depth + 1, CountLeaves(child)); cursor += width;
        }
        node.X = node.Children.Average(c => c.X);
    }

    private void DrawBranch(GenealogyNode parent, GenealogyNode child)
    {
        var bend = (parent.Y + child.Y) / 2;
        var thickness = Math.Max(5, parent.IsRoot ? 34 - child.Depth * 3 : 19 - child.Depth * 2);
        var shadow = new Path { Stroke = new SolidColorBrush(Color.FromArgb(45, 35, 20, 10)), StrokeThickness = thickness + 10, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Data = Geometry.Parse($"M {parent.X},{parent.Y} C {parent.X},{bend} {child.X},{bend} {child.X},{child.Y}") };
        TreeCanvas.Children.Add(shadow);
        var branch = new Path { Stroke = new LinearGradientBrush(Brush("#9A6331").Color, Brush("#402414").Color, 90), StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Data = shadow.Data };
        TreeCanvas.Children.Add(branch);
        DrawBarkHighlight(parent, child, bend, thickness);
    }

    private void DrawBarkHighlight(GenealogyNode parent, GenealogyNode child, double bend, double thickness)
    {
        var highlight = new Path { Stroke = new SolidColorBrush(Color.FromArgb(95, 221, 170, 98)), StrokeThickness = Math.Max(2, thickness / 6), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Data = Geometry.Parse($"M {parent.X - thickness / 4},{parent.Y} C {parent.X - thickness / 4},{bend} {child.X - thickness / 4},{bend} {child.X - thickness / 4},{child.Y}") };
        TreeCanvas.Children.Add(highlight);
    }

    private void DrawLeafCluster(GenealogyNode node)
    {
        var count = Math.Min(10, 3 + node.Children.Count * 2);
        for (var i = 0; i < count; i++)
        {
            var angle = -165 + i * (330.0 / Math.Max(1, count - 1)); var rad = angle * Math.PI / 180;
            var distance = 62 + (i % 3) * 18; var x = node.X + Math.Cos(rad) * distance; var y = node.Y + Math.Sin(rad) * distance;
            var leaf = new Path { Fill = new LinearGradientBrush(Brush("#71B95B").Color, Brush("#17663D").Color, 90), Stroke = Brush("#1E5A37"), StrokeThickness = 1.2, Data = LeafGeometry(18, 9), Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 2, Opacity = .22 } };
            Canvas.SetLeft(leaf, x - 18); Canvas.SetTop(leaf, y - 9); leaf.RenderTransform = new RotateTransform(angle, 18, 9); TreeCanvas.Children.Add(leaf);
            var vein = new Line { X1 = x - 13, Y1 = y, X2 = x + 13, Y2 = y, Stroke = new SolidColorBrush(Color.FromArgb(125, 226, 244, 194)), StrokeThickness = 1 };
            vein.RenderTransform = new RotateTransform(angle, x, y); TreeCanvas.Children.Add(vein);
        }
    }

    private static StreamGeometry LeafGeometry(double width, double height)
    {
        var g = new StreamGeometry(); using var c = g.Open();
        c.BeginFigure(new Point(0, height), true, true); c.BezierTo(new Point(width * .35, -height), new Point(width * .82, -height), new Point(width, height), true, true); c.BezierTo(new Point(width * .55, height * 1.65), new Point(width * .18, height * 1.35), new Point(0, height), true, true); return g;
    }

    private void DrawNode(GenealogyNode node)
    {
        var size = node.IsRoot ? 178 : 124;
        var ring = new Ellipse { Width = size + 18, Height = size + 18, Fill = new RadialGradientBrush(Color.FromRgb(255, 248, 195), Color.FromRgb(199, 145, 43)), Stroke = Brush("#80601E"), StrokeThickness = 4, Tag = node, Cursor = Cursors.Hand, Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 5, Opacity = .32 } };
        Canvas.SetLeft(ring, node.X - ring.Width / 2); Canvas.SetTop(ring, node.Y - ring.Height / 2); ring.MouseLeftButtonDown += Node_Click; TreeCanvas.Children.Add(ring);
        var text = new TextBlock { Text = node.Name, Width = size - 16, Height = size - 25, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = node.IsRoot ? 27 : 20, FontWeight = FontWeights.Bold, Foreground = Brush("#4E391C"), FontFamily = new FontFamily("Arial"), IsHitTestVisible = false };
        Canvas.SetLeft(text, node.X - text.Width / 2); Canvas.SetTop(text, node.Y - text.Height / 2 + 5); TreeCanvas.Children.Add(text);
    }

    private void DrawGround()
    {
        var grass = new Rectangle { Width = W, Height = 110, Fill = new LinearGradientBrush(Brush("#8ACB65").Color, Brush("#2E7A3D").Color, 90) };
        Canvas.SetTop(grass, H - 105); TreeCanvas.Children.Add(grass);
        for (var i = 0; i < 50; i++)
        {
            var x = i * 62; var blade = new Line { X1 = x, Y1 = H - 92, X2 = x + _random.Next(-18, 19), Y2 = H - 120 - _random.Next(0, 26), Stroke = Brush("#236833"), StrokeThickness = 4 };
            TreeCanvas.Children.Add(blade);
        }
    }

    private static int CountLeaves(GenealogyNode node) => node.Children.Count == 0 ? 1 : node.Children.Sum(CountLeaves);
    private static List<GenealogyNode> Flatten(GenealogyNode root) { var all = new List<GenealogyNode>(); void Walk(GenealogyNode n) { all.Add(n); foreach (var c in n.Children) Walk(c); } Walk(root); return all; }

    private void Node_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse ellipse && ellipse.Tag is GenealogyNode node)
        {
            _selectedNode = node; NameTextBox.Text = node.Name; TitleTextBox.Text = node.Title; NotesTextBox.Text = node.Notes; SelectedNodeText.Text = node.Name; e.Handled = true;
        }
    }

    private void AddRootButton_Click(object sender, RoutedEventArgs e) => AddPerson(null);
    private void AddChildButton_Click(object sender, RoutedEventArgs e) { if (_selectedNode is null) { MessageBox.Show("حدد عقدة أولاً.", "تنبيه"); return; } AddPerson(_selectedNode.Id); }
    private void UpdateButton_Click(object sender, RoutedEventArgs e) { if (_selectedNode is null) { MessageBox.Show("حدد عقدة لتحديثها.", "تنبيه"); return; } if (string.IsNullOrWhiteSpace(NameTextBox.Text)) return; _repository.Update(_selectedNode.Id, NameTextBox.Text, TitleTextBox.Text, NotesTextBox.Text); DrawTree(); }
    private void DeleteButton_Click(object sender, RoutedEventArgs e) { if (_selectedNode is null) { MessageBox.Show("حدد عقدة لحذفها.", "تنبيه"); return; } if (MessageBox.Show($"حذف «{_selectedNode.Name}» وجميع فروعه؟", "تأكيد", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { _repository.Delete(_selectedNode.Id); ClearForm(); DrawTree(); } }
    private void ClearButton_Click(object sender, RoutedEventArgs e) => ClearForm();
    private void ClearForm() { _selectedNode = null; NameTextBox.Clear(); TitleTextBox.Clear(); NotesTextBox.Clear(); SelectedNodeText.Text = "لا يوجد"; }
    private void AddPerson(int? parentId) { if (string.IsNullOrWhiteSpace(NameTextBox.Text)) { MessageBox.Show("أدخل الاسم أولاً.", "تنبيه"); return; } _repository.Add(NameTextBox.Text, TitleTextBox.Text, NotesTextBox.Text, parentId); ClearForm(); DrawTree(); }
}

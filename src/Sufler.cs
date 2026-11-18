using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Serialization;
using Microsoft.Win32;
using System.Threading;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("Суфлёр")]
[assembly: AssemblyProduct("Суфлёр")]
[assembly: AssemblyVersion("1.1.0.0")]

public sealed class Session
{
    public string Text = "Привет! Это ваш суфлёр.\n\nВставьте текст выступления в редактор слева. Настройте размер шрифта и скорость, затем нажмите «Начать».\n\nЧитайте спокойно, в своём темпе. Пробел ставит прокрутку на паузу и продолжает её.\n\nДля чтения через стекло включите «Зеркало». А если нужно сосредоточиться на тексте — нажмите F11.\n\nВсё готово. Можно начинать!";
    public double Font = 48, Speed = 40;
    public int Theme;
    public string FontName = "Segoe UI";
    public bool Mirror, Countdown = true, Guide = true, OnTop;
    public bool CloseToTray = true;
    public static Session Load(string path)
    {
        if (!File.Exists(path)) return new Session();
        using (var stream = File.OpenRead(path))
        {
            var s = (Session)new XmlSerializer(typeof(Session)).Deserialize(stream);
            s.Font = Valid(s.Font, 5, 100, 48); s.Speed = Valid(s.Speed, 5, 160, 40);
            if (s.Theme < 0 || s.Theme > 3) s.Theme = 0;
            s.Text = s.Text ?? ""; return s;
        }
    }
    static double Valid(double n, double min, double max, double fallback) { return Double.IsNaN(n) || Double.IsInfinity(n) || n < min || n > max ? fallback : n; }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
        { new XmlSerializer(typeof(Session)).Serialize(stream, this); stream.Flush(true); }
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
    }
}

internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static string CommandFor(string executable) { return "\"" + executable + "\" --background"; }
    internal static bool Enabled
    {
        get { using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue("Sufler") != null; }
    }
    internal static void SetEnabled(bool enabled)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled) key.SetValue("Sufler", CommandFor(Assembly.GetExecutingAssembly().Location));
            else key.DeleteValue("Sufler", false);
        }
    }
}

internal sealed class TrayController : IDisposable
{
    readonly Forms.NotifyIcon icon;
    readonly Forms.ContextMenuStrip menu;
    readonly System.Drawing.Icon image;
    internal bool Visible { get { return icon.Visible; } }
    internal TrayController(Prompter prompt)
    {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Sufler.ico"))
        using (var original = new System.Drawing.Icon(stream)) image = (System.Drawing.Icon)original.Clone();
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть суфлёр", null, delegate { prompt.ShowWindow(); });
        menu.Items.Add("Начать / пауза", null, delegate { prompt.ShowWindow(); prompt.Toggle(); });
        menu.Items.Add("Настройки", null, delegate { prompt.ShowWindow(); prompt.ShowSettings(); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выйти", null, delegate { prompt.Exit(); });
        icon = new Forms.NotifyIcon { Icon = image, Text = "Суфлёр · работает", ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) prompt.ShowWindow(); };
    }
    public void Dispose() { icon.Visible = false; icon.Dispose(); menu.Dispose(); image.Dispose(); }
}

internal sealed class Prompter
{
    internal readonly Window Window;
    internal readonly TextBox Editor;
    internal readonly TextBlock Script, Status;
    internal readonly ScrollViewer Reader;
    internal readonly Slider FontSlider, SpeedSlider;
    internal readonly ComboBox Theme;
    internal readonly ComboBox FontChoice;
    static readonly string[] FontNames = { "Segoe UI", "Inter", "Arial", "Georgia", "Verdana" };
    readonly FontFamily interFamily;
    readonly CheckBox mirror, onTop, countdown, guide;
    readonly Button play;
    readonly string statePath;
    readonly bool testMode;
    readonly DispatcherTimer autosave;
    readonly Stopwatch watch = new Stopwatch();
    TrayController tray;
    Window settingsWindow;
    bool closeToTray, exitRequested;
    bool running, counting, dirty, full, editorVisible = true, ready, renderAttached;
    double countElapsed, previousFrame;
    WindowState oldState;
    internal bool Running { get { return running; } }
    T Get<T>(string name) where T : class { return Window.FindName(name) as T; }

    internal Prompter(string path, bool testing)
    {
        statePath = path; testMode = testing;
        string fontDir = Path.Combine(Path.GetDirectoryName(path), "Fonts");
        Directory.CreateDirectory(fontDir);
        foreach (string fontFile in new [] { "Inter-Regular.ttf", "Inter-SemiBold.ttf", "Inter-LICENSE.txt" })
        {
            string destination = Path.Combine(fontDir, fontFile);
            if (!File.Exists(destination)) using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(fontFile))
            using (var target = File.Create(destination)) source.CopyTo(target);
        }
        interFamily = new FontFamily(new Uri(fontDir + Path.DirectorySeparatorChar, UriKind.Absolute), "./#Inter");
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Main.xaml")) Window = (Window)XamlReader.Load(stream);
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Sufler.ico")) Window.Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Editor = Get<TextBox>("Editor"); Script = Get<TextBlock>("Script"); Status = Get<TextBlock>("Status"); Reader = Get<ScrollViewer>("Reader");
        FontSlider = Get<Slider>("FontSlider"); SpeedSlider = Get<Slider>("SpeedSlider"); Theme = Get<ComboBox>("Theme");
        FontChoice = Get<ComboBox>("FontChoice");
        mirror = Get<CheckBox>("Mirror"); onTop = Get<CheckBox>("OnTop"); countdown = Get<CheckBox>("Countdown"); guide = Get<CheckBox>("Guide"); play = Get<Button>("Play");
        Session saved;
        string loadError = null;
        try
        {
            saved = Session.Load(path);
            if (!File.Exists(path) && !testing)
            {
                string doner = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DoneLog", "settings.txt");
                if (File.Exists(doner)) foreach (string line in File.ReadAllLines(doner))
                { int index; if (line.StartsWith("theme=") && Int32.TryParse(line.Substring(6), out index) && index >= 0 && index < 4) saved.Theme = index; }
            }
        }
        catch (Exception ex) { saved = new Session(); loadError = "Не удалось прочитать сохранённый текст: " + ex.Message; }
        Editor.Text = saved.Text; FontSlider.Value = saved.Font; SpeedSlider.Value = saved.Speed;
        closeToTray = saved.CloseToTray;
        mirror.IsChecked = saved.Mirror; countdown.IsChecked = saved.Countdown; guide.IsChecked = saved.Guide; onTop.IsChecked = saved.OnTop; Theme.SelectedIndex = saved.Theme;
        FontChoice.SelectedIndex = Math.Max(0, Array.IndexOf(FontNames, saved.FontName));
        Editor.TextChanged += delegate { Stop(); Script.Text = Editor.Text; Reader.ScrollToTop(); UpdateWords(); Changed(); };
        FontSlider.ValueChanged += delegate { UpdateFont(); Changed(); };
        Get<TextBox>("FontSizeInput").LostKeyboardFocus += delegate { CommitFontSize(); };
        Get<TextBox>("FontSizeInput").PreviewKeyDown += delegate(object sender, KeyEventArgs e)
        { if (e.Key == Key.Enter) { CommitFontSize(); e.Handled = true; } };
        SpeedSlider.ValueChanged += delegate { UpdateSpeed(); Changed(); };
        Theme.SelectionChanged += delegate { ApplyTheme(); Changed(); };
        FontChoice.SelectionChanged += delegate { UpdateFontFamily(); Changed(); };
        mirror.Click += delegate { UpdateMirror(); Changed(); };
        onTop.Click += delegate { Window.Topmost = onTop.IsChecked == true; Changed(); };
        countdown.Click += delegate { Changed(); };
        guide.Click += delegate { UpdateGuide(); Changed(); };
        play.Click += delegate { Toggle(); };
        Get<Button>("Reset").Click += delegate { Reset(); };
        Get<Button>("Open").Click += delegate { OpenText(); };
        Get<Button>("Save").Click += delegate { SaveText(); };
        Get<Button>("EditorToggle").Click += delegate { SetEditor(!editorVisible); };
        Get<Button>("Full").Click += delegate { SetFull(!full); };
        Get<Button>("Settings").Click += delegate { ShowSettings(); };
        Get<Grid>("ReaderArea").SizeChanged += delegate { UpdateSpacing(); };
        Reader.ScrollChanged += delegate { UpdateProgress(); };
        Reader.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e) { Reader.ScrollToVerticalOffset(Reader.VerticalOffset - e.Delta); e.Handled = true; };
        Window.PreviewKeyDown += OnKey;
        Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
        {
            CommitFontSize();
            Stop();
            if (!testMode && !exitRequested && closeToTray && tray != null)
            {
                e.Cancel = true; HideToTray(); return;
            }
            if (!testMode && dirty && !Persist())
            {
                var answer = MessageBox.Show(Window, "Не удалось сохранить текст. Закрыть приложение без сохранения?\nВы можете отменить закрытие и сохранить текст кнопкой «Сохранить .txt».", "Суфлёр", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) e.Cancel = true;
            }
        };
        Window.StateChanged += delegate { if (Window.WindowState == WindowState.Minimized && tray != null) HideToTray(); };
        Window.Closed += delegate { autosave.Stop(); DetachRendering(); if (tray != null) { tray.Dispose(); tray = null; } if (!testMode) Application.Current.Shutdown(); };
        autosave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        autosave.Tick += delegate { autosave.Stop(); if (dirty && !testMode) Persist(); };
        Script.Text = Editor.Text; UpdateFont(); UpdateFontFamily(); UpdateSpeed(); UpdateMirror(); UpdateGuide(); UpdateWords(); ApplyTheme(); Window.Topmost = saved.OnTop;
        ready = true;
        if (loadError != null) Status.Text = loadError;
        Window.Loaded += delegate { UpdateSpacing(); Reader.ScrollToTop(); };
    }
    void Changed() { if (!ready) return; dirty = true; autosave.Stop(); autosave.Start(); }
    internal Session Snapshot() { return new Session { Text = Editor.Text, Font = FontSlider.Value, FontName = FontNames[Math.Max(0, FontChoice.SelectedIndex)], Speed = SpeedSlider.Value, Theme = Theme.SelectedIndex, Mirror = mirror.IsChecked == true, Countdown = countdown.IsChecked == true, Guide = guide.IsChecked == true, OnTop = onTop.IsChecked == true, CloseToTray = closeToTray }; }
    bool Persist()
    {
        try { Snapshot().Save(statePath); dirty = false; return true; }
        catch (Exception ex) { Status.Text = "Не удалось сохранить: " + ex.Message; return false; }
    }
    void UpdateFont() { Script.FontSize = FontSlider.Value; Script.LineHeight = FontSlider.Value * 1.48; Get<TextBox>("FontSizeInput").Text = ((int)FontSlider.Value).ToString(CultureInfo.InvariantCulture); }
    internal void CommitFontSize()
    {
        int number; var input = Get<TextBox>("FontSizeInput");
        if (Int32.TryParse(input.Text.Trim(), out number)) FontSlider.Value = Math.Max(5, Math.Min(100, number));
        UpdateFont();
    }
    internal void EnableTray() { if (tray == null) tray = new TrayController(this); }
    internal void HideToTray()
    {
        CommitFontSize(); Stop(); if (dirty && !testMode) Persist();
        if (settingsWindow != null) settingsWindow.Close();
        Window.Hide(); Status.Text = "Суфлёр в трее · нажмите на значок, чтобы открыть";
    }
    internal void ShowWindow()
    {
        Window.Show();
        if (Window.WindowState == WindowState.Minimized) Window.WindowState = full ? WindowState.Maximized : WindowState.Normal;
        Window.Activate();
    }
    internal void Exit() { exitRequested = true; Window.Close(); exitRequested = false; }
    internal Window CreateSettings()
    {
        Window dialog;
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Settings.xaml")) dialog = (Window)XamlReader.Load(stream);
        dialog.Resources.MergedDictionaries.Add(Window.Resources); dialog.Owner = Window; dialog.Icon = Window.Icon;
        ((CheckBox)dialog.FindName("AutoStart")).IsChecked = Startup.Enabled;
        ((CheckBox)dialog.FindName("CloseToTray")).IsChecked = closeToTray;
        ((Button)dialog.FindName("Cancel")).Click += delegate { dialog.Close(); };
        ((Button)dialog.FindName("Apply")).Click += delegate
        {
            try
            {
                if (!testMode) Startup.SetEnabled(((CheckBox)dialog.FindName("AutoStart")).IsChecked == true);
                closeToTray = ((CheckBox)dialog.FindName("CloseToTray")).IsChecked == true;
                Changed();
                if (!testMode && !Persist()) return;
                dialog.Close();
            }
            catch (Exception ex) { MessageBox.Show(dialog, "Не удалось сохранить настройки.\n" + ex.Message, "Настройки", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        return dialog;
    }
    internal void ShowSettings()
    {
        Stop();
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        try
        {
            settingsWindow = CreateSettings();
            settingsWindow.Closed += delegate { settingsWindow = null; };
            settingsWindow.ShowDialog();
        }
        catch (Exception ex) { settingsWindow = null; MessageBox.Show(Window, ex.Message, "Не удалось открыть настройки", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    void UpdateFontFamily() { Script.FontFamily = FontChoice.SelectedIndex == 1 ? interFamily : new FontFamily(FontNames[Math.Max(0, FontChoice.SelectedIndex)]); }
    void UpdateSpeed() { Get<TextBlock>("SpeedLabel").Text = "Скорость · " + (int)SpeedSlider.Value + " пикс/с"; }
    void UpdateMirror() { Get<StackPanel>("ScriptStack").RenderTransform = new ScaleTransform(mirror.IsChecked == true ? -1 : 1, 1); }
    void UpdateGuide() { Get<System.Windows.Controls.Border>("ReadingLine").Visibility = guide.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }
    void UpdateWords() { int n = Regex.Matches(Editor.Text, @"\S+").Count; Get<TextBlock>("WordCount").Text = n + " слов · текст сохраняется автоматически"; play.IsEnabled = n > 0; }
    void UpdateSpacing()
    {
        double height = Get<Grid>("ReaderArea").ActualHeight;
        double top = Math.Max(60, height * .27);
        Get<Border>("TopSpace").Height = top; Get<Border>("BottomSpace").Height = Math.Max(0, height - top);
        Get<Border>("ReadingLine").Margin = new Thickness(12, top, 12, 0);
    }
    internal void ApplyTheme()
    {
        string[][] palettes = {
            new [] { "#F1F7F4", "#FFFFFF", "#182C25", "#4C6258", "#185543", "#D5DFDA" },
            new [] { "#F8F3EA", "#FFFCF7", "#332B24", "#6B5B4A", "#80532C", "#DAD3CB" },
            new [] { "#202326", "#2C3034", "#F1F4F6", "#B6C1C8", "#286952", "#50565A" },
            new [] { "#151E32", "#202D46", "#EEF3FF", "#B2C2DF", "#365FA3", "#45516A" }
        };
        int i = Math.Max(0, Math.Min(3, Theme.SelectedIndex));
        string[] keys = { "Bg", "Surface", "Ink", "Muted", "Accent", "Edge" };
        for (int k = 0; k < keys.Length; k++) Window.Resources[keys[k]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palettes[i][k]));
    }
    internal void Toggle()
    {
        if (running || counting) { Stop(); return; }
        if (String.IsNullOrWhiteSpace(Editor.Text)) return;
        Window.UpdateLayout();
        if (Reader.VerticalOffset >= Reader.ScrollableHeight - .5) Reader.ScrollToTop();
        running = true;
        counting = countdown.IsChecked == true; countElapsed = 0;
        Get<TextBlock>("CountdownText").Visibility = counting ? Visibility.Visible : Visibility.Collapsed;
        Get<TextBlock>("CountdownText").Text = "3";
        play.Content = "Пауза"; Status.Text = counting ? "Приготовьтесь…" : "Чтение · пробел — пауза";
        watch.Restart(); previousFrame = 0;
        if (!renderAttached) { CompositionTarget.Rendering += OnFrame; renderAttached = true; }
    }
    void DetachRendering() { if (renderAttached) { CompositionTarget.Rendering -= OnFrame; renderAttached = false; } }
    internal void Stop()
    {
        bool wasActive = running || counting;
        running = counting = false; watch.Stop(); DetachRendering();
        Get<TextBlock>("CountdownText").Visibility = Visibility.Collapsed;
        play.Content = "Продолжить";
        if (wasActive) Status.Text = "Пауза · пробел — продолжить";
    }
    internal void Reset() { Stop(); Reader.ScrollToTop(); play.Content = "Начать"; Status.Text = "Готово к чтению"; }
    void OnFrame(object sender, EventArgs e)
    {
        double now = watch.Elapsed.TotalSeconds;
        double dt = Math.Min(.1, now - previousFrame); previousFrame = now;
        Advance(dt);
    }
    internal void Advance(double seconds)
    {
        if (!running) return;
        if (counting)
        {
            countElapsed += seconds;
            if (countElapsed < 3) { Get<TextBlock>("CountdownText").Text = Math.Ceiling(3 - countElapsed).ToString(CultureInfo.InvariantCulture); return; }
            counting = false; Get<TextBlock>("CountdownText").Visibility = Visibility.Collapsed; Status.Text = "Чтение · пробел — пауза"; return;
        }
        double end = Reader.ScrollableHeight;
        double next = Math.Min(end, Reader.VerticalOffset + SpeedSlider.Value * seconds);
        Reader.ScrollToVerticalOffset(next);
        if (next >= end - .1) { Stop(); Status.Text = "Текст прочитан · «В начало» — повторить"; play.Content = "Ещё раз"; }
    }
    void UpdateProgress() { Get<ProgressBar>("Progress").Value = Reader.ScrollableHeight > 0 ? Reader.VerticalOffset / Reader.ScrollableHeight * 100 : 0; }
    internal void SetEditor(bool visible)
    {
        editorVisible = visible;
        bool show = visible && !full;
        Get<Border>("EditorPanel").Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Get<ColumnDefinition>("EditorCol").Width = new GridLength(show ? 310 : 0);
        Get<ColumnDefinition>("GapCol").Width = new GridLength(show ? 20 : 0);
        Get<Button>("EditorToggle").Content = visible ? "Скрыть редактор" : "Показать редактор";
    }
    internal void SetFull(bool value)
    {
        if (full == value) return;
        full = value;
        if (full)
        {
            oldState = Window.WindowState; Window.WindowState = WindowState.Normal; Window.WindowStyle = WindowStyle.None; Window.ResizeMode = ResizeMode.NoResize; Window.WindowState = WindowState.Maximized;
        }
        else { Window.WindowState = WindowState.Normal; Window.WindowStyle = WindowStyle.SingleBorderWindow; Window.ResizeMode = ResizeMode.CanResize; Window.WindowState = oldState; }
        Get<Grid>("Header").Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        Get<Grid>("Root").Margin = new Thickness(full ? 16 : 24);
        SetEditor(editorVisible);
        if (full) Status.Text = "Полный экран · Esc — вернуться · пробел — пауза";
    }
    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { SetFull(!full); e.Handled = true; return; }
        if (e.Key == Key.Escape) { if (full) SetFull(false); else Stop(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { OpenText(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { SaveText(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter) { Toggle(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Space) { Toggle(); e.Handled = true; }
        if (e.Key == Key.Up || e.Key == Key.Down) { SpeedSlider.Value += e.Key == Key.Up ? 5 : -5; e.Handled = true; }
        if (e.Key == Key.Home) { Reset(); e.Handled = true; }
        if (e.Key == Key.PageDown || e.Key == Key.PageUp) { Reader.ScrollToVerticalOffset(Reader.VerticalOffset + (e.Key == Key.PageDown ? 1 : -1) * Reader.ViewportHeight * .6); e.Handled = true; }
    }
    void OpenText()
    {
        Stop();
        var dialog = new OpenFileDialog { Filter = "Текстовые файлы (*.txt)|*.txt", Title = "Открыть текст выступления" };
        if (dialog.ShowDialog(Window) != true) return;
        try
        {
            string text;
            try { text = File.ReadAllText(dialog.FileName, new UTF8Encoding(false, true)); }
            catch (DecoderFallbackException) { text = File.ReadAllText(dialog.FileName, Encoding.GetEncoding(1251)); }
            if (!String.IsNullOrWhiteSpace(Editor.Text) && MessageBox.Show(Window, "Заменить текущий текст содержимым файла?", "Открыть текст", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Editor.Text = text; Reset(); Status.Text = "Открыт: " + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex) { MessageBox.Show(Window, ex.Message, "Не удалось открыть файл", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    void SaveText()
    {
        var dialog = new SaveFileDialog { Filter = "Текстовые файлы (*.txt)|*.txt", FileName = "Выступление.txt", DefaultExt = ".txt" };
        if (dialog.ShowDialog(Window) != true) return;
        try { File.WriteAllText(dialog.FileName, Editor.Text, new UTF8Encoding(true)); Status.Text = "Сохранено: " + Path.GetFileName(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(Window, ex.Message, "Не удалось сохранить файл", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test") return SelfTest();
        bool created;
        using (var mutex = new Mutex(true, "Local\\Sufler.Application", out created))
        using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Sufler.Show"))
        {
            if (!created) { signal.Set(); return 0; }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sufler", "session.xml");
                var prompt = new Prompter(path, false);
                app.MainWindow = prompt.Window; prompt.EnableTray();
                var activation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                activation.Tick += delegate { if (signal.WaitOne(0)) prompt.ShowWindow(); };
                activation.Start();
                app.SessionEnding += delegate { prompt.Exit(); };
                return Array.IndexOf(args, "--background") >= 0 ? app.Run() : app.Run(prompt.Window);
            }
            catch (Exception ex) { MessageBox.Show("Не удалось запустить суфлёр.\n" + ex.Message, "Суфлёр", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
        }
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static double Luminance(Color c)
    {
        Func<byte, double> channel = delegate(byte value) { double v = value / 255.0; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); };
        return .2126 * channel(c.R) + .7152 * channel(c.G) + .0722 * channel(c.B);
    }
    static double Contrast(Brush a, Brush b)
    {
        double x = Luminance(((SolidColorBrush)a).Color), y = Luminance(((SolidColorBrush)b).Color);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
    static int SelfTest()
    {
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-output");
        Directory.CreateDirectory(dir);
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            string path = Path.Combine(dir, "session.xml");
            new Session { Text = "Проверка UTF-8\nВторая строка", Font = 64, Speed = 80, Theme = 2, Mirror = true, Countdown = false }.Save(path);
            var saved = Session.Load(path);
            Check(saved.Text.Contains("Вторая строка") && saved.Font == 64 && saved.Mirror, "Session roundtrip");
            saved.Text = "Изменённый текст"; saved.Save(path); Check(Session.Load(path).Text == saved.Text, "Atomic replacement");
            new Session { Font = Double.NaN, Speed = -1, Theme = 99 }.Save(path);
            saved = Session.Load(path); Check(saved.Font == 48 && saved.Speed == 40 && saved.Theme == 0, "Settings validation");
            new Session { Countdown = false }.Save(path);
            var p = new Prompter(path, true); p.Window.Show(); p.Window.UpdateLayout();
            p.Editor.Text = String.Join("\n\n", new string[] { new Session().Text, new Session().Text, new Session().Text }); p.Window.UpdateLayout();
            Check(p.Script.Text == p.Editor.Text, "Editor preview sync");
            p.FontSlider.Value = 72; Check(p.Script.FontSize == 72 && p.Script.LineHeight > 100, "Font and line spacing");
            p.FontSlider.Value = 5; Check(p.Script.FontSize == 5 && p.FontSlider.Minimum == 5 && p.FontSlider.TickFrequency == 1, "Minimum font size and unit increments");
            var fontInput = (TextBox)p.Window.FindName("FontSizeInput");
            fontInput.Text = "5"; p.CommitFontSize(); Check(p.FontSlider.Value == 5 && p.Script.FontSize == 5 && fontInput.Text == "5", "Direct font size 5");
            fontInput.Text = "1"; p.CommitFontSize(); Check(p.FontSlider.Value == 5 && fontInput.Text == "5", "Minimum clamp");
            fontInput.Text = "999"; p.CommitFontSize(); Check(p.FontSlider.Value == 100, "Maximum clamp");
            fontInput.Text = "abc"; p.CommitFontSize(); Check(fontInput.Text == "100", "Invalid font input");
            fontInput.Text = "5"; p.CommitFontSize();
            p.Snapshot().Save(path); Check(Session.Load(path).Font == 5, "Minimum font persistence");
            p.FontSlider.Value = 72;
            p.Window.UpdateLayout(); p.SpeedSlider.Value = 80; p.Toggle(); p.Advance(.5); p.Window.UpdateLayout();
            Check(p.Running && Math.Abs(p.Reader.VerticalOffset - 40) < 2, "Speed-based scrolling");
            p.Stop(); double offset = p.Reader.VerticalOffset; p.Advance(1); p.Window.UpdateLayout(); Check(p.Reader.VerticalOffset == offset, "Pause");
            p.Reset(); p.Window.UpdateLayout(); Check(p.Reader.VerticalOffset == 0 && !p.Running, "Reset");
            for (int i = 0; i < 5; i++) { p.FontChoice.SelectedIndex = i; p.Window.UpdateLayout(); Check(p.Script.FontFamily != null, "Font selection"); }
            p.FontChoice.SelectedIndex = 1; p.Snapshot().Save(path); Check(Session.Load(path).FontName == "Inter", "Font persistence");
            GlyphTypeface glyph; Check(new Typeface(p.Script.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out glyph) && glyph.FontUri.IsFile && glyph.FontUri.LocalPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase), "Inter uses bundled local font");
            Check(glyph.CharacterToGlyphMap.ContainsKey(0x0416), "Inter Cyrillic glyphs");
            for (int i = 0; i < 4; i++)
            {
                p.Theme.SelectedIndex = i; p.Window.UpdateLayout();
                foreach (string bg in new [] { "Bg", "Surface" }) foreach (string ink in new [] { "Ink", "Muted" })
                    Check(Contrast((Brush)p.Window.Resources[ink], (Brush)p.Window.Resources[bg]) >= 4.5, "Theme text contrast");
                Check(Contrast(((CheckBox)p.Window.FindName("Mirror")).Foreground, p.Window.Background) >= 4.5, "Checkbox text contrast");
                Check(Contrast(Brushes.White, (Brush)p.Window.Resources["Accent"]) >= 4.5, "Primary button text contrast");
            }
            p.SetEditor(false); p.Window.UpdateLayout(); Check(((Border)p.Window.FindName("EditorPanel")).Visibility == Visibility.Collapsed, "Hide editor"); p.SetEditor(true);
            p.SetFull(true); Check(p.Window.WindowStyle == WindowStyle.None, "Fullscreen"); p.SetFull(false); Check(p.Window.WindowStyle == WindowStyle.SingleBorderWindow, "Exit fullscreen");
            p.Toggle(); p.Reader.ScrollToEnd(); p.Window.UpdateLayout(); p.Advance(1); Check(!p.Running, "Stop at end");
            p.Editor.Text = "   "; p.Toggle(); Check(!p.Running, "Reject empty script");
            p.Editor.Text = new Session().Text; p.FontSlider.Value = 48; p.Theme.SelectedIndex = 0; p.Reset(); p.Window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)p.Window.ActualWidth, (int)p.Window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(p.Window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(dir, "preview.png"))) encoder.Save(file);
            p.Theme.SelectedIndex = 2; p.Window.Width = 920; p.Window.Height = 650; p.Window.UpdateLayout();
            var darkBitmap = new RenderTargetBitmap((int)p.Window.ActualWidth, (int)p.Window.ActualHeight, 96, 96, PixelFormats.Pbgra32); darkBitmap.Render(p.Window);
            var darkEncoder = new PngBitmapEncoder(); darkEncoder.Frames.Add(BitmapFrame.Create(darkBitmap)); using (var file = File.Create(Path.Combine(dir, "preview-dark.png"))) darkEncoder.Save(file);
            p.Theme.IsDropDownOpen = true; p.Window.UpdateLayout(); Check(p.Theme.IsDropDownOpen, "Theme dropdown"); p.Theme.IsDropDownOpen = false;
            ((CheckBox)p.Window.FindName("Countdown")).IsChecked = true; p.Toggle(); p.Advance(1); Check(((TextBlock)p.Window.FindName("CountdownText")).Text == "2", "Countdown"); p.Advance(2.1); Check(p.Reader.VerticalOffset == 0, "Countdown keeps initial position"); p.Advance(.5); p.Window.UpdateLayout(); Check(p.Reader.VerticalOffset > 0, "Scroll after countdown"); p.Stop();
            Check(Startup.CommandFor(@"C:\Folder With Space\Sufler.exe") == "\"C:\\Folder With Space\\Sufler.exe\" --background", "Quoted startup command");
            bool wasStartupEnabled = Startup.Enabled;
            var settings = p.CreateSettings(); settings.Show(); settings.UpdateLayout();
            Check(((CheckBox)settings.FindName("AutoStart")).IsChecked == wasStartupEnabled, "Startup state displayed");
            Check(Contrast(((CheckBox)settings.FindName("AutoStart")).Foreground, settings.Background) >= 4.5, "Settings text contrast");
            var settingsBitmap = new RenderTargetBitmap((int)settings.ActualWidth, (int)settings.ActualHeight, 96, 96, PixelFormats.Pbgra32); settingsBitmap.Render(settings);
            var settingsEncoder = new PngBitmapEncoder(); settingsEncoder.Frames.Add(BitmapFrame.Create(settingsBitmap)); using (var file = File.Create(Path.Combine(dir, "settings.png"))) settingsEncoder.Save(file);
            ((CheckBox)settings.FindName("CloseToTray")).IsChecked = false;
            ((Button)settings.FindName("Apply")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!p.Snapshot().CloseToTray && Startup.Enabled == wasStartupEnabled, "Settings apply without changing real startup in test mode");
            p.Snapshot().Save(path); Check(!Session.Load(path).CloseToTray, "Close-to-tray persistence");
            p.EnableTray(); p.Toggle(); p.HideToTray(); Check(!p.Window.IsVisible && !p.Running, "Hide to tray pauses playback");
            p.ShowWindow(); Check(p.Window.IsVisible, "Restore from tray");
            p.Window.WindowState = WindowState.Minimized; Check(!p.Window.IsVisible, "Minimize to tray"); p.ShowWindow(); Check(p.Window.IsVisible && p.Window.WindowState == WindowState.Normal, "Restore minimized window");
            p.Window.Close();
            new Session { CloseToTray = true, Countdown = false, Font = 5 }.Save(path);
            var lifecycle = new Prompter(path, false); lifecycle.EnableTray(); lifecycle.Window.Show(); lifecycle.Window.UpdateLayout();
            Check(lifecycle.Script.FontSize == 5 && ((TextBox)lifecycle.Window.FindName("FontSizeInput")).Text == "5", "Actual restored font size 5");
            lifecycle.Window.Close(); Check(!lifecycle.Window.IsVisible, "Close button hides actual application to tray");
            lifecycle.ShowWindow(); Check(lifecycle.Window.IsVisible, "Restore after close button");
            lifecycle.Exit();
            File.WriteAllText(Path.Combine(dir, "result.txt"), "PASS: UTF-8 persistence, atomic replacement, setting validation, editor sync, typography, font size from 5 with step 1, direct numeric size 5 + input validation, five fonts, local bundled Inter + Cyrillic glyphs, font persistence, scrolling speed, pause, reset, four themes + contrast, theme dropdown, editor visibility, fullscreen, end of script, empty script, countdown, startup command quoting, settings + contrast, close-to-tray persistence, tray creation + hide + pause + restore + minimize + cleanup.\r\n", Encoding.UTF8);
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "result.txt"), "FAIL: " + ex, Encoding.UTF8); return 1; }
    }
}

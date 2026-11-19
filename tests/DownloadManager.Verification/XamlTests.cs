using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using DownloadManager.Presentation.Localization;
using DownloadManager.Presentation.ViewModels;

namespace DownloadManager.Verification;

/// <summary>
/// The WPF project cannot be compiled on Linux, so these tests statically verify everything that can be verified
/// about its XAML: well-formedness, resource keys, theme parity, and that every {Binding} resolves against the real
/// view-model types (with the DataContext of each element worked out from DataContext= and ItemsSource=/ItemTemplate).
/// They do NOT prove that the XAML compiles or renders.
/// </summary>
public static partial class XamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string AppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DownloadManager.sln"))) dir = dir.Parent;
        if (dir is null) throw new AssertionException("could not locate the solution directory");
        return Path.Combine(dir.FullName, "src", "DownloadManager.App");
    }

    private static XDocument Load(string relative) => XDocument.Load(Path.Combine(AppDir(), relative), LoadOptions.SetLineInfo);

    private static string Where(XElement e) => $"<{e.Name.LocalName}> line {((System.Xml.IXmlLineInfo)e).LineNumber}";

    private static IEnumerable<string> Keys(XDocument doc) =>
        doc.Descendants().Select(e => (string?)e.Attribute(Xaml + "Key")).Where(k => k is not null)!;

    [GeneratedRegex(@"\{(StaticResource|DynamicResource)\s+(\w+)\}")]
    private static partial Regex ResourceRef();

    [GeneratedRegex(@"^\{Binding\s*(?<body>.*)\}$", RegexOptions.Singleline)]
    private static partial Regex BindingExpr();

    public static IEnumerable<TestCase> All()
    {
        yield return new TestCase("xaml: files are well-formed, resources resolve, light/dark themes match, styles fit their targets", _ =>
        {
            var files = new[] { "App.xaml", "MainWindow.xaml", "DownloadWindow.xaml", "FloatingTrackerWindow.xaml", "Themes/Light.xaml", "Themes/Dark.xaml", "Themes/Controls.xaml" };
            var docs = files.ToDictionary(f => f, Load);

            var light = Keys(docs["Themes/Light.xaml"]).ToHashSet();
            var dark = Keys(docs["Themes/Dark.xaml"]).ToHashSet();
            Assert.True(light.SetEquals(dark), "theme key mismatch: " + string.Join(",", light.Except(dark).Concat(dark.Except(light))));
            Assert.True(light.Count >= 15, "theme brushes present");

            var global = light.Concat(Keys(docs["Themes/Controls.xaml"])).Concat(Keys(docs["App.xaml"])).Append("Loc").ToHashSet();

            // styles by key -> target type, for "applied to the right control" checks
            var styleTargets = docs["Themes/Controls.xaml"].Descendants().Where(e => e.Name.LocalName == "Style" && e.Attribute(Xaml + "Key") is not null)
                .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => (string?)e.Attribute("TargetType") ?? "");

            foreach (var (file, doc) in docs)
            {
                var defined = new HashSet<string>(file.StartsWith("Themes/Controls") ? Array.Empty<string>() : global);
                if (file.StartsWith("Themes/Controls")) foreach (var k in light.Concat(Keys(docs["App.xaml"]))) defined.Add(k);

                foreach (var e in doc.Descendants())
                {
                    foreach (var a in e.Attributes())
                    {
                        foreach (Match m in ResourceRef().Matches(a.Value))
                        {
                            var key = m.Groups[2].Value;
                            if (m.Groups[1].Value == "DynamicResource")
                                Assert.True(light.Contains(key) || styleTargets.ContainsKey(key), $"{file} {Where(e)}: DynamicResource '{key}' is not a theme key");
                            else
                                Assert.True(defined.Contains(key), $"{file} {Where(e)}: StaticResource '{key}' is not defined (or is defined later in the same dictionary)");
                        }
                        if (a.Name.LocalName == "Style" && a.Parent is { } owner && ResourceRef().Match(a.Value) is { Success: true } sm)
                        {
                            var target = styleTargets.GetValueOrDefault(sm.Groups[2].Value);
                            Assert.True(target is null or "" || target == owner.Name.LocalName, $"{file} {Where(owner)}: style '{sm.Groups[2].Value}' targets {target}");
                        }
                    }
                    if (e.Attribute(Xaml + "Key") is { } k) defined.Add(k.Value);
                }
            }

            foreach (var style in docs["Themes/Controls.xaml"].Descendants().Where(e => e.Name.LocalName == "Style"))
                if ((string?)style.Attribute("BasedOn") is { } based && ResourceRef().Match(based) is { Success: true } bm)
                    Assert.True(styleTargets.ContainsKey(bm.Groups[2].Value), $"BasedOn '{bm.Groups[2].Value}' missing");
            return Task.CompletedTask;
        });

        yield return new TestCase("xaml: code-behind matches (x:Class, event handlers) and RTL/theme hooks exist", _ =>
        {
            var window = Load("MainWindow.xaml").Root!;
            Assert.Equal("DownloadManager.App.MainWindow", (string)window.Attribute(Xaml + "Class")!, "x:Class");
            var code = File.ReadAllText(Path.Combine(AppDir(), "MainWindow.xaml.cs"));
            Assert.True(code.Contains("partial class MainWindow") && code.Contains("namespace DownloadManager.App"), "code-behind class");
            foreach (var attr in window.Attributes().Where(a => a.Name.LocalName is "Drop" or "DragOver"))
                Assert.True(Regex.IsMatch(code, $@"void\s+{attr.Value}\s*\("), $"handler {attr.Value} missing in code-behind");

            var app = File.ReadAllText(Path.Combine(AppDir(), "App.xaml.cs"));
            Assert.True(app.Contains("Resources[\"Loc\"]") && app.Contains("MergedDictionaries[0]"), "App swaps theme at index 0 and registers Loc");
            var appXaml = Load("App.xaml").Root!;
            var merged = appXaml.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute("Source") is not null).Select(e => (string)e.Attribute("Source")!).ToList();
            Assert.True(merged.Count == 2 && merged[0] == "Themes/Light.xaml", "theme dictionary must be first in App.xaml");
            Assert.True(File.Exists(Path.Combine(AppDir(), merged[1])), "Controls dictionary exists");
            return Task.CompletedTask;
        });

        yield return new TestCase("xaml: every binding resolves on the right view-model, with correct modes, types and language keys", _ =>
        {
            var doc = Load("MainWindow.xaml");
            var checkedCount = 0;

            foreach (var element in doc.Descendants())
            {
                var context = ContextOf(element, typeof(MainViewModel));
                foreach (var attr in element.Attributes())
                {
                    var m = BindingExpr().Match(attr.Value);
                    if (!m.Success) continue;
                    var ctx = attr.Name.LocalName == "DataContext" || attr.Name.LocalName == "ItemsSource" && IsItemsOwner(element)
                        ? ContextOf(element, typeof(MainViewModel), excludeOwnDataContext: true) : context;
                    CheckBinding(element, attr, m.Groups["body"].Value, ctx);
                    checkedCount++;
                }
            }
            Assert.True(checkedCount > 150, $"only {checkedCount} bindings were checked");

            var dlDoc = Load("DownloadWindow.xaml");
            var dlChecked = 0;
            foreach (var element in dlDoc.Descendants())
            {
                var context = ContextOf(element, typeof(DownloadSessionViewModel));
                foreach (var attr in element.Attributes())
                {
                    var m = BindingExpr().Match(attr.Value);
                    if (!m.Success) continue;
                    var ctx = attr.Name.LocalName == "DataContext" || attr.Name.LocalName == "ItemsSource" && IsItemsOwner(element)
                        ? ContextOf(element, typeof(DownloadSessionViewModel), excludeOwnDataContext: true) : context;
                    CheckBinding(element, attr, m.Groups["body"].Value, ctx);
                    dlChecked++;
                }
            }
            Assert.True(dlChecked > 40, $"only {dlChecked} DownloadWindow bindings were checked");

            var floatDoc = Load("FloatingTrackerWindow.xaml");
            var floatChecked = 0;
            foreach (var element in floatDoc.Descendants())
            {
                var context = ContextOf(element, typeof(MainViewModel));
                foreach (var attr in element.Attributes())
                {
                    var m = BindingExpr().Match(attr.Value);
                    if (!m.Success) continue;
                    CheckBinding(element, attr, m.Groups["body"].Value, context);
                    floatChecked++;
                }
            }
            Assert.True(floatChecked >= 6, $"only {floatChecked} FloatingTrackerWindow bindings were checked");
            return Task.CompletedTask;
        });
    }

    private static bool IsItemsOwner(XElement e) => e.Name.LocalName is "ListBox" or "ItemsControl";

    /// <summary>The type of the DataContext an element's bindings are evaluated against.</summary>
    private static Type ContextOf(XElement element, Type rootContext, bool excludeOwnDataContext = false)
    {
        var ctx = rootContext;
        foreach (var el in element.AncestorsAndSelf().Reverse())
        {
            if (el.Name.LocalName == "DataTemplate" && el.Parent is { } itemTemplate && itemTemplate.Name.LocalName.EndsWith(".ItemTemplate") && itemTemplate.Parent is { } owner)
            {
                var src = (string?)owner.Attribute("ItemsSource");
                var m = src is null ? null : BindingExpr().Match(src);
                if (m is not { Success: true }) throw new AssertionException($"{Where(owner)}: ItemTemplate owner needs a bound ItemsSource");
                ctx = ElementType(ResolvePath(ctx, PathOf(m.Groups["body"].Value), owner, "ItemsSource"), owner);
            }

            if (el == element && excludeOwnDataContext) continue;
            if ((string?)el.Attribute("DataContext") is { } dc && BindingExpr().Match(dc) is { Success: true } dm)
            {
                var parentContext = el == element ? ctx : ctx;
                ctx = ResolvePath(parentContext, PathOf(dm.Groups["body"].Value), el, "DataContext");
            }
        }
        return ctx;
    }

    private static string PathOf(string body)
    {
        var comma = body.IndexOf(',');
        return (comma < 0 ? body : body[..comma]).Trim();
    }

    private static Type ElementType(Type collection, XElement where)
    {
        var enumerable = collection.GetInterfaces().Append(collection)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0] ?? throw new AssertionException($"{Where(where)}: ItemsSource is not a typed collection ({collection.Name})");
    }

    private static Type ResolvePath(Type start, string path, XElement where, string attribute)
    {
        var type = start;
        foreach (var segment in path.Split('.'))
        {
            var prop = type.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null) throw new AssertionException($"{Where(where)} {attribute}: '{segment}' is not a public property of {type.Name} (path '{path}')");
            type = prop.PropertyType;
        }
        return type;
    }

    private static PropertyInfo LastProperty(Type start, string path)
    {
        PropertyInfo? prop = null;
        var type = start;
        foreach (var segment in path.Split('.'))
        {
            prop = type.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance)!;
            type = prop.PropertyType;
        }
        return prop!;
    }

    private static void CheckBinding(XElement element, XAttribute attr, string body, Type ctx)
    {
        var path = PathOf(body);
        var elementName = element.Name.LocalName;
        var attrName = attr.Name.LocalName;

        // {Binding [key], Source={StaticResource Loc}} - language lookups
        if (path.StartsWith('[') && path.EndsWith(']'))
        {
            Assert.True(body.Contains("Source={StaticResource Loc}"), $"{Where(element)} {attrName}: indexer binding without the Loc source");
            var key = path[1..^1];
            Assert.True(Strings.En.ContainsKey(key) && Strings.Ar.ContainsKey(key), $"{Where(element)} {attrName}: language key '{key}' missing (en/ar)");
            return;
        }

        if (body.Contains("Source=") || body.Contains("RelativeSource") || body.Contains("ElementName")) return;
        if (path.Length == 0) throw new AssertionException($"{Where(element)} {attrName}: empty binding path");

        ResolvePath(ctx, path, element, attrName);
        var property = LastProperty(ctx, path);
        var oneWay = body.Contains("Mode=OneWay");

        var twoWayByDefault = (elementName == "TextBox" && attrName == "Text") || (elementName is "RadioButton" or "CheckBox" && attrName == "IsChecked")
                              || (elementName == "Slider" && attrName == "Value") || (elementName == "ListBox" && attrName == "SelectedItem");
        var twoWay = body.Contains("Mode=TwoWay") || (twoWayByDefault && !oneWay);
        if (twoWay)
            Assert.True(property.SetMethod?.IsPublic == true, $"{Where(element)} {attrName}: two-way binding to read-only property {property.DeclaringType!.Name}.{property.Name} throws at runtime");

        if (attrName == "Command")
            Assert.True(typeof(ICommand).IsAssignableFrom(property.PropertyType), $"{Where(element)}: '{path}' is not an ICommand");

        if (body.Contains("BoolToVis") || body.Contains("RtlToFlow"))
            Assert.True(property.PropertyType == typeof(bool), $"{Where(element)} {attrName}: converter needs bool but '{path}' is {property.PropertyType.Name}");
        else if (attrName == "Visibility")
            throw new AssertionException($"{Where(element)}: Visibility bound to '{path}' without a converter");

        if (elementName == "DataTrigger" && attrName == "Binding" && element.Attribute("Value") is { } value)
        {
            if (property.PropertyType.IsEnum) Assert.True(Enum.IsDefined(property.PropertyType, value.Value), $"{Where(element)}: '{value.Value}' is not a {property.PropertyType.Name}");
            if (property.PropertyType == typeof(bool)) Assert.True(value.Value is "True" or "False", $"{Where(element)}: bool trigger value '{value.Value}'");
        }

        if (attrName is "Text" or "Content" or "ToolTip" or "Title" && property.PropertyType.IsEnum)
            throw new AssertionException($"{Where(element)}: enum '{path}' shown directly; use a localised text property");
    }
}

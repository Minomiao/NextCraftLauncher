using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using NextCraftLauncher.ViewModels;

namespace NextCraftLauncher.Views;

public partial class SettingsView : UserControl
{
    /// <summary>下拉列表比输入框左右各多出的宽度。</summary>
    private const double PopupBleed = 1;

    public SettingsView()
    {
        InitializeComponent();

        // 设置页的所有下拉框：展开后的列表比输入框左右各宽 1px。
        foreach (var combo in this.GetLogicalDescendants().OfType<ComboBox>())
        {
            Popup? popup = null;

            combo.TemplateApplied += (_, e) => popup = e.NameScope.Find<Popup>("PART_Popup");

            // 每次展开前按当前宽度重算，窗口缩放后同样保持左右各 1px。
            combo.PropertyChanged += (_, e) =>
            {
                if (e.Property == ComboBox.IsDropDownOpenProperty && combo.IsDropDownOpen)
                    ApplyPopupBleed(combo, popup);
            };
        }
    }

    /// <summary>
    /// 模板把弹出列表的最小宽度绑在输入框宽度上，普通样式改不动它，
    /// 所以直接给弹出层的容器设本地值；弹层本身相对目标居中，宽度多出 2px 就是左右各 1px。
    /// </summary>
    private static void ApplyPopupBleed(ComboBox combo, Popup? popup)
    {
        if (popup?.Child is not Control list) return;

        list.MinWidth = combo.Bounds.Width + PopupBleed * 2;
    }

    private async void OnBrowseJavaClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 java.exe",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Java 可执行文件")
                {
                    Patterns = new[] { "java.exe", "java" },
                },
            },
        });

        if (files.Count == 0) return;

        var path = files[0].Path.LocalPath;
        if (!string.IsNullOrEmpty(path))
            await viewModel.SetPreferredJavaAsync(path);
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using STranslate.Core;
using STranslate.Plugin;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;

namespace STranslate.ViewModels;

public partial class PromptEditViewModel : ObservableObject, IDisposable
{
    private readonly Internationalization _i18n = Ioc.Default.GetRequiredService<Internationalization>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemovePromptCommand), nameof(EditPromptCommand), nameof(CopyPromptCommand))]
    public partial Prompt? SelectedPrompt { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemovePromptItemCommand))]
    public partial PromptItem? SelectedPromptItem { get; set; }

    [ObservableProperty]
    public partial List<string> Roles { get; set; } = ["system", "user", "assistant"];

    public ObservableCollection<Prompt> Prompts { get; set; } = [];
    public ObservableCollection<PromptItem> PromptItems { get; set; } = [];

    private readonly ObservableCollection<Prompt> _originalPrompts;
    private bool _isUpdatingPromptEnabled = false;

    public PromptEditViewModel(ObservableCollection<Prompt> prompts, List<string>? roles = default)
    {
        _originalPrompts = prompts;
        if (roles is not null)
        {
            Roles = roles;
        }

        // 克隆所有 Prompt 以便编辑
        foreach (var prompt in prompts)
        {
            var clonedPrompt = prompt.Clone();
            Prompts.Add(clonedPrompt);
            // 为每个 Prompt 添加 PropertyChanged 监听
            clonedPrompt.PropertyChanged += OnPromptPropertyChanged;
        }

        // 监听集合变化，为新添加的 Prompt 也添加监听
        Prompts.CollectionChanged += OnPromptsCollectionChanged;

        PromptItems.CollectionChanged += OnPromptItemsCollectionChanged;

        // 选择第一个 Prompt
        if (Prompts.Count > 0)
        {
            SelectedPrompt = Prompts.FirstOrDefault(x => x.IsEnabled) ?? Prompts.FirstOrDefault();
            LoadPromptItems();
        }
    }

    private void OnPromptsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (Prompt prompt in e.NewItems)
            {
                prompt.PropertyChanged += OnPromptPropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (Prompt prompt in e.OldItems)
            {
                prompt.PropertyChanged -= OnPromptPropertyChanged;
            }
        }
    }

    private void OnPromptItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (SelectedPrompt == null) return;

        // 当集合发生移动操作时,同步更新 SelectedPrompt.Items
        if (e.Action == NotifyCollectionChangedAction.Move)
        {
            if (e.OldStartingIndex >= 0 && e.NewStartingIndex >= 0)
            {
                var item = SelectedPrompt.Items[e.OldStartingIndex];
                SelectedPrompt.Items.RemoveAt(e.OldStartingIndex);
                SelectedPrompt.Items.Insert(e.NewStartingIndex, item);
            }
        }
    }

    private void OnPromptPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Prompt.IsEnabled) && !_isUpdatingPromptEnabled)
        {
            var changedPrompt = sender as Prompt;
            if (changedPrompt?.IsEnabled == true)
            {
                // 禁用所有其他 Prompt
                _isUpdatingPromptEnabled = true;
                try
                {
                    foreach (var prompt in Prompts)
                    {
                        if (prompt != changedPrompt)
                        {
                            prompt.IsEnabled = false;
                        }
                    }
                }
                finally
                {
                    _isUpdatingPromptEnabled = false;
                }
            }
        }
    }

    partial void OnSelectedPromptChanged(Prompt? value)
    {
        LoadPromptItems();
    }

    private void LoadPromptItems()
    {
        PromptItems.Clear();
        if (SelectedPrompt != null)
        {
            foreach (var item in SelectedPrompt.Items)
            {
                PromptItems.Add(item);
            }
        }
        SelectedPromptItem = PromptItems.FirstOrDefault();
    }

    [RelayCommand]
    private void AddPrompt()
    {
        var newPrompt = new Prompt(_i18n.GetTranslation("NewPrompt"), [], false);
        Prompts.Add(newPrompt);
        SelectedPrompt = newPrompt;
    }

    public bool CanCopyPrompt() => SelectedPrompt is not null;

    [RelayCommand(CanExecute = nameof(CanCopyPrompt))]
    private void CopyPrompt()
    {
        if (SelectedPrompt is not null)
        {
            // 克隆选中的 Prompt
            var copiedPrompt = SelectedPrompt.Clone();
            // 复制的新 Prompt 生成独立 Id（Clone 保留原 Id，必须显式覆盖）
            copiedPrompt.Id = Guid.NewGuid().ToString("N");

            // 生成唯一的名字
            var newName = GenerateUniqueName(SelectedPrompt.Name + _i18n.GetTranslation("NewPromptSuffix"));
            copiedPrompt.Name = newName;
            copiedPrompt.IsEnabled = false;

            // 找到选中项的索引，在其后插入
            var selectedIndex = Prompts.IndexOf(SelectedPrompt);
            if (selectedIndex >= 0)
            {
                Prompts.Insert(selectedIndex + 1, copiedPrompt);
            }
            else
            {
                Prompts.Add(copiedPrompt);
            }

            // 选中新创建的 Prompt
            SelectedPrompt = copiedPrompt;
        }
    }

    private string GenerateUniqueName(string baseName)
    {
        var name = baseName;
        var counter = 1;

        // 检查名称是否已存在，如果存在则添加数字后缀
        while (Prompts.Any(p => p.Name == name))
        {
            name = $"{baseName}_{counter}";
            counter++;
        }

        return name;
    }

    public bool CanRemovePrompt() => SelectedPrompt is not null && Prompts.Count > 1;

    [RelayCommand(CanExecute = nameof(CanRemovePrompt))]
    private void RemovePrompt()
    {
        if (SelectedPrompt is not null)
        {
            var index = Prompts.IndexOf(SelectedPrompt);

            // 移除事件监听
            SelectedPrompt.PropertyChanged -= OnPromptPropertyChanged;
            Prompts.Remove(SelectedPrompt);

            // 选择下一个可用的 Prompt
            if (Prompts.Count > 0)
            {
                SelectedPrompt = Prompts[Math.Min(index, Prompts.Count - 1)];
            }
        }
    }

    public bool CanEditPrompt() => SelectedPrompt is not null;

    [RelayCommand(CanExecute = nameof(CanEditPrompt))]
    private void EditPrompt()
    {
        // 这里可以添加单个 Prompt 的详细编辑逻辑
        // 目前在主界面已经可以编辑了
    }

    [RelayCommand]
    private void AddPromptItem()
    {
        if (SelectedPrompt is not null)
        {
            var newItem = new PromptItem(Roles.FirstOrDefault() ?? "user", "");
            SelectedPrompt.Items.Add(newItem);
            PromptItems.Add(newItem);
            SelectedPromptItem = newItem;
        }
    }

    public bool CanRemovePromptItem() => SelectedPromptItem is not null && PromptItems.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRemovePromptItem))]
    private void RemovePromptItem()
    {
        if (SelectedPromptItem is not null && SelectedPrompt is not null)
        {
            SelectedPrompt.Items.Remove(SelectedPromptItem);
            PromptItems.Remove(SelectedPromptItem);
        }
    }

    [RelayCommand]
    private void Save(Window window)
    {
        // 更新原始 Prompts 集合
        _originalPrompts.Clear();
        foreach (var prompt in Prompts)
        {
            _originalPrompts.Add(prompt.Clone());
        }

        window.DialogResult = true;

        // VM 释放统一交由 PromptEditWindow.OnClosed 处理，确保 X / Alt+F4 等关闭路径也释放。
        window.Close();
    }

    [RelayCommand]
    private void Cancel(Window window)
    {
        window.DialogResult = false;

        // VM 释放统一交由 PromptEditWindow.OnClosed 处理，确保 X / Alt+F4 等关闭路径也释放。
        window.Close();
    }

    // 清理事件监听：取消每个 Prompt.PropertyChanged 与集合变化订阅，
    // 由 PromptEditWindow.OnClosed 统一调用，避免单例持有的 Prompt 集合反向钉住 VM。
    public void Dispose()
    {
        foreach (var prompt in Prompts)
        {
            prompt.PropertyChanged -= OnPromptPropertyChanged;
        }
        Prompts.CollectionChanged -= OnPromptsCollectionChanged;
        PromptItems.CollectionChanged -= OnPromptItemsCollectionChanged;
    }
}
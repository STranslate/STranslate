using STranslate.Core;
using STranslate.Plugin;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace STranslate.Controls;

public class InputControl : Control
{
    #region Constants

    private const string PartTextBoxName = "PART_TextBox";
    private const string PartFontSizeHintBorderName = "PART_FontSizeHintBorder";
    private const string PartFontSizeTextName = "PART_FontSizeText";
    private const string PartIdentifiedLanguageComboBoxName = "PART_IdentifiedLanguageComboBox";
    private const string PartLanguageDetectorComboBoxName = "PART_LanguageDetectorComboBox";
    private const int FontSizeHintAnimationDurationMs = 1200;

    #endregion

    static InputControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(InputControl),
            new FrameworkPropertyMetadata(typeof(InputControl)));
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(InputControl),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string IdentifiedLanguage
    {
        get => (string)GetValue(IdentifiedLanguageProperty);
        set => SetValue(IdentifiedLanguageProperty, value);
    }
    public static readonly DependencyProperty IdentifiedLanguageProperty =
        DependencyProperty.Register(
            nameof(IdentifiedLanguage),
            typeof(string),
            typeof(InputControl),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public LangEnum SourceLanguage
    {
        get => (LangEnum)GetValue(SourceLanguageProperty);
        set => SetValue(SourceLanguageProperty, value);
    }

    public static readonly DependencyProperty SourceLanguageProperty =
        DependencyProperty.Register(
            nameof(SourceLanguage),
            typeof(LangEnum),
            typeof(InputControl),
            new PropertyMetadata(LangEnum.Auto));

    public IdentifiedLanguageStateKind IdentifiedLanguageState
    {
        get => (IdentifiedLanguageStateKind)GetValue(IdentifiedLanguageStateProperty);
        set => SetValue(IdentifiedLanguageStateProperty, value);
    }

    public static readonly DependencyProperty IdentifiedLanguageStateProperty =
        DependencyProperty.Register(
            nameof(IdentifiedLanguageState),
            typeof(IdentifiedLanguageStateKind),
            typeof(InputControl),
            new PropertyMetadata(IdentifiedLanguageStateKind.None));

    public IEnumerable<DropdownDataGeneric<LangEnum>>? IdentifiedLanguageItemsSource
    {
        get => (IEnumerable<DropdownDataGeneric<LangEnum>>?)GetValue(IdentifiedLanguageItemsSourceProperty);
        set => SetValue(IdentifiedLanguageItemsSourceProperty, value);
    }

    public static readonly DependencyProperty IdentifiedLanguageItemsSourceProperty =
        DependencyProperty.Register(
            nameof(IdentifiedLanguageItemsSource),
            typeof(IEnumerable<DropdownDataGeneric<LangEnum>>),
            typeof(InputControl));

    public LangEnum SelectedIdentifiedLanguage
    {
        get => (LangEnum)GetValue(SelectedIdentifiedLanguageProperty);
        set => SetValue(SelectedIdentifiedLanguageProperty, value);
    }

    public static readonly DependencyProperty SelectedIdentifiedLanguageProperty =
        DependencyProperty.Register(
            nameof(SelectedIdentifiedLanguage),
            typeof(LangEnum),
            typeof(InputControl),
            new PropertyMetadata(LangEnum.Auto));

    public bool CanSelectIdentifiedLanguage
    {
        get => (bool)GetValue(CanSelectIdentifiedLanguageProperty);
        set => SetValue(CanSelectIdentifiedLanguageProperty, value);
    }

    public static readonly DependencyProperty CanSelectIdentifiedLanguageProperty =
        DependencyProperty.Register(
            nameof(CanSelectIdentifiedLanguage),
            typeof(bool),
            typeof(InputControl),
            new PropertyMetadata(false));

    public IEnumerable<DropdownDataGeneric<LanguageDetectorType>>? LanguageDetectorItemsSource
    {
        get => (IEnumerable<DropdownDataGeneric<LanguageDetectorType>>?)GetValue(LanguageDetectorItemsSourceProperty);
        set => SetValue(LanguageDetectorItemsSourceProperty, value);
    }

    public static readonly DependencyProperty LanguageDetectorItemsSourceProperty =
        DependencyProperty.Register(
            nameof(LanguageDetectorItemsSource),
            typeof(IEnumerable<DropdownDataGeneric<LanguageDetectorType>>),
            typeof(InputControl));

    public LanguageDetectorType SelectedLanguageDetector
    {
        get => (LanguageDetectorType)GetValue(SelectedLanguageDetectorProperty);
        set => SetValue(SelectedLanguageDetectorProperty, value);
    }

    public static readonly DependencyProperty SelectedLanguageDetectorProperty =
        DependencyProperty.Register(
            nameof(SelectedLanguageDetector),
            typeof(LanguageDetectorType),
            typeof(InputControl),
            new FrameworkPropertyMetadata(
                LanguageDetectorType.Local,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public bool HasTextPreprocessingRules
    {
        get => (bool)GetValue(HasTextPreprocessingRulesProperty);
        set => SetValue(HasTextPreprocessingRulesProperty, value);
    }

    public static readonly DependencyProperty HasTextPreprocessingRulesProperty =
        DependencyProperty.Register(
            nameof(HasTextPreprocessingRules), typeof(bool), typeof(InputControl), new PropertyMetadata(false));

    public int TextPreprocessingRuleCount
    {
        get => (int)GetValue(TextPreprocessingRuleCountProperty);
        set => SetValue(TextPreprocessingRuleCountProperty, value);
    }

    public static readonly DependencyProperty TextPreprocessingRuleCountProperty =
        DependencyProperty.Register(
            nameof(TextPreprocessingRuleCount), typeof(int), typeof(InputControl), new PropertyMetadata(0));

    public string TextPreprocessingRuleDetails
    {
        get => (string)GetValue(TextPreprocessingRuleDetailsProperty);
        set => SetValue(TextPreprocessingRuleDetailsProperty, value);
    }

    public static readonly DependencyProperty TextPreprocessingRuleDetailsProperty =
        DependencyProperty.Register(
            nameof(TextPreprocessingRuleDetails), typeof(string), typeof(InputControl), new PropertyMetadata(string.Empty));

    public bool IsIdentify
    {
        get => (bool)GetValue(IsIdentifyProperty);
        set => SetValue(IsIdentifyProperty, value);
    }

    public static readonly DependencyProperty IsIdentifyProperty =
        DependencyProperty.Register(
            nameof(IsIdentify),
            typeof(bool),
            typeof(InputControl),
            new PropertyMetadata(false));

    public bool TranslateOnPaste
    {
        get => (bool)GetValue(TranslateOnPasteProperty);
        set => SetValue(TranslateOnPasteProperty, value);
    }

    public static readonly DependencyProperty TranslateOnPasteProperty =
        DependencyProperty.Register(
            nameof(TranslateOnPaste),
            typeof(bool),
            typeof(InputControl),
            new PropertyMetadata(true));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(CornerRadius),
            typeof(InputControl),
            new PropertyMetadata(new CornerRadius(4)));

    /// <summary>
    /// 字体大小，用于读取和设置
    /// </summary>
    public double CurrentFontSize
    {
        get => (double)GetValue(CurrentFontSizeProperty);
        set => SetValue(CurrentFontSizeProperty, value);
    }

    public static readonly DependencyProperty CurrentFontSizeProperty =
        DependencyProperty.Register(
            nameof(CurrentFontSize),
            typeof(double),
            typeof(InputControl),
            new FrameworkPropertyMetadata(
                14.0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public ICommand? ExecuteCommand
    {
        get => (ICommand?)GetValue(ExecuteCommandProperty);
        set => SetValue(ExecuteCommandProperty, value);
    }

    public static readonly DependencyProperty ExecuteCommandProperty =
        DependencyProperty.Register(
            nameof(ExecuteCommand),
            typeof(ICommand),
            typeof(InputControl));

    public ICommand? ForceExecuteCommand
    {
        get => (ICommand?)GetValue(ForceExecuteCommandProperty);
        set => SetValue(ForceExecuteCommandProperty, value);
    }

    public static readonly DependencyProperty ForceExecuteCommandProperty =
        DependencyProperty.Register(
            nameof(ForceExecuteCommand),
            typeof(ICommand),
            typeof(InputControl));

    public ICommand? SaveToVocabularyCommand
    {
        get => (ICommand?)GetValue(SaveToVocabularyCommandProperty);
        set => SetValue(SaveToVocabularyCommandProperty, value);
    }

    public static readonly DependencyProperty SaveToVocabularyCommandProperty =
        DependencyProperty.Register(
            nameof(SaveToVocabularyCommand),
            typeof(ICommand),
            typeof(InputControl));

    public bool HasActivedVocabulary
    {
        get => (bool)GetValue(HasActivedVocabularyProperty);
        set => SetValue(HasActivedVocabularyProperty, value);
    }

    public static readonly DependencyProperty HasActivedVocabularyProperty =
        DependencyProperty.Register(
            nameof(HasActivedVocabulary),
            typeof(bool),
            typeof(InputControl));

    public ICommand? PlayAudioCommand
    {
        get => (ICommand?)GetValue(PlayAudioCommandProperty);
        set => SetValue(PlayAudioCommandProperty, value);
    }

    public static readonly DependencyProperty PlayAudioCommandProperty =
        DependencyProperty.Register(
            nameof(PlayAudioCommand),
            typeof(ICommand),
            typeof(InputControl));

    public ICommand? PlayAudioCancelCommand
    {
        get => (ICommand?)GetValue(PlayAudioCancelCommandProperty);
        set => SetValue(PlayAudioCancelCommandProperty, value);
    }

    public static readonly DependencyProperty PlayAudioCancelCommandProperty =
        DependencyProperty.Register(
            nameof(PlayAudioCancelCommand),
            typeof(ICommand),
            typeof(InputControl));

    public ICommand? CopyCommand
    {
        get => (ICommand?)GetValue(CopyCommandProperty);
        set => SetValue(CopyCommandProperty, value);
    }

    public static readonly DependencyProperty CopyCommandProperty =
        DependencyProperty.Register(
            nameof(CopyCommand),
            typeof(ICommand),
            typeof(InputControl));

    public ICommand? RemoveLineBreaksCommand
    {
        get => (ICommand?)GetValue(RemoveLineBreaksCommandProperty);
        set => SetValue(RemoveLineBreaksCommandProperty, value);
    }

    public static readonly DependencyProperty RemoveLineBreaksCommandProperty =
        DependencyProperty.Register(
            nameof(RemoveLineBreaksCommand),
            typeof(ICommand),
            typeof(InputControl));

    public ICommand? RemoveSpacesCommand
    {
        get => (ICommand?)GetValue(RemoveSpacesCommandProperty);
        set => SetValue(RemoveSpacesCommandProperty, value);
    }

    public static readonly DependencyProperty RemoveSpacesCommandProperty =
        DependencyProperty.Register(
            nameof(RemoveSpacesCommand),
            typeof(ICommand),
            typeof(InputControl));

    public ICommand? SelectIdentifiedLanguageCommand
    {
        get => (ICommand?)GetValue(SelectIdentifiedLanguageCommandProperty);
        set => SetValue(SelectIdentifiedLanguageCommandProperty, value);
    }

    public static readonly DependencyProperty SelectIdentifiedLanguageCommandProperty =
        DependencyProperty.Register(
            nameof(SelectIdentifiedLanguageCommand),
            typeof(ICommand),
            typeof(InputControl));

    public ICommand? SelectLanguageDetectorCommand
    {
        get => (ICommand?)GetValue(SelectLanguageDetectorCommandProperty);
        set => SetValue(SelectLanguageDetectorCommandProperty, value);
    }

    public static readonly DependencyProperty SelectLanguageDetectorCommandProperty =
        DependencyProperty.Register(
            nameof(SelectLanguageDetectorCommand),
            typeof(ICommand),
            typeof(InputControl));

    private TextBox? _textBox;
    private Border? _fontSizeHintBorder;
    private TextBlock? _fontSizeText;
    private ComboBox? _identifiedLanguageComboBox;
    private ComboBox? _languageDetectorComboBox;
    private CommandBinding? _pasteBinding;

    public override void OnApplyTemplate()
    {
        // 模板重建时先解绑旧模板事件，避免重复订阅
        if (_textBox != null)
        {
            _textBox.PreviewMouseWheel -= OnTextBoxPreviewMouseWheel;
            if (_pasteBinding != null)
            {
                _textBox.CommandBindings.Remove(_pasteBinding);
            }
        }

        if (_identifiedLanguageComboBox != null)
            _identifiedLanguageComboBox.SelectionChanged -= OnIdentifiedLanguageComboBoxSelectionChanged;

        if (_languageDetectorComboBox != null)
            _languageDetectorComboBox.SelectionChanged -= OnLanguageDetectorComboBoxSelectionChanged;

        base.OnApplyTemplate();

        _textBox = GetTemplateChild(PartTextBoxName) as TextBox;
        _fontSizeHintBorder = GetTemplateChild(PartFontSizeHintBorderName) as Border;
        _fontSizeText = GetTemplateChild(PartFontSizeTextName) as TextBlock;
        _identifiedLanguageComboBox = GetTemplateChild(PartIdentifiedLanguageComboBoxName) as ComboBox;
        _languageDetectorComboBox = GetTemplateChild(PartLanguageDetectorComboBoxName) as ComboBox;

        // 绑定粘贴命令
        if (_textBox != null)
        {
            _pasteBinding = new CommandBinding(ApplicationCommands.Paste, OnPasteExecuted);
            _textBox.CommandBindings.Add(_pasteBinding);

            // 添加鼠标滚轮事件处理
            _textBox.PreviewMouseWheel += OnTextBoxPreviewMouseWheel;
        }

        if (_identifiedLanguageComboBox != null)
            _identifiedLanguageComboBox.SelectionChanged += OnIdentifiedLanguageComboBoxSelectionChanged;

        if (_languageDetectorComboBox != null)
            _languageDetectorComboBox.SelectionChanged += OnLanguageDetectorComboBoxSelectionChanged;

    }

    /// <summary>
    /// 处理 TextBox 的鼠标滚轮事件，实现 Ctrl+鼠标滚轮调节字体大小
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void OnTextBoxPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 检查是否按下了 Ctrl 键
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            try
            {
                // 获取当前字体大小
                var currentFontSize = CurrentFontSize;

                // 根据滚轮方向调整字体大小
                var delta = e.Delta > 0 ? 1 : -1;
                var newFontSize = currentFontSize + delta;

                // 限制字体大小范围（10-20，与设置页面的滑块范围一致）
                newFontSize = Math.Max(10, Math.Min(20, newFontSize));

                // 更新字体大小
                if (Math.Abs(newFontSize - currentFontSize) > 0.01)
                {
                    CurrentFontSize = newFontSize;
                    ShowFontSizeHint();
                }

                // 标记事件已处理，防止页面滚动
                e.Handled = true;
            }
            catch
            {
                // 如果出现异常，不处理事件，让默认行为继续
                e.Handled = false;
            }
        }
    }

    /// <summary>
    /// 显示字体大小调节提示
    /// </summary>
    private void ShowFontSizeHint()
    {
        if (_fontSizeHintBorder == null)
            return;

        // 停止所有正在进行的 Opacity 动画，避免冲突
        _fontSizeHintBorder.BeginAnimation(UIElement.OpacityProperty, null);

        // 设置为可见并完全不透明
        _fontSizeHintBorder.Visibility = Visibility.Visible;
        _fontSizeHintBorder.Opacity = 1.0;

        // 创建淡出动画
        var duration = TimeSpan.FromMilliseconds(FontSizeHintAnimationDurationMs);
        var fadeOutAnimation = new DoubleAnimation(1.0, 0.0, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            BeginTime = TimeSpan.FromMilliseconds(200)
        };

        // 动画完成后隐藏 - 使用弱事件处理避免内存泄漏
        EventHandler? completedHandler = null;
        completedHandler = (s, e) =>
        {
            // 确保只有当前动画完成才隐藏元素
            if (s == fadeOutAnimation && _fontSizeHintBorder != null)
            {
                _fontSizeHintBorder.Visibility = Visibility.Collapsed;
            }

            // 取消事件订阅
            if (fadeOutAnimation != null)
            {
                fadeOutAnimation.Completed -= completedHandler;
            }
        };

        fadeOutAnimation.Completed += completedHandler;
        _fontSizeHintBorder.BeginAnimation(UIElement.OpacityProperty, fadeOutAnimation);
    }

    /// <summary>
    /// 处理粘贴命令执行事件
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void OnPasteExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (_textBox != null && Clipboard.ContainsText())
        {
            try
            {
                // 获取剪贴板文本内容
                var clipboardText = Clipboard.GetText();

                if (!string.IsNullOrEmpty(clipboardText))
                {
                    // 获取当前光标位置
                    var caretIndex = _textBox.CaretIndex;
                    var currentText = Text ?? string.Empty;

                    // 如果有选中的文本，先删除选中的内容
                    if (_textBox.SelectionLength > 0)
                    {
                        var selectionStart = _textBox.SelectionStart;
                        var selectionLength = _textBox.SelectionLength;
                        currentText = currentText.Remove(selectionStart, selectionLength);
                        caretIndex = selectionStart;
                    }

                    // 在光标位置插入剪贴板文本
                    var newText = currentText.Insert(caretIndex, clipboardText);

                    // 更新 Text 属性
                    Text = newText;

                    // 设置新的光标位置（粘贴文本的末尾）
                    var newCaretIndex = caretIndex + clipboardText.Length;

                    // 使用 Dispatcher 确保在下一个 UI 周期执行，以确保文本更新完成
                    Dispatcher.BeginInvoke(() =>
                    {
                        _textBox?.CaretIndex = newCaretIndex;
                    });

                    // 如果有绑定的命令，执行它
                    if (TranslateOnPaste && ExecuteCommand?.CanExecute(null) == true)
                    {
                        ExecuteCommand.Execute(null);
                    }
                }

                // 标记事件已处理
                e.Handled = true;
            }
            catch
            {
                // 如果自定义粘贴逻辑失败，让默认行为处理
                e.Handled = false;
            }
        }
    }

    /// <summary>
    /// 重写焦点获取方法，将焦点转发到 TextBox
    /// </summary>
    /// <param name="e"></param>
    protected override void OnGotFocus(RoutedEventArgs e)
    {
        base.OnGotFocus(e);

        if (_textBox != null && !_textBox.IsFocused)
        {
            _textBox.Focus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 重写鼠标点击事件，确保点击 InputControl 时 TextBox 获得焦点
    /// </summary>
    /// <param name="e"></param>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (IsClickInsideLanguageSelector(e.OriginalSource))
            return;

        if (_textBox != null && !_textBox.IsFocused)
        {
            _textBox.Focus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 重写 Focus 方法
    /// </summary>
    /// <returns></returns>
    public new bool Focus()
    {
        if (_textBox != null)
        {
            return _textBox.Focus();
        }
        return base.Focus();
    }

    /// <summary>
    /// 选择所有文本
    /// </summary>
    public void SelectAll() => _textBox?.SelectAll();

    /// <summary>
    /// 设置光标位置
    /// </summary>
    /// <param name="index"></param>
    public void SetCaretIndex(int index) => _textBox?.CaretIndex = index;

    private void OnIdentifiedLanguageComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_identifiedLanguageComboBox == null ||
            !_identifiedLanguageComboBox.IsDropDownOpen ||
            !CanSelectIdentifiedLanguage)
            return;

        if (_identifiedLanguageComboBox.SelectedValue is not LangEnum language || language == LangEnum.Auto)
            return;

        if (SelectIdentifiedLanguageCommand?.CanExecute(language) != true)
            return;

        SelectIdentifiedLanguageCommand.Execute(language);
        _identifiedLanguageComboBox.IsDropDownOpen = false;
    }

    private void OnLanguageDetectorComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_languageDetectorComboBox == null || !_languageDetectorComboBox.IsDropDownOpen)
            return;

        if (_languageDetectorComboBox.SelectedValue is not LanguageDetectorType detector)
            return;

        if (SelectLanguageDetectorCommand?.CanExecute(detector) != true)
            return;

        SelectLanguageDetectorCommand.Execute(detector);
        _languageDetectorComboBox.IsDropDownOpen = false;
    }

    private bool IsClickInsideLanguageSelector(object? source)
    {
        if (source is not DependencyObject dependencyObject)
            return false;

        return IsDescendantOf(dependencyObject, _identifiedLanguageComboBox) ||
               IsDescendantOf(dependencyObject, _languageDetectorComboBox);
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject? ancestor)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, ancestor))
                return true;

            source = source switch
            {
                Visual or Visual3D => VisualTreeHelper.GetParent(source),
                FrameworkContentElement contentElement => contentElement.Parent,
                _ => LogicalTreeHelper.GetParent(source)
            };
        }

        return false;
    }
}

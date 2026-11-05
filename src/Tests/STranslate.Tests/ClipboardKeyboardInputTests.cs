using STranslate.Helpers;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace STranslate.Tests;

public class ClipboardKeyboardInputTests
{
    private static readonly VIRTUAL_KEY[] Modifiers =
    [
        VIRTUAL_KEY.VK_LCONTROL, VIRTUAL_KEY.VK_RCONTROL,
        VIRTUAL_KEY.VK_LSHIFT, VIRTUAL_KEY.VK_RSHIFT,
        VIRTUAL_KEY.VK_LMENU, VIRTUAL_KEY.VK_RMENU,
        VIRTUAL_KEY.VK_LWIN, VIRTUAL_KEY.VK_RWIN
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopyAndPasteUseOneBatchWithOnlyCtrlAtTarget(bool isCopy)
    {
        for (var mask = 0; mask < 1 << Modifiers.Length; mask++)
        {
            var pressed = Modifiers.Where((_, index) => (mask & (1 << index)) != 0).ToHashSet();
            var calls = 0;
            var targetDowns = 0;
            var target = isCopy ? VIRTUAL_KEY.VK_C : VIRTUAL_KEY.VK_V;

            ClipboardHelper.SendCtrlCV(isCopy, inputs =>
            {
                calls++;
                foreach (var input in inputs)
                {
                    Assert.Equal(INPUT_TYPE.INPUT_KEYBOARD, input.type);
                    var key = input.Anonymous.ki.wVk;
                    var up = input.Anonymous.ki.dwFlags.HasFlag(KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP);
                    if (key is VIRTUAL_KEY.VK_RCONTROL or VIRTUAL_KEY.VK_RMENU or VIRTUAL_KEY.VK_LWIN or VIRTUAL_KEY.VK_RWIN)
                        Assert.True(input.Anonymous.ki.dwFlags.HasFlag(KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY));

                    if (up && key is VIRTUAL_KEY.VK_MENU or VIRTUAL_KEY.VK_LMENU or VIRTUAL_KEY.VK_RMENU or VIRTUAL_KEY.VK_LWIN or VIRTUAL_KEY.VK_RWIN)
                        Assert.Contains(VIRTUAL_KEY.VK_LCONTROL, pressed);
                    if (!up && key == VIRTUAL_KEY.VK_CONTROL)
                    {
                        Assert.DoesNotContain(VIRTUAL_KEY.VK_LSHIFT, pressed);
                        Assert.DoesNotContain(VIRTUAL_KEY.VK_RSHIFT, pressed);
                    }
                    if (!up && key == target)
                    {
                        Assert.Equal(VIRTUAL_KEY.VK_LCONTROL, Assert.Single(pressed));
                        targetDowns++;
                    }
                    Apply(input, pressed);
                }
                return (uint)inputs.Length;
            });

            Assert.Equal(1, calls);
            Assert.Equal(1, targetDowns);
            Assert.Empty(pressed);
        }
    }

    public static IEnumerable<object[]> PartialSends()
    {
        foreach (var isCopy in new[] { true, false })
        for (var sent = 0; sent < 15; sent++)
            yield return [isCopy, sent];
    }

    [Theory]
    [MemberData(nameof(PartialSends))]
    public void PartialSendReleasesTargetAndCtrlAndReportsFailure(bool isCopy, int sent)
    {
        var pressed = new HashSet<VIRTUAL_KEY>();
        var calls = 0;
        Assert.Throws<InvalidOperationException>(() => ClipboardHelper.SendCtrlCV(isCopy, inputs =>
        {
            var count = ++calls == 1 ? sent : inputs.Length;
            foreach (var input in inputs.Take(count))
                Apply(input, pressed);
            return (uint)count;
        }));
        Assert.Equal(2, calls);
        Assert.Empty(pressed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CleanupFailurePreservesOriginalException(bool isCopy)
    {
        var error = new InvalidOperationException("原始发送异常");
        var calls = 0;
        var actual = Assert.Throws<InvalidOperationException>(() => ClipboardHelper.SendCtrlCV(isCopy, _ =>
        {
            if (++calls == 1)
                throw error;
            throw new InvalidOperationException("清理异常");
        }));
        Assert.Same(error, actual);
        Assert.Equal(2, calls);
    }

    private static void Apply(INPUT input, HashSet<VIRTUAL_KEY> pressed)
    {
        var key = input.Anonymous.ki.wVk switch
        {
            VIRTUAL_KEY.VK_CONTROL => VIRTUAL_KEY.VK_LCONTROL,
            VIRTUAL_KEY.VK_SHIFT => VIRTUAL_KEY.VK_LSHIFT,
            VIRTUAL_KEY.VK_MENU => VIRTUAL_KEY.VK_LMENU,
            var value => value
        };
        if (input.Anonymous.ki.dwFlags.HasFlag(KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP))
            pressed.Remove(key);
        else
            pressed.Add(key);
    }
}
